"""Check chamfer parameter routing with independently calculated removed volume and face area."""
import argparse, datetime, hashlib, json, math
from pathlib import Path
from focused_three import Client, ROOT, base, feature, v
from local_geometry import surface

def run(package, output):
    output.mkdir(parents=True)
    client=Client(package,output); results={}
    def save(name, value):
        (output/(name+'.json')).write_text(json.dumps(value,indent=2,ensure_ascii=False),encoding='utf-8')
    try:
        save('tools',client.rpc('tools/list',{}))
        save('runtime',dict(package=str(package),executor_sha256=hashlib.sha256((package/'runtime/executor/CadModeling.Executor.SolidWorks.dll').read_bytes()).hexdigest()))
        for mode,a,b,angle in [('EqualDistance',1,1,45),('TwoDistances',2,3,45),('DistanceAngle',2,2*math.tan(math.radians(30)),30)]:
            d=base(); d['recovery']=dict(enabled=False)
            d['operations'].append(feature('bevel','Chamfer',chamfer_mode=mode,distance_mm=a,second_distance_mm=b,angle_degrees=angle,
                selections=[dict(kind='Edge',geometry='Line',position_mm=v(40,25,5))]))
            # A single right-triangle prism is removed, irrespective of which adjacent face is side 1.
            # Use the chamfer center's candidate coordinates in separate inspections below.
            d.update(expected_volume_mm3=40000-a*b*5,volume_tolerance_percent=.0001)
            path=output/(mode+'_'+output.name+'.SLDPRT')
            plan=client.call('cad_create_model_plan',draft=d,nativeOutputPath=str(path),exportPaths=[str(path.with_suffix('.STEP'))]);save(mode+'-plan',plan)
            assert plan['success'],plan
            built=client.call('cad_build_model',irJson=plan['ir_json']);save(mode,built)
            assert built['success'],built
            expected_volume=40000-a*b*5
            assert abs(built['geometry']['volume_mm3']-expected_volume)<.001,(mode,built['geometry'],expected_volume)
            results[mode+'_removed_volume']=True;save('results',results)
            for ext in ['.SLDPRT','.STEP']:
                file=path.with_suffix(ext); before=hashlib.sha256(file.read_bytes()).hexdigest()
                checks=[]
                for dx,dy in [(a,b),(b,a)]:
                    spec=dict(surface_samples=[surface('bevel_face','Plane',[v(40-dx/2,25-dy/2,5)],[v(dy,dx)],expected_area_mm2=10*math.hypot(a,b))])
                    r=client.call('cad_inspect_model',inputPath=str(file),verification=spec);checks.append(r)
                    if r['success']:break
                save(mode+ext+'-inspection',checks)
                assert any(r['success'] and r['verification']['passed'] for r in checks),(mode,ext,checks)
                assert hashlib.sha256(file.read_bytes()).hexdigest()==before
                results[mode+ext+'_geometry_and_unchanged']=True;save('results',results)
            print('PASS '+mode,flush=True)
        return results
    finally:client.close()

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--package',type=Path,default=ROOT/'plugins/auto-solidworks');p.add_argument('--output',type=Path)
    a=p.parse_args();out=a.output or ROOT/'artifacts'/('chamfer-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
    print(out,flush=True);run(a.package.resolve(),out.resolve())
