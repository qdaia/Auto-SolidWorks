"""Offline workspace and actual candidate MCP checks. Native executable deliberately unavailable."""
import copy, hashlib, importlib.util, json, os, queue, subprocess, sys, threading, time
from pathlib import Path

root=Path(__file__).resolve().parents[1]
plugin=root/'plugins/auto-solidworks'
output=Path(sys.argv[1]).resolve()
output.mkdir(parents=True,exist_ok=False)
fixture=Path(sys.argv[2]).resolve()
draft=json.loads(fixture.read_text(encoding='utf-8-sig'))
review=draft['drawing_context']['omission_review']
spec=importlib.util.spec_from_file_location('workspace',plugin/'scripts/prepare-review-workspace.py')
helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper)
checks=[]
def check(condition,name):
    if not condition: raise AssertionError(name)
    checks.append(name);print('PASS: '+name,flush=True)
workspace=helper.prepare(review['inventory_path'],review['inventory_sha256'],output/'workspace')
inventory=json.loads(Path(review['inventory_path']).read_text(encoding='utf-8-sig'))
all_ids=[]
for worklist in workspace['worklists']:
    all_ids.extend(c['id'] for c in json.loads(Path(worklist['path']).read_text(encoding='utf-8'))['candidates'])
check(len(all_ids)==len(set(all_ids))==len(inventory['candidates']) and set(all_ids)=={c['id'] for c in inventory['candidates']},'all inventory candidates retained exactly once in worklist')
pending=json.loads(Path(workspace['files']['drawing_context']['path']).read_text(encoding='utf-8'))
check(not pending['omission_review']['candidates'] and all(r['state']=='Unresolved' for r in pending['omission_review']['regions']),'no automatic interpretation, noise dismissal or accepted raw region')
check(pending['require_complete_bindings'] and pending['require_interpretation_review'],'strict source gates enabled in generated context')
check(workspace['raw_regions']==inventory['regions'],'all original raw regions and provenance retained')
try: helper.prepare(review['inventory_path'],'0'*64,output/'wrong-inventory')
except ValueError: check(not (output/'wrong-inventory').exists(),'wrong inventory SHA rejected before any output')
else: raise AssertionError('wrong inventory SHA accepted')
try: helper.prepare(review['inventory_path'],review['inventory_sha256'],output/'workspace')
except FileExistsError: check(True,'previous workspace cannot be overwritten')
else: raise AssertionError('workspace overwritten')

# Independent synthetic data verifies changed images/source and duplicate IDs fail closed.
synthetic=output/'synthetic';synthetic.mkdir()
source=synthetic/'source.png';source.write_bytes(b'original-source')
image=synthetic/'region.png';image.write_bytes(b'raw-region')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
base={'source_path':str(source),'source_sha256':sha(source),'complete_extraction':True,
      'regions':[{'id':'r','image_path':str(image),'image_sha256':sha(image)}],
      'candidates':[{'id':'c','review_region_ids':['r']}]}
for case in ('changed-source','changed-region','duplicate-id','incomplete-extraction','missing-region'):
    item=copy.deepcopy(base)
    source.write_bytes(b'changed-source' if case=='changed-source' else b'original-source')
    image.write_bytes(b'changed-image' if case=='changed-region' else b'raw-region')
    if case=='duplicate-id':item['candidates'].append(copy.deepcopy(item['candidates'][0]))
    if case=='incomplete-extraction':item['complete_extraction']=False
    if case=='missing-region':item['candidates'][0]['review_region_ids']=['missing']
    path=synthetic/(case+'.json');path.write_text(json.dumps(item),encoding='utf-8')
    try: helper.prepare(path,sha(path),synthetic/(case+'-output'))
    except ValueError: check(not (synthetic/(case+'-output')).exists(),case+' rejected before any output')
    else: raise AssertionError(case+' accepted')

env=dict(os.environ,AUTO_SOLIDWORKS_TOOL_PROFILE='compact',AUTO_SOLIDWORKS_ARTIFACT_DIRECTORY=str(output/'mcp-artifacts'),
         CAD_SOLIDWORKS_EXECUTOR=str(output/'NATIVE-EXECUTOR-FORBIDDEN.exe'),CAD_SOLIDWORKS_PIPE='integration-offline-only')
stderr=(output/'mcp-stderr.log').open('x',encoding='utf-8')
proc=subprocess.Popen([str(plugin/'runtime/mcp/AutoSolidWorks.ModelingMcp.exe')],cwd=plugin,env=env,
    stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=stderr,text=True,encoding='utf-8')
responses=queue.Queue()
threading.Thread(target=lambda:[responses.put(line) for line in proc.stdout],daemon=True).start()
serial=0
def rpc(method,params):
    global serial
    serial+=1
    proc.stdin.write(json.dumps({'jsonrpc':'2.0','id':serial,'method':method,'params':params})+'\n');proc.stdin.flush()
    deadline=time.monotonic()+45
    while time.monotonic()<deadline:
        try: body=json.loads(responses.get(timeout=.5))
        except queue.Empty:continue
        if body.get('id')==serial:
            (output/f'mcp-{serial}.json').write_text(json.dumps(body,ensure_ascii=False,indent=2),encoding='utf-8')
            assert 'error' not in body,body
            return body['result']
    raise TimeoutError(method)
try:
    rpc('initialize',{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'integrated-offline-regression','version':'1'}})
    proc.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');proc.stdin.flush()
    candidate=copy.deepcopy(draft);candidate['drawing_context']=pending
    result=rpc('tools/call',{'name':'cad_create_model_plan','arguments':{'draft':candidate,'nativeOutputPath':str(output/'NEVER-CREATED.SLDPRT')}})
    check(not result.get('isError'),'pending context is structurally valid through actual MCP interface')
    body=result.get('structuredContent') or json.loads(next(c['text'] for c in result['content'] if c['type']=='text'))
    if 'result' in body: body=body['result']
    check(body.get('success') is False,'pending visual review cannot compile to an executable plan')
    serialized=json.dumps(body)
    check('SOURCE_' in serialized or 'DRAWING_OMISSION' in serialized,'source/omission rejection evidence retained')
    check(not (output/'NEVER-CREATED.SLDPRT').exists(),'offline regression never creates native CAD')
finally:
    proc.stdin.close();proc.wait(timeout=15);stderr.close()
report={'passed':len(checks),'checks':checks,'native_execution':False,'candidate_count':len(all_ids)}
(output/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'passed':len(checks),'candidate_count':len(all_ids),'native_execution':False}))
