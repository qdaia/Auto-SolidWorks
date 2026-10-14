"""Actual image ingestion and pending-input MCP integration; native executor deliberately unavailable."""
import hashlib,json,os,queue,subprocess,sys,threading,time,uuid
from pathlib import Path

root=Path(__file__).resolve().parents[1]
plugin=Path(os.environ.get('AUTO_SOLIDWORKS_VERIFY_PLUGIN',str(root/'plugins/auto-solidworks'))).resolve()
output=Path(sys.argv[1]).resolve();output.mkdir(parents=True,exist_ok=False)
source=Path(sys.argv[2]).resolve()
original_sha=hashlib.sha256(source.read_bytes()).hexdigest()
fixture=json.loads(Path(sys.argv[3]).read_text(encoding='utf-8-sig'))
checks=[];calls=[]
def check(value,name):
    assert value,name
    checks.append(name);print('PASS: '+name,flush=True)

class Session:
    def __init__(self,profile):
        self.profile=profile;self.serial=0
        self.directory=output/profile;self.directory.mkdir()
        self.stderr=(self.directory/'stderr.log').open('x',encoding='utf-8')
        env=dict(os.environ,AUTO_SOLIDWORKS_TOOL_PROFILE=profile,AUTO_SOLIDWORKS_ARTIFACT_DIRECTORY=str(self.directory/'artifacts'),
                 CAD_SOLIDWORKS_EXECUTOR=str(output/'NATIVE-EXECUTOR-FORBIDDEN.exe'),CAD_SOLIDWORKS_PIPE='read-offline-'+uuid.uuid4().hex,
                 CAD_EXECUTOR_LOG_DIR=str(output/'unexpected-native-logs'))
        # Match the bundled launcher's OCR initialization while retaining the native-forbidden guard.
        ocr_path=Path(os.environ['LOCALAPPDATA'])/'AutoSolidWorks/dependencies/ocr.json'
        if ocr_path.is_file():
            ocr=json.loads(ocr_path.read_text(encoding='utf-8-sig'))
            env.setdefault('CAD_TESSERACT_PATH',ocr['executable'])
            env.setdefault('CAD_TESSDATA_DIR',ocr['tessdata'])
            env.setdefault('CAD_TESSERACT_LANG','eng+chi_sim')
        self.proc=subprocess.Popen([str(plugin/'runtime/mcp/AutoSolidWorks.ModelingMcp.exe')],cwd=plugin,env=env,
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.stderr,text=True,encoding='utf-8')
        self.responses=queue.Queue()
        threading.Thread(target=lambda:[self.responses.put(line) for line in self.proc.stdout],daemon=True).start()
        self.rpc('initialize',{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'read-integration-regression','version':'1'}})
        self.proc.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');self.proc.stdin.flush()
    def rpc(self,method,params):
        self.serial+=1;start=time.monotonic()
        self.proc.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.serial,'method':method,'params':params},ensure_ascii=False)+'\n');self.proc.stdin.flush()
        while time.monotonic()-start<90:
            try: response=json.loads(self.responses.get(timeout=.5))
            except queue.Empty:
                if self.proc.poll() is not None:raise RuntimeError('MCP exited')
                continue
            if response.get('id')!=self.serial:continue
            (self.directory/f'{self.serial:03d}.json').write_text(json.dumps(response,ensure_ascii=False,indent=2),encoding='utf-8')
            calls.append({'profile':self.profile,'method':method,'tool':params.get('name'),'seconds':time.monotonic()-start})
            assert 'error' not in response,response
            return response['result']
        raise TimeoutError(method)
    def call(self,name,args):
        assert name in {'cad_get_capabilities','cad_read_drawing','cad_read_result','cad_create_model_plan','cad_store_model_input','cad_review_drawing_coverage'}
        result=self.rpc('tools/call',{'name':name,'arguments':args})
        assert not result.get('isError'),result
        body=result.get('structuredContent') or json.loads(next(c['text'] for c in result['content'] if c['type']=='text'))
        return body['result'] if 'result' in body else body
    def close(self):
        self.proc.stdin.close();self.proc.wait(timeout=15);self.stderr.close()

for profile in ('compact','full'):
    session=Session(profile)
    try:
        tools=session.rpc('tools/list',{})['tools']
        read_tool=next(t for t in tools if t['name']=='cad_read_drawing')
        option=read_tool['inputSchema']['properties']['prepareReview']
        check(option.get('default') is True and 'prepareReview' not in read_tool['inputSchema'].get('required',[]),profile+' exposes optional prepareReview=true without changing mandatory arguments')
        caps=session.call('cad_get_capabilities',{})
        check(caps['integrated_workflow']['semantic_review_automatic'] is False and caps['integrated_workflow']['native_acceptance']=='not_run',profile+' capabilities expose actual pending/native boundaries')
        if profile=='full':continue
        read=session.call('cad_read_drawing',{'inputPath':str(source),'artifactRoot':str(output/'fresh-ingestion')})
        check(read['outcome'] in ('Succeeded','SucceededWithWarnings') and read['review_workspace_status']=='pending_independent_visual_review','actual image ingestion returns pending workspace in the same call')
        workspace=read['review_workspace'];context_id=workspace['drawing_context_input_id'];worklist_id=workspace['worklist_artifact_id']
        check(context_id.startswith('context_') and worklist_id.startswith('result_') and workspace['compiled'] is False,'new handles have original typed input/receipt identities, not accepted plans')
        inventory=json.loads(Path(read['omission_inventory_path']).read_text(encoding='utf-8-sig'))
        check(any(c['provider']=='tesseract-local' for c in inventory['candidates']),'normal launcher OCR configuration actually uses local Tesseract candidate extraction')
        raw=session.call('cad_read_result',{'artifactId':worklist_id,'section':'/raw_regions','limit':200})
        check(raw['total']==len(inventory['regions']) and raw['returned']==len(inventory['regions']),'all raw regions are accessible through bounded existing pagination')
        page=session.call('cad_read_result',{'artifactId':worklist_id,'section':'/worklists','limit':200})
        all_ids=[]
        for number in range(page['total']):
            offset=0
            while True:
                batch=session.call('cad_read_result',{'artifactId':worklist_id,'section':f'/worklists/{number}/candidates','offset':offset,'limit':200})
                all_ids.extend(c['id'] for c in batch['items'])
                if batch['next_offset'] is None:break
                offset=batch['next_offset']
        check(len(all_ids)==len(set(all_ids)) and set(all_ids)=={c['id'] for c in inventory['candidates']},'complete original candidate IDs including residual ink remain paginable exactly once')
        review=session.call('cad_read_result',{'artifactId':context_id,'section':'/omission_review/regions','limit':200})
        check(all(r['state']=='Unresolved' for r in review['items']),'no raw image is auto-marked Reviewed')
        geometry=dict(fixture);geometry.pop('drawing_context')
        stored=session.call('cad_store_model_input',{'kind':'draft','input':geometry})
        compiled=session.call('cad_create_model_plan',{'draftId':stored['input_id'],'drawingContextId':context_id,'nativeOutputPath':str(output/'NEVER-CREATED.SLDPRT')})
        check(compiled['success'] is False and compiled['plan_id'] is None,'actual pending handle cannot create executable source-backed plan')
        check(compiled['diagnostics'][0]['severity']==2,'source error appears before informational direction traces in actual MCP summary')
        gaps=session.call('cad_review_drawing_coverage',{'inventoryPath':read['omission_inventory_path'],'drawingContextId':context_id,'limit':5})
        check(gaps.get('review',{}).get('passed') is False or 'review_incomplete' in json.dumps(gaps),'coverage review still reports pending candidates and regions')
        legacy=session.call('cad_read_drawing',{'inputPath':str(source),'artifactRoot':str(output/'legacy-extraction'),'prepareReview':False})
        check(legacy['outcome'] in ('Succeeded','SucceededWithWarnings') and 'review_workspace' not in legacy,'explicit extraction-only option preserves original response behavior')
        pending_id=session.call('cad_store_model_input',{'kind':'drawing_context','baseInputId':context_id,'edits':[{'op':'add','path':'/interpretation_reviews','value':[]}]})['input_id']
        check(pending_id.startswith('context_'),'generated context supports unchanged incremental-input API')
    finally:session.close()
check(not (output/'NEVER-CREATED.SLDPRT').exists() and not (output/'unexpected-native-logs').exists(),'all actual image/schema/compile calls remain native-forbidden')
check(hashlib.sha256(source.read_bytes()).hexdigest()==original_sha,'raw source image remains unchanged')
report={'success':True,'count':len(checks),'checks':checks,'native_execution':False,'source_sha256':original_sha,'calls':calls,
        'independent_visual_analysis':'not_performed','end_to_end_modeling_time':'not_measured'}
(output/'summary.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'success':True,'checks':len(checks),'native_execution':False}))
