import argparse,json,math
from pathlib import Path
from optimization_native import Session,ROOT,save
parser=argparse.ArgumentParser();parser.add_argument('--out',type=Path,required=True);args=parser.parse_args()
out=args.out.resolve();out.mkdir(parents=True,exist_ok=False);session=Session(ROOT/'plugins/auto-solidworks',out/'trace')
try:
    recipes=[
        ('flange',dict(kind='Flange',outer_diameter_mm=120,inner_diameter_mm=60,thickness_mm=12,hole_diameter_mm=9,pitch_circle_diameter_mm=90,hole_count=6,start_angle_degrees=0)),
        ('plate',dict(kind='HolePlate',width_mm=80,height_mm=60,thickness_mm=12,hole_diameter_mm=9,hole_centers=[dict(xmm=-20,ymm=10),dict(xmm=20,ymm=-10)])),
        ('blind',dict(kind='HoleFamily',width_mm=80,height_mm=60,thickness_mm=12,hole_diameter_mm=9,hole_kind='Blind',depth_mm=7,hole_centers=[dict(xmm=15,ymm=-10)])),
        ('counterbore',dict(kind='HoleFamily',width_mm=80,height_mm=60,thickness_mm=12,hole_diameter_mm=6,hole_kind='Counterbore',counterbore_diameter_mm=12,counterbore_depth_mm=4,hole_centers=[dict(xmm=-15,ymm=10),dict(xmm=15,ymm=-10)]))]
    for name,recipe in recipes:
        native=out/(name+'.sldprt')
        plan=session.call('cad_create_model_plan',dict(nativeOutputPath=str(native),draft=dict(source_text=json.dumps(recipe),operations=[],recipe=recipe),responseDetail='full'));save(out,name+'-plan',plan);assert plan['success'],plan
        result=session.call('cad_build_model',dict(planId=plan['plan_id'],responseDetail='full'));save(out,name+'-build',result)
        print(json.dumps(dict(case=name,success=result['success'],message=result['message']),ensure_ascii=False),flush=True);assert result['success'],result
        v=json.loads(plan['ir_json'])['verification']
        probes=[]
        if name=='flange':
            radius=json.loads(json.dumps(v));radius['require_whole_model_inventory']=False;radius['whole_model_checks']=[]
            radius['cylinder_groups'][-1]['axis_starts_mm']=[dict(x=46*math.cos(i*math.pi/3),y=46*math.sin(i*math.pi/3),z=0) for i in range(6)]
            count=json.loads(json.dumps(v));count['require_whole_model_inventory']=False;count['whole_model_checks']=[]
            count['cylinder_groups'][-1]['axis_starts_mm']=[dict(x=45*math.cos(i*2*math.pi/7),y=45*math.sin(i*2*math.pi/7),z=0) for i in range(7)]
            probes=[dict(probe_id='pcd90-to92',dimension_name='分度圆半径@安装孔定位',feature_name='安装孔定位',trial_value=46,unit='Millimeter',trial_verification=radius,restore_verification=v),
                dict(probe_id='holes6-to7',dimension_name='孔数@孔圆周阵列',feature_name='孔圆周阵列',trial_value=7,unit='Unitless',trial_verification=count,restore_verification=v)]
        inspected=session.call('cad_inspect_model',dict(inputPath=str(native),verification=v,editabilityProbes=probes,requireEditableSketches=True,responseDetail='full'));save(out,name+'-inspection',inspected)
        print(json.dumps(dict(case=name,inspection_success=inspected['success'],parametric_status=inspected.get('parametric_status'),probes=inspected.get('editability_probes')),ensure_ascii=False),flush=True)
        assert inspected['success'] and inspected['parametric_status']=='passed',inspected
finally:
    (out/'mcp.log').write_text(''.join(session.logs),encoding='utf-8');session.close()
