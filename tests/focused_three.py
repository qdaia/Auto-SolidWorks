"""Bounded tests of binding coverage, measured verification and checkpoint recovery.

This is not an engineering-drawing recognition benchmark. --native creates only
small test parts in a fresh artifact directory through the public MCP interface.
"""
import argparse, copy, datetime, hashlib, json, queue, subprocess, threading
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
def v(x=0,y=0,z=0): return dict(x=x,y=y,z=z)
def sketch(id,primitives): return dict(type='ProfileSketch',id=id,name=id,plane='Front',primitives=primitives)
def feature(id,kind,**kw): return dict(type='NativeFeature',id=id,name=id,feature=dict(kind=kind,**kw))
def base():
    return dict(name='focused_plate',source_text='Contract fixture: 80 x 50 x 10 mm plate.',operations=[
        sketch('base',[dict(type='CenteredRectangle',center_xmm=0,center_ymm=0,width_mm=80,height_mm=50)]),
        dict(type='ExtrudeBoss',id='block',name='block',sketch_id='base',depth_mm=10,end_condition='Blind')])
def drawing_case():
    d=base()
    facts=[dict(id=id,operation_id=op,parameter_path=path,value=value,source_literal=literal,view_ids=['front']) for id,op,path,value,literal in [
        ('width','base','primitives.0.width_mm',80,'80'),('height','base','primitives.0.height_mm',50,'50'),('depth','block','depth_mm',10,'10')]]
    d['drawing_context']=dict(source_path=str(ROOT/'tests/fixtures/annotation-drawing.png'),views=[dict(id='front',kind='Front')],dimensions=facts,
        features=[dict(id='plate',source_literal='80 x 50 x 10',view_ids=['front'],operation_ids=['base','block'],
            critical_parameters=[dict(operation_id=f['operation_id'],parameter_path=f['parameter_path'],dimension_id=f['id']) for f in facts],verification_check_ids=['bounds'])])
    d['verification']=dict(bounds=[dict(id='bounds',source_literal='80 x 50 x 10',source_dimension_ids=['width','height','depth'],size_mm=v(80,50,10))],
        bindings=[dict(dimension_id=id,check_id='bounds',parameter_path='size_mm.'+axis) for id,axis in [('width','x'),('height','y'),('depth','z')]])
    return d
def drilled_case():
    d=base()
    d['operations'].append(feature('holes','Hole',diameter_mm=10,hole_centers=[dict(xmm=-20,ymm=0),dict(xmm=20,ymm=0)],
        frame=dict(origin_mm=v(0,0,10),x_direction=v(1),normal=v(0,0,-1))))
    d['verification']=dict(cylinder_groups=[dict(id='holes_check',source_literal='Two diameter 10 through cylindrical walls at x=-20,+20; z=0..10.',
        diameter_mm=10,axis_starts_mm=[v(-20),v(20)],direction=v(0,0,1),length_mm=10)],
        bounds=[dict(id='bounds',source_literal='80 x 50 x 10',size_mm=v(80,50,10))],
        native_dimensions=[dict(id='depth',source_literal='Plate thickness 10',dimension_name='D1@block',value=10)])
    return d

class Client:
    def __init__(self,package,output):
        self.output=output; self.counter=0; self.responses=queue.Queue();self.log=[]
        self.p=subprocess.Popen(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(package/'scripts/run-auto-solidworks-mcp.ps1')],
            cwd=package,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',errors='replace',creationflags=0x08000000)
        def read():
            for line in self.p.stdout:
                try:self.responses.put(json.loads(line))
                except ValueError:self.log.append(line)
            self.responses.put(dict(eof=True))
        def errors():
            for line in self.p.stderr:self.log.append(line)
        threading.Thread(target=read,daemon=True).start();threading.Thread(target=errors,daemon=True).start()
        self.rpc('initialize',dict(protocolVersion='2024-11-05',capabilities={},clientInfo=dict(name='focused-three',version='1')))
        self.p.stdin.write(json.dumps(dict(jsonrpc='2.0',method='notifications/initialized'))+'\n');self.p.stdin.flush()
    def rpc(self,method,params):
        self.counter+=1;self.p.stdin.write(json.dumps(dict(jsonrpc='2.0',id=self.counter,method=method,params=params))+'\n');self.p.stdin.flush()
        while True:
            r=self.responses.get(timeout=180)
            if r.get('eof'):raise RuntimeError(''.join(self.log)[-2000:])
            if r.get('id')==self.counter:
                if 'error' in r:raise RuntimeError(r['error'])
                return r['result']
    def call(self,name,**args):
        r=self.rpc('tools/call',dict(name=name,arguments=args))
        if r.get('isError'):raise RuntimeError(r)
        c=r.get('structuredContent') or json.loads(r['content'][0]['text'])
        return c['result'] if list(c)==['result'] else c
    def close(self):
        self.p.stdin.close()
        try:self.p.wait(timeout=15)
        except subprocess.TimeoutExpired:self.p.terminate()
        (self.output/'stderr.log').write_text(''.join(self.log),encoding='utf-8')

def run(package,output,native=False):
    output.mkdir(parents=True);client=Client(package,output);results={}
    def save(name,data): (output/(name+'.json')).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    def passed(name):results[name]=True;save('results',results);print('PASS '+name,flush=True)
    def compile(d,name):
        native_path=output/(name+'_'+output.name+'.SLDPRT')
        r=client.call('cad_create_model_plan',draft=d,nativeOutputPath=str(native_path),exportPaths=[str(native_path.with_suffix('.STEP'))] if name=='measured_good' else []);save(name+'-plan',r);return r
    def reject(name,mutate):
        d=drawing_case();mutate(d);r=compile(d,name);assert not r['success'],name;passed(name)
    def build(d,name):
        d=copy.deepcopy(d);d.setdefault('recovery',{})['directory']=str(output/'checkpoints')
        r=compile(d,name);assert r['success'],r
        result=client.call('cad_build_model',irJson=r['ir_json']);save(name,result);return result
    try:
        save('runtime',dict(package=str(package),executor_sha256=hashlib.sha256((package/'runtime/executor/CadModeling.Executor.SolidWorks.dll').read_bytes()).hexdigest()))
        save('tools',client.rpc('tools/list',{}))
        plan=compile(drawing_case(),'binding_valid');assert plan['success'],plan;passed('complete_binding_compiles')
        reject('missing_feature_inventory',lambda d:d['drawing_context'].update(features=[]))
        reject('missing_operation_coverage',lambda d:d['drawing_context']['features'][0].update(operation_ids=['base']))
        reject('missing_critical_dimension',lambda d:d['drawing_context']['dimensions'].pop())
        reject('assumed_critical_dimension',lambda d:d['drawing_context']['dimensions'][0].update(status='Assumed'))
        reject('missing_check',lambda d:d['drawing_context']['features'][0].update(verification_check_ids=[]))
        reject('wrong_expected_value',lambda d:d['verification']['bounds'][0]['size_mm'].update(x=70))
        reject('missing_expected_binding',lambda d:d['verification']['bindings'].pop())
        reject('missing_critical_parameter',lambda d:d['drawing_context']['features'][0]['critical_parameters'].pop())
        reject('duplicate_source_dimension',lambda d:d['drawing_context']['dimensions'].append(copy.deepcopy(d['drawing_context']['dimensions'][0])))
        ir=json.loads(plan['ir_json']);ir['operations'][1]['depth_mm']=11
        changed=client.call('cad_build_model',irJson=json.dumps(ir),dryRun=True)
        assert not changed['success'] and any(e.get('code')=='DRAWING_COMPILED_CHANGED' for e in changed['evidence']),changed;passed('changed_ir_rejected')
        dry=client.call('cad_build_model',irJson=plan['ir_json'],dryRun=True);assert dry['success'],dry;passed('unchanged_drawing_dryrun')
        source_copy=output/'source_copy.png';source_copy.write_bytes((ROOT/'tests/fixtures/annotation-drawing.png').read_bytes())
        source_draft=drawing_case();source_draft['drawing_context']['source_path']=str(source_copy)
        source_plan=compile(source_draft,'source_hash');assert source_plan['success'],source_plan
        source_copy.write_bytes(source_copy.read_bytes()+b'changed fixture')
        changed=client.call('cad_build_model',irJson=source_plan['ir_json'],dryRun=True)
        assert not changed['success'] and any(e.get('code')=='DRAWING_SOURCE_CHANGED' for e in changed['evidence']),changed;passed('changed_source_drawing_rejected')
        simple=client.call('cad_create_model_plan',text='80 x 50 x 10 mm plate',nativeOutputPath=str(output/'text.SLDPRT'))
        assert simple['success'];passed('text_path_preserved')
        if not native:return results
        good=build(drilled_case(),'measured_good');assert good['success'],good;assert good['verification']['passed'];passed('native_saved_measurements')
        assert good['recovery']['manifest_path'],good;passed('native_checkpoint_reopened')
        model=Path(good['native_path']);original=hashlib.sha256(model.read_bytes()).hexdigest()
        step=model.with_suffix('.STEP');step_hash=hashlib.sha256(step.read_bytes()).hexdigest()
        step_spec=copy.deepcopy(drilled_case()['verification']);step_spec.pop('native_dimensions')
        step_check=client.call('cad_inspect_model',inputPath=str(step),verification=step_spec);save('step_reimport',step_check)
        assert step_check['success'] and step_check['verification']['passed'],step_check
        assert abs(step_check['geometry']['volume_mm3']/good['geometry']['volume_mm3']-1)<1e-6
        assert hashlib.sha256(step.read_bytes()).hexdigest()==step_hash;passed('step_reimport_source_measurements')
        for name,mutate in [
            ('wrong_hole_position',lambda s:s['cylinder_groups'][0]['axis_starts_mm'][0].update(x=-15)),
            ('wrong_hole_depth',lambda s:s['cylinder_groups'][0].update(length_mm=8)),
            ('wrong_hole_count',lambda s:s['cylinder_groups'][0]['axis_starts_mm'].pop()),
            ('wrong_hole_direction',lambda s:s['cylinder_groups'][0].update(direction=v(0,1,0))),
            ('wrong_hole_diameter',lambda s:s['cylinder_groups'][0].update(diameter_mm=9)),
            ('wrong_native_dimension',lambda s:s['native_dimensions'][0].update(value=8))]:
            spec=copy.deepcopy(drilled_case()['verification']);mutate(spec)
            inspected=client.call('cad_inspect_model',inputPath=str(model),verification=spec);save(name,inspected)
            assert not inspected['success'] and not inspected['verification']['passed'],inspected;passed(name+'_rejected')
        assert hashlib.sha256(model.read_bytes()).hexdigest()==original;passed('inspection_preserves_native_file')
        wrong=drilled_case();wrong['operations'][2]['feature']['hole_centers'][0]['xmm']=-15
        rejected=build(wrong,'wrong_geometry_build');assert not rejected['success'] and rejected['native_path'] is None,rejected;passed('wrong_geometry_not_delivered')
        failing=drilled_case();failing['operations'].append(feature('detail','Fillet',radius_mm=1,
            selections=[dict(kind='Edge',geometry='Circle',radius_mm=999,all_matches=True)]))
        failed=build(failing,'recover_failure');assert not failed['success'],failed
        assert failed['recovery']['can_resume'] and failed['recovery']['failed_operation_id']=='detail',failed;passed('failure_reports_operation_and_checkpoint')
        checkpoint=failed['recovery']['manifest_path'];cp=Path(failed['recovery']['checkpoint_path']);cp_hash=hashlib.sha256(cp.read_bytes()).hexdigest()
        corrected=copy.deepcopy(failing);corrected['operations'][-1]['feature']['selections']=[dict(kind='Edge',geometry='Line',direction=v(0,0,1),all_matches=True)]
        corrected['recovery']=dict(resume_manifest_path=checkpoint)
        resumed=build(corrected,'resumed');assert resumed['success'],resumed
        assert any(e['stage']=='checkpoint_resume' for e in resumed['evidence']),resumed
        assert not any(e['stage']=='sketch' and (e.get('data') or {}).get('id')=='base' for e in resumed['evidence']);passed('resume_reuses_prefix_and_passes_measurements')
        assert hashlib.sha256(cp.read_bytes()).hexdigest()==cp_hash;passed('resume_preserves_checkpoint')
        changed=copy.deepcopy(corrected);changed['operations'][1]['depth_mm']=11
        r=compile(changed,'changed_prefix');assert not r['success'],r;passed('changed_prefix_rejected')
        changed=copy.deepcopy(corrected);changed['verification']['cylinder_groups'][0]['diameter_mm']=11
        r=compile(changed,'changed_requirements');assert not r['success'],r;passed('changed_requirements_rejected')
        tampered=output/'tampered_checkpoint.SLDPRT';tampered.write_bytes(cp.read_bytes()+b'tampered')
        manifest=json.loads(Path(checkpoint).read_text(encoding='utf-8'));manifest['native_path']=str(tampered)
        badmanifest=output/'tampered_checkpoint.json';badmanifest.write_text(json.dumps(manifest),encoding='utf-8')
        changed=copy.deepcopy(corrected);changed['recovery']['resume_manifest_path']=str(badmanifest)
        r=compile(changed,'changed_checkpoint');assert not r['success'],r;passed('changed_checkpoint_rejected')
        return results
    except Exception as e:
        save('failure',dict(error=str(e)));raise
    finally:
        client.close();print('REPORT '+str(output/'results.json'),flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--package-root',type=Path,required=True);p.add_argument('--native',action='store_true');args=p.parse_args()
    run(args.package_root.resolve(),ROOT/'artifacts'/('focused-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S')),args.native)
