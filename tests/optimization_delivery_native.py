"""Reinspect retained native artifacts with the delivery runtime, then exercise non-depth ends."""
import argparse,json,math
from pathlib import Path
from optimization_native import Session,ROOT,save

parser=argparse.ArgumentParser();parser.add_argument('--verification',type=Path,required=True);parser.add_argument('--readback-name',default='delivery-readback');args=parser.parse_args()
base=args.verification.resolve();out=base/args.readback_name;out.mkdir(exist_ok=False)
session=Session(ROOT/'plugins/auto-solidworks',out/'trace');records=[]
try:
    families=[('recipe-'+name,base/'recipes-final',name+'-plan.json',name+'-build.json',name+'-inspection.json') for name in ('flange','plate','blind','counterbore')]
    families+=[('independent-hole-positions',base/'parametric-final','plan.json','build.json','inspection.json')]
    families += [('direction-'+name,base/'matrix-final',name+'-plan.json',name+'-build.json',name+'-inspection.json') for name in ('front','front-reverse','top','right','midplane','frame')]
    for name,source,planfile,buildfile,inspectionfile in families:
        folder=out/name;folder.mkdir()
        plan=json.loads((source/planfile).read_text(encoding='utf-8'));ir=json.loads(plan['ir_json'])
        previous=json.loads((source/inspectionfile).read_text(encoding='utf-8'))
        model=folder/Path(previous['input_path']).name;model.write_bytes(Path(previous['input_path']).read_bytes())
        probes=[dict(probe_id=p['probe_id'],dimension_name=p['dimension_name'],feature_name=p['feature_name'],unit=p['unit'],trial_value=p['requested_trial_value']) for p in previous['editability_probes']]
        # Retain the actual source-derived geometry probe contracts, not just previous pass flags.
        verification=ir['verification']
        for probe in probes:
            trial=json.loads(json.dumps(verification))
            if probe['probe_id']=='move-hole-x':trial['cylinder_groups'][2]['axis_starts_mm'][0]['x']=46
            elif probe['probe_id']=='pcd90-to92':
                trial['require_whole_model_inventory']=False;trial['whole_model_checks']=[]
                trial['cylinder_groups'][-1]['axis_starts_mm']=[dict(x=46*math.cos(i*math.pi/3),y=46*math.sin(i*math.pi/3),z=0) for i in range(6)]
            elif probe['probe_id']=='holes6-to7':
                trial['require_whole_model_inventory']=False;trial['whole_model_checks']=[]
                trial['cylinder_groups'][-1]['axis_starts_mm']=[dict(x=45*math.cos(i*2*math.pi/7),y=45*math.sin(i*2*math.pi/7),z=0) for i in range(7)]
            else:raise AssertionError('Unknown geometry probe scope')
            probe.update(trial_verification=trial,restore_verification=verification)
        request=dict(inputPath=str(model),verification=verification,editabilityProbes=probes,requireEditableSketches=True,responseDetail='full')
        save(folder,'request',request);inspected=session.call('cad_inspect_model',request);save(folder,'inspection',inspected)
        assert inspected['success'] and inspected['parametric_status']=='passed',inspected
        (folder/'plan.json').write_bytes((source/planfile).read_bytes());(folder/'build.json').write_bytes((source/buildfile).read_bytes())
        records.append(dict(case=name,passed=True,inspection=str(folder/'inspection.json')));print(json.dumps(records[-1]),flush=True)
    # Blind/MidPlane remain depth-driven; these two end conditions intentionally omit depth.
    for end in ('UpToNext','UpToSurface'):
        folder=out/end;folder.mkdir();model=folder/(end+'.sldprt')
        operations=[dict(type='ProfileSketch',id='base',name='底板轮廓',plane='Front',auto_dimension_primitives=True,require_editable=True,require_fully_defined=True,
            primitives=[dict(type='CenteredRectangle',center_xmm=0,center_ymm=0,width_mm=80,height_mm=60)]),
            dict(type='ExtrudeBoss',id='boss',name='底板',sketch_id='base',depth_mm=12,end_condition='Blind'),
            dict(type='ProfileSketch',id='hole',name='孔定位',plane='Front',auto_dimension_primitives=True,require_editable=True,require_fully_defined=True,
                primitives=[dict(type='Circle',center_xmm=10,center_ymm=-15,diameter_mm=20)]),
            dict(type='ExtrudeCut',id='cut',name='孔切除',sketch_id='hole',end_condition=end)]
        if end=='UpToSurface':operations[-1]['end_reference']=dict(support_operation_id='boss',pick_xmm=30,pick_ymm=20,pick_zmm=12)
        verification=dict(require_whole_model_inventory=True,cylinder_groups=[dict(id='hole',source_literal='20mm hole at (10,-15), plate thickness12',diameter_mm=20,
            axis_starts_mm=[dict(x=10,y=-15,z=0)],direction=dict(x=0,y=0,z=1),length_mm=12,interior=True,exact_count=True,tolerance_mm=.02,direction_tolerance_degrees=.1,coverage='CompleteWall')],
            whole_model_checks=[dict(id='inventory',source_literal='80x60x12 plate minus one complete20mm cylinder',solid_body_count=1,surface_body_count=0,face_count=7,edge_count=14,
                open_edge_count=0,surface_face_counts=dict(Plane=6,Cylinder=1),volume_mm3=80*60*12-math.pi*100*12,
                surface_area_mm2=2*80*60+2*(80+60)*12-2*math.pi*100+math.pi*20*12,volume_tolerance_mm3=.2,area_tolerance_mm2=.2)])
        draft=dict(source_text=end+' to opposite plate surface, no nominal depth supplied',operations=operations,verification=verification)
        save(folder,'draft',draft);plan=session.call('cad_create_model_plan',dict(draft=draft,nativeOutputPath=str(model),responseDetail='full'));save(folder,'plan',plan);assert plan['success'],plan
        built=session.call('cad_build_model',dict(planId=plan['plan_id'],responseDetail='full'));save(folder,'build',built);assert built['success'],built
        request=dict(inputPath=str(model),verification=verification,requireEditableSketches=True,responseDetail='full')
        save(folder,'request',request);inspected=session.call('cad_inspect_model',request);save(folder,'inspection',inspected);assert inspected['success'],inspected
        records.append(dict(case=end,passed=True,inspection=str(folder/'inspection.json')));print(json.dumps(records[-1]),flush=True)
    save(out,'summary',records)
finally:
    (out/'mcp.log').write_text(''.join(session.logs),encoding='utf-8');session.close()
