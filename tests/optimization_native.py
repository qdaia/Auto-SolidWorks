"""Native regression through the exact isolated plugin launcher; never touches installed cache."""
import argparse, datetime, hashlib, json, os, queue, subprocess, threading, time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
class Session:
    def __init__(self,package,trace):
        env=os.environ.copy();env['AUTO_SOLIDWORKS_TRACE_DIRECTORY']=str(trace)
        env['CAD_EXECUTOR_LOG_DIR']=str(trace/'executor-logs')
        self.p=subprocess.Popen(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(package/'scripts/run-auto-solidworks-mcp.ps1')],cwd=package,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',errors='replace')
        self.q=queue.Queue();self.logs=[];self.counter=0
        def read():
            for line in self.p.stdout:
                try:self.q.put(json.loads(line))
                except ValueError:self.logs.append(line)
            self.q.put({'eof':True})
        def errors():
            for line in self.p.stderr:self.logs.append(line)
        threading.Thread(target=read,daemon=True).start();threading.Thread(target=errors,daemon=True).start()
        self.rpc('initialize',{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'isolated-optimization-validation','version':'1'}})
        self.p.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');self.p.stdin.flush()
    def rpc(self,method,params):
        self.counter+=1;self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.counter,'method':method,'params':params})+'\n');self.p.stdin.flush()
        while True:
            r=self.q.get(timeout=360)
            if r.get('eof'):raise RuntimeError(''.join(self.logs)[-3000:])
            if r.get('id')==self.counter:
                if 'error' in r:raise RuntimeError(r['error'])
                return r['result']
    def call(self,name,args):
        r=self.rpc('tools/call',{'name':name,'arguments':args})
        if r.get('isError'):raise RuntimeError(r)
        x=r.get('structuredContent') or json.loads(r['content'][0]['text'])
        return x['result'] if list(x)==['result'] else x
    def close(self):
        self.p.stdin.close()
        try:self.p.wait(timeout=15)
        except subprocess.TimeoutExpired:self.p.terminate()

def save(out,name,data): (out/(name+'.json')).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
def run_flange(session,out):
    source=Path(r'C:\Users\csj17\Desktop\CODEX TEST\solidworks-real-comparison-20261013-183436\evidence\draft.json')
    draft=json.loads(source.read_text(encoding='utf-8'))
    native=out/'flange.sldprt'
    for op in draft['operations']:
        if op['type']=='ProfileSketch':op['require_editable']=True
    start=time.perf_counter();plan=session.call('cad_create_model_plan',dict(draft=draft,nativeOutputPath=str(native),responseDetail='full'));save(out,'plan',plan)
    assert plan['success'],plan
    result=session.call('cad_build_model',dict(planId=plan['plan_id'],responseDetail='full'));save(out,'build',result)
    print(json.dumps(dict(stage='flange_build',seconds=time.perf_counter()-start,success=result['success'],message=result.get('message')),ensure_ascii=False),flush=True)
    assert result['success'],result
    v=draft['verification']
    trial=json.loads(json.dumps(v));trial['cylinder_groups'][2]['axis_starts_mm'][0]['x']=46
    probes=[dict(probe_id='move-hole-x',dimension_name='定位X_0@六孔定位',feature_name='六孔定位',trial_value=46,unit='Millimeter',trial_verification=trial,restore_verification=v)]
    inspected=session.call('cad_inspect_model',dict(inputPath=str(native),verification=v,editabilityProbes=probes,requireEditableSketches=True,responseDetail='full'))
    save(out,'inspection',inspected)
    assert inspected['success'] and inspected['parametric_status']=='passed',inspected
    assert all(p['passed'] and p['trial_geometry_passed'] and p['restore_geometry_passed'] and p['model_file_unchanged'] for p in inspected['editability_probes'])
    print(json.dumps(dict(stage='flange_editability',success=True,geometry=inspected['geometry'],sketches=[f for f in inspected['features'] if f.get('sketch_quality')]),ensure_ascii=False),flush=True)
    return native
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--out',type=Path,required=True);args=parser.parse_args()
    out=args.out.resolve();out.mkdir(parents=True,exist_ok=False)
    session=Session(ROOT/'plugins/auto-solidworks',out/'trace')
    try:
        save(out,'capabilities',session.call('cad_get_capabilities',{}))
        run_flange(session,out)
    finally:
        (out/'mcp.log').write_text(''.join(session.logs),encoding='utf-8');session.close()
if __name__=='__main__':main()
