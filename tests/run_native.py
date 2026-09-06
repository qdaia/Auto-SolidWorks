import argparse, json, subprocess, threading, queue, datetime, hashlib
from pathlib import Path
from cases import cases

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--case', action='append')
parser.add_argument('--drawing')
parser.add_argument('--assembly',action='store_true')
parser.add_argument('--inspect')
parser.add_argument('--drawing-hints')
parser.add_argument('--contracts',action='store_true')
parser.add_argument('--references',action='store_true')
parser.add_argument('--package-root',type=Path)
parser.add_argument('--model-dir',type=Path)
args=parser.parse_args()
package=args.package_root.resolve() if args.package_root else root/'plugins/auto-solidworks'
output=root/'artifacts'/datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
output.mkdir(parents=True)
model_dir=args.model_dir.resolve() if args.model_dir else output
model_dir.mkdir(parents=True,exist_ok=True)
p=subprocess.Popen(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',
    str(package/'scripts/run-auto-solidworks-mcp.ps1')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
    text=True,encoding='utf-8',errors='replace',cwd=package)
q=queue.Queue(); logs=[]; counter=0
def reader():
    for line in p.stdout:
        try:q.put(json.loads(line))
        except ValueError:logs.append(line)
    q.put({'eof':True})
def errors():
    for line in p.stderr:logs.append(line)
threading.Thread(target=reader,daemon=True).start()
threading.Thread(target=errors,daemon=True).start()
def rpc(method,params):
    global counter
    counter+=1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':counter,'method':method,'params':params})+'\n');p.stdin.flush()
    while True:
        r=q.get(timeout=180)
        if r.get('eof'):raise RuntimeError(''.join(logs)[-2000:])
        if r.get('id')==counter:
            if 'error' in r:raise RuntimeError(r['error'])
            return r['result']
def call(name,arguments):
    r=rpc('tools/call',{'name':name,'arguments':arguments})
    if r.get('isError'):raise RuntimeError(r)
    c=r.get('structuredContent') or json.loads(r['content'][0]['text'])
    return c['result'] if list(c)==['result'] else c
report={}
try:
    rpc('initialize',{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'native-upgrade-tests','version':'1'}})
    p.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');p.stdin.flush()
    listing=rpc('tools/list',{})
    (output/'tools.json').write_text(json.dumps(listing,indent=2),encoding='utf-8')
    if args.contracts:
        from contract_cases import check_contracts
        report['contracts']=check_contracts(call,root,output)
        (output/'results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps(report,ensure_ascii=False),flush=True)
    if args.references:
        from reference_cases import check_references
        report['references']=check_references(call,root)
        (output/'results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps(report,ensure_ascii=False),flush=True)
    if args.inspect:
        result=call('cad_inspect_model',{'inputPath':args.inspect})
        report['inspection']=result
        (output/'results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps({'success':result['success'],'configurations':result.get('configurations',[]),'message':result['message']},ensure_ascii=False),flush=True)
    if args.assembly:
        from assembly_cases import assembly_cases
        for name,spec in assembly_cases(root,output).items():
            expected=spec.pop('_expected_interference',None)
            hashes={c['path']:hashlib.sha256(Path(c['path']).read_bytes()).hexdigest() for c in spec['components']}
            result=call('cad_build_assembly',{'plan':spec})
            if result['success']:
                for component in result['components']:
                    intended=next(c for c in spec['components'] if c['id']==component['id'])
                    assert component['fixed']==intended.get('fixed',False),'Component fixed/floating state differs from requested state'
            for path,original in hashes.items():assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==original,'Assembly creation changed a source component'
            if expected is not None and result['success']:
                actual=sum(i['volume_mm3'] for i in result['interferences'])
                if abs(actual-expected)>.01:result['success']=False;result['message']=f'Expected interference {expected}, measured {actual}'
            report[name]=result
            (output/'results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
            print(json.dumps({'case':name,'success':result['success'],'message':result['message'],'reopened':result.get('reopened'),
                'component_count':len(result.get('components') or []),'interference_volume_mm3':sum(i['volume_mm3'] for i in result.get('interferences') or [])},ensure_ascii=False),flush=True)
    if args.drawing:
        hints=json.loads(Path(args.drawing_hints).read_text(encoding='utf-8')) if args.drawing_hints else []
        drawing=call('cad_read_drawing',{'inputPath':args.drawing,'artifactRoot':str(output/'drawing'),'viewHints':hints})
        (output/'drawing-result.json').write_text(json.dumps(drawing,ensure_ascii=False,indent=2),encoding='utf-8')
        text_count=sum(sum(o['kind']=='Text' for o in p['observation']['observations']) for p in drawing['pages'])
        dimensions=[d['raw_literal'] for p in drawing['pages'] for d in p['observation']['dimension_observations']]
        report['drawing']={'success':drawing['outcome']!='Rejected' and text_count>0 and len(dimensions)>0,'text_count':text_count,'dimension_candidates':dimensions,'diagnostics':drawing['diagnostics']}
        (output/'results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps(report,ensure_ascii=False),flush=True)
    for name,draft in cases.items():
        if (args.drawing or args.assembly or args.inspect or args.contracts or args.references) and not args.case:continue
        if args.case and name not in args.case:continue
        print('RUN '+name,flush=True)
        try:
            source=Path(draft['source_model_path']) if draft.get('source_model_path') else None
            source_hash=hashlib.sha256(source.read_bytes()).hexdigest() if source else None
            plan=call('cad_create_model_plan',{'draft':draft,'nativeOutputPath':str(model_dir/(name+'.SLDPRT')),
                'exportPaths':[str(model_dir/(name+'.STEP'))]})
            (output/(name+'-plan.json')).write_text(json.dumps(plan,indent=2),encoding='utf-8')
            if not plan['success']:raise RuntimeError(plan['diagnostics'])
            result=call('cad_build_model',{'irJson':plan['ir_json']})
            result['executor_sha256']=hashlib.sha256((package/'runtime/executor/CadModeling.Executor.SolidWorks.dll').read_bytes()).hexdigest()
            if source:assert hashlib.sha256(source.read_bytes()).hexdigest()==source_hash,'Editing changed the source file'
            if result['success']:
                native=Path(result['native_path']); before_hash=hashlib.sha256(native.read_bytes()).hexdigest()
                inspection=call('cad_inspect_model',{'inputPath':str(native)})
                (output/(name+'-inspection.json')).write_text(json.dumps(inspection,indent=2),encoding='utf-8')
                assert inspection['success'],inspection
                assert hashlib.sha256(native.read_bytes()).hexdigest()==before_hash, 'Read-only inspection changed the model file'
                result['reopen_inspection_passed']=True
                if name.startswith('workflow_'):
                    from workflow_cases import verify_workflow
                    result['workflow_geometry_checks']=verify_workflow(call,native,name)
            report[name]=result
            print(json.dumps({'case':name,'success':result['success'],'message':result['message'],
                'geometry':result.get('geometry'),'failures':[e for e in result.get('evidence',[]) if not e['passed']]},ensure_ascii=False),flush=True)
        except Exception as ex:
            report[name]={'success':False,'error':str(ex)}
            print(json.dumps(report[name],ensure_ascii=False),flush=True)
        (output/'results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
finally:
    p.stdin.close()
    try:p.wait(timeout=15)
    except subprocess.TimeoutExpired:p.terminate()
    (output/'stderr.log').write_text(''.join(logs),encoding='utf-8')
print('REPORT '+str(output/'results.json'),flush=True)
raise SystemExit(0 if report and all(r['success'] for r in report.values()) else 1)
