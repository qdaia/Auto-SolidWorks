"""Actual diameter/thickness geometry changes plus negative cases on the final runtime."""
import argparse,hashlib,json,math
from pathlib import Path
from optimization_native import Session,ROOT,save
parser=argparse.ArgumentParser();parser.add_argument('--verification',type=Path,required=True);args=parser.parse_args()
base=args.verification.resolve();out=base/'extended-probes';out.mkdir(exist_ok=False)
source=base/'final-readback/recipe-flange';request=json.loads((source/'request.json').read_text(encoding='utf-8'));v=request['verification']
session=Session(ROOT/'plugins/auto-solidworks',out/'trace')
try:
    diameter=json.loads(json.dumps(v));diameter['cylinder_groups'][-1]['diameter_mm']=10
    thickness=json.loads(json.dumps(v))
    for group in thickness['cylinder_groups']:group['length_mm']=14
    for verification,hd,t in ((diameter,10,12),(thickness,9,14)):
        inventory=verification['whole_model_checks'][0]
        area=math.pi*(60**2-30**2-6*(hd/2)**2)
        inventory['volume_mm3']=area*t
        inventory['surface_area_mm2']=2*area+math.pi*(120+60+6*hd)*t
    request['editabilityProbes'] += [dict(probe_id='hole-diameter9-to10',dimension_name='直径@安装孔定位',feature_name='安装孔定位',unit='Millimeter',trial_value=10,trial_verification=diameter,restore_verification=v),
        dict(probe_id='thickness12-to14',dimension_name='D1@法兰主体',feature_name='法兰主体',unit='Millimeter',trial_value=14,trial_verification=thickness,restore_verification=v)]
    save(out,'request',request);result=session.call('cad_inspect_model',request);save(out,'inspection',result)
    assert result['success'] and all(p['passed'] and p['trial_geometry_passed'] and p['restore_geometry_passed'] and p['model_file_unchanged'] for p in result['editability_probes']),result
    print(json.dumps(dict(case='four-flange-geometry-probes',passed=True,probes=[p['probe_id'] for p in result['editability_probes']])),flush=True)
    bad=json.loads((base/'matrix-final/wrong-geometry-request.json').read_text(encoding='utf-8'))
    negative=session.call('cad_inspect_model',bad);save(out,'wrong-geometry',negative)
    assert not negative['success'] and not negative['editability_probes'][0]['trial_geometry_passed'] and negative['editability_probes'][0]['restore_geometry_passed'] and negative['editability_probes'][0]['model_file_unchanged'],negative
    old=base/'matrix-final/old-frozen-copy.sldprt';before=hashlib.sha256(old.read_bytes()).hexdigest()
    negative=session.call('cad_inspect_model',dict(inputPath=str(old),requireEditableSketches=True,responseDetail='full'));save(out,'old-fixed-centers',negative)
    assert not negative['success'] and negative['geometry_status']=='passed' and negative['parametric_status']=='review_required' and hashlib.sha256(old.read_bytes()).hexdigest()==before,negative
    save(out,'summary',dict(success=True,flange_probes=4,negative_cases=2))
finally:
    (out/'mcp.log').write_text(''.join(session.logs),encoding='utf-8');session.close()
