import argparse,hashlib,json,math
from pathlib import Path
from optimization_native import Session,ROOT,save
parser=argparse.ArgumentParser();parser.add_argument('--out',type=Path,required=True);args=parser.parse_args()
out=args.out.resolve();out.mkdir(parents=True,exist_ok=False);session=Session(ROOT/'plugins/auto-solidworks',out/'trace');records=[]
def group(start,direction,length,diameter=20,interior=False):
    return dict(id='wall',source_literal='explicit native regression geometry',diameter_mm=diameter,axis_starts_mm=[dict(zip(('x','y','z'),start))],
        direction=dict(zip(('x','y','z'),direction)),length_mm=length,interior=interior,exact_count=True,tolerance_mm=.02,direction_tolerance_degrees=.1,coverage='CompleteWall')
try:
    shapes=[('front','Front',False,None,(10,-15,0),(0,0,1),'Blind'),('front-reverse','Front',True,None,(10,-15,0),(0,0,-1),'Blind'),
        ('top','Top',False,None,(10,0,-15),(0,1,0),'Blind'),('right','Right',False,None,(0,-15,10),(1,0,0),'Blind'),
        ('midplane','Front',False,None,(10,-15,-4),(0,0,1),'MidPlane'),
        ('frame','Front',False,dict(origin_mm=dict(x=15,y=0,z=20),normal=dict(x=.6,y=0,z=.8),x_direction=dict(x=.8,y=0,z=-.6)),(23,-15,14),(.6,0,.8),'Blind')]
    for name,plane,reverse,frame,start,direction,end in shapes:
        s=dict(type='ProfileSketch',id='profile',name='圆轮廓',plane=plane,auto_dimension_primitives=True,require_fully_defined=True,require_editable=True,
            primitives=[dict(type='Circle',center_xmm=10,center_ymm=-15,diameter_mm=20)])
        if frame:s['frame']=frame
        v=dict(cylinder_groups=[group(start,direction,8)],require_whole_model_inventory=True,whole_model_checks=[dict(id='inventory',source_literal='closed cylinder with two planar end faces and one periodic cylindrical wall',
            solid_body_count=1,surface_body_count=0,face_count=3,edge_count=2,open_edge_count=0,surface_face_counts=dict(Plane=2,Cylinder=1),
            volume_mm3=math.pi*100*8,surface_area_mm2=2*math.pi*100+math.pi*20*8,area_tolerance_mm2=.2,volume_tolerance_mm3=.2)])
        draft=dict(source_text=name+' explicit circle20 at sketch coordinates(10,-15), length8; independent world axis specified',operations=[s,
            dict(type='ExtrudeBoss',id='boss',name='主体',sketch_id='profile',depth_mm=8,end_condition=end,reverse_direction=reverse)],verification=v)
        native=out/(name+'.sldprt');plan=session.call('cad_create_model_plan',dict(draft=draft,nativeOutputPath=str(native),responseDetail='full'));save(out,name+'-plan',plan);assert plan['success'],plan
        built=session.call('cad_build_model',dict(planId=plan['plan_id'],responseDetail='full'));save(out,name+'-build',built);assert built['success'],built
        inspected=session.call('cad_inspect_model',dict(inputPath=str(native),verification=v,requireEditableSketches=True,responseDetail='full'));save(out,name+'-inspection',inspected);assert inspected['success'] and inspected['parametric_status']=='passed',inspected
        records.append(dict(case=name,passed=True));print(json.dumps(records[-1]),flush=True)
        if name=='front':
            # A parameter readback cannot override a deliberately wrong target-geometry requirement.
            bad=group((999,-15,0),(0,0,1),8)
            spec=dict(probe_id='wrong-geometry',dimension_name='定位X_0@圆轮廓',feature_name='圆轮廓',unit='Millimeter',trial_value=11,
                trial_verification=dict(cylinder_groups=[bad]),restore_verification=v)
            request=dict(inputPath=str(native),verification=v,editabilityProbes=[spec],responseDetail='full')
            save(out,'wrong-geometry-request',request);negative=session.call('cad_inspect_model',request);save(out,'wrong-geometry',negative)
            p=negative['editability_probes'][0]
            assert not negative['success'] and not p['passed'] and p['trial_geometry_passed'] is False and p['restore_geometry_passed'] and p['model_file_unchanged'],negative
            records.append(dict(case='wrong-trial-geometry-rejected-and-restored',passed=True));print(json.dumps(records[-1]),flush=True)
    old=Path(r'C:\Users\csj17\Desktop\CODEX TEST\solidworks-real-comparison-20261013-183436\plugin-result\six-hole-flange.sldprt')
    old_copy=out/'old-frozen-copy.sldprt';old_copy.write_bytes(old.read_bytes())
    inspected=session.call('cad_inspect_model',dict(inputPath=str(old_copy),requireEditableSketches=True,responseDetail='full'));save(out,'old-frozen-inspection',inspected)
    assert not inspected['success'] and inspected['geometry_status']=='passed' and inspected['parametric_status']=='review_required',inspected
    assert hashlib.sha256(old_copy.read_bytes()).digest()==hashlib.sha256(old.read_bytes()).digest()
    records.append(dict(case='old-fixed-design-centers-rejected-with-file-preserved',passed=True));print(json.dumps(records[-1]),flush=True)
    save(out,'summary',records)
finally:
    (out/'mcp.log').write_text(''.join(session.logs),encoding='utf-8');session.close()
