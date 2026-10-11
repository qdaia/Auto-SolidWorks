"""Real public MCP comparison; compilation/coverage/dry-run only, native executor forbidden."""
import copy, hashlib, json, os, queue, statistics, subprocess, sys, threading, time, uuid
from pathlib import Path

root=Path(__file__).resolve().parents[1]
output=Path(sys.argv[1]).resolve();output.mkdir(parents=True,exist_ok=False)
fixture=Path(sys.argv[2]).resolve()
draft=json.loads(fixture.read_text(encoding='utf-8'))
context=draft['drawing_context']
geometry=copy.deepcopy(draft);geometry.pop('drawing_context')
inventory=context['omission_review']['inventory_path']
checks=[]
def check(value,name):
    assert value,name
    checks.append(name);print('PASS:',name,flush=True)
def wire(value):return json.dumps(value,ensure_ascii=False,separators=(',',':'))
def chars(value):return len(wire(value))

class Session:
    def __init__(self,name,plugin):
        self.directory=output/name;self.directory.mkdir()
        self.plugin=plugin;self.counter=0;self.calls=[];self.responses=[]
        self.stderr=(self.directory/'stderr.log').open('x',encoding='utf-8')
        self.env=dict(os.environ,AUTO_SOLIDWORKS_ARTIFACT_DIRECTORY=str(self.directory/'artifacts'),
            CAD_SOLIDWORKS_EXECUTOR=str(output/'NATIVE-EXECUTOR-FORBIDDEN.exe'),
            CAD_SOLIDWORKS_PIPE='input-offline-'+uuid.uuid4().hex,CAD_EXECUTOR_LOG_DIR=str(output/'unexpected-native-logs'))
        self.start()
    def start(self):
        self.proc=subprocess.Popen([str(self.plugin/'runtime/mcp/AutoSolidWorks.ModelingMcp.exe')],cwd=self.plugin,
            env=self.env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.stderr,text=True,encoding='utf-8')
        self.queue=queue.Queue()
        threading.Thread(target=lambda:[self.queue.put(line) for line in self.proc.stdout],daemon=True).start()
        self.rpc('initialize',dict(protocolVersion='2025-06-18',capabilities={},clientInfo=dict(name='input-reuse-offline',version='1')))
        self.proc.stdin.write(wire(dict(jsonrpc='2.0',method='notifications/initialized'))+'\n');self.proc.stdin.flush()
    def rpc(self,method,params):
        self.counter+=1;started=time.perf_counter()
        self.proc.stdin.write(wire(dict(jsonrpc='2.0',id=self.counter,method=method,params=params))+'\n');self.proc.stdin.flush()
        deadline=time.monotonic()+55
        while time.monotonic()<deadline:
            try:line=self.queue.get(timeout=.5)
            except queue.Empty:
                if self.proc.poll() is not None:raise RuntimeError('MCP exited')
                continue
            try:response=json.loads(line)
            except ValueError:continue
            if response.get('id')!=self.counter:continue
            assert 'error' not in response,response
            (self.directory/f'{self.counter:03d}-response.json').write_text(json.dumps(response,ensure_ascii=False,indent=2),encoding='utf-8')
            return response['result'],(time.perf_counter()-started)*1000
        raise TimeoutError('MCP deadline')
    def call(self,name,args,error=False):
        assert name in {'cad_get_capabilities','cad_create_model_plan','cad_store_model_input','cad_review_drawing_coverage','cad_build_model','cad_read_result'}
        if name=='cad_build_model':assert args.get('dryRun') is True
        result,ms=self.rpc('tools/call',dict(name=name,arguments=args))
        self.calls.append(dict(name=name,arguments=args,elapsed_ms=ms,request_chars=chars(args)))
        self.responses.append(result)
        if error:
            check(result.get('isError') is True,'公开拒绝 '+name+' #'+str(len(checks)))
            return result
        assert not result.get('isError'),result
        body=result.get('structuredContent')
        if body is None:body=json.loads(next(c['text'] for c in result['content'] if c['type']=='text'))
        return body.get('result',body)
    def stop(self):
        self.proc.stdin.close();self.proc.wait(timeout=15)
    def close(self):
        self.stop();self.stderr.close()

old=Session('baseline',Path(os.environ.get('AUTO_SOLIDWORKS_BASELINE_PLUGIN', str(Path.home()/'.codex/plugins/cache/personal/auto-solidworks/26.10.07.1'))))
new=Session('optimized',root/'plugins/auto-solidworks')
try:
    old_catalog,_=old.rpc('tools/list',{});new_catalog,_=new.rpc('tools/list',{})
    check(len(new_catalog['tools'])==16 and {t['name'] for t in old_catalog['tools']}<={t['name'] for t in new_catalog['tools']},'新增输入存储且保留默认全部旧工具')
    check(new.call('cad_get_capabilities',{})['server_version']==json.loads((root/'plugins/auto-solidworks/.codex-plugin/plugin.json').read_text(encoding='utf-8'))['version'],'运行时明确返回新版本')
    native_path=str(output/'never-created.SLDPRT')
    baseline=old.call('cad_create_model_plan',dict(draft=draft,nativeOutputPath=native_path,responseDetail='full'))
    check(baseline['success'],'原版真实图纸草案编译成功')
    context_saved=new.call('cad_store_model_input',dict(kind='drawing_context',input=context))
    context_id=context_saved['input_id']
    draft_saved=new.call('cad_store_model_input',dict(kind='draft',input=geometry))
    draft_id=draft_saved['input_id']
    optimized=new.call('cad_create_model_plan',dict(draftId=draft_id,drawingContextId=context_id,nativeOutputPath=native_path,responseDetail='full'))
    check(optimized['success'],'图纸与草案句柄真实编译成功')
    def semantic(plan):
        result=copy.deepcopy(plan)
        result.pop('plan_id',None)
        return result
    check(semantic(baseline['plan'])==semantic(optimized['plan']),'原版与句柄路线完整IR一致（仅排除新请求ID）')
    check(new.call('cad_build_model',dict(planId=optimized['plan_id'],dryRun=True))['success'],'句柄编译结果经过原有dry-run验证')
    old_review=old.call('cad_review_drawing_coverage',dict(inventoryPath=inventory,drawingContext=context,limit=5))
    new_review=new.call('cad_review_drawing_coverage',dict(inventoryPath=inventory,drawingContextId=context_id,limit=5))
    check(old_review==new_review,'图纸句柄与完整上下文遗漏复查结果一致')
    timing={'old_review_ms':[],'new_review_ms':[]}
    for _ in range(5):
        old.call('cad_review_drawing_coverage',dict(inventoryPath=inventory,drawingContext=context,limit=5));timing['old_review_ms'].append(old.calls[-1]['elapsed_ms'])
        new.call('cad_review_drawing_coverage',dict(inventoryPath=inventory,drawingContextId=context_id,limit=5));timing['new_review_ms'].append(new.calls[-1]['elapsed_ms'])
    rename=[dict(op='test',path='/name',value=geometry['name']),dict(op='replace',path='/name',value='拱形支座_增量修正')]
    changed=new.call('cad_store_model_input',dict(kind='draft',baseInputId=draft_id,edits=rename))
    changed_draft_id=changed['input_id']
    corrected=copy.deepcopy(draft);corrected['name']='拱形支座_增量修正'
    old_changed=old.call('cad_create_model_plan',dict(draft=corrected,nativeOutputPath=native_path,responseDetail='full'))
    new_changed=new.call('cad_create_model_plan',dict(draftId=changed_draft_id,drawingContextId=context_id,nativeOutputPath=native_path,responseDetail='full'))
    check(semantic(old_changed['plan'])==semantic(new_changed['plan']),'增量修正与完整重发编译语义一致')
    # An actual source-bound dimension is corrected in both the binding and executed operation.
    binding=next(d for d in context['dimensions'] if d['parameter_path']=='feature.radius_mm')
    operation_index=next(i for i,o in enumerate(geometry['operations']) if o['id']==binding['operation_id'])
    dimension_index=context['dimensions'].index(binding)
    radius=binding['value']+.1
    dimension_edits=[dict(op='replace',path=f'/operations/{operation_index}/feature/radius_mm',value=radius)]
    for index,verification in enumerate(geometry['verification']['native_dimensions']):
        if binding['id'] in verification.get('source_dimension_ids',[]):
            dimension_edits.append(dict(op='replace',path=f'/verification/native_dimensions/{index}/value',value=radius))
    dimensional=new.call('cad_store_model_input',dict(kind='draft',baseInputId=draft_id,edits=dimension_edits))
    dimensional_context=new.call('cad_store_model_input',dict(kind='drawing_context',baseInputId=context_id,edits=[dict(op='replace',path=f'/dimensions/{dimension_index}/value',value=radius)]))
    corrected=copy.deepcopy(draft);corrected['operations'][operation_index]['feature']['radius_mm']=radius;corrected['drawing_context']['dimensions'][dimension_index]['value']=radius
    for verification in corrected['verification']['native_dimensions']:
        if binding['id'] in verification.get('source_dimension_ids',[]):verification['value']=radius
    direct=new.call('cad_create_model_plan',dict(draft=corrected,nativeOutputPath=native_path,responseDetail='full'))
    delta=new.call('cad_create_model_plan',dict(draftId=dimensional['input_id'],drawingContextId=dimensional_context['input_id'],nativeOutputPath=native_path,responseDetail='full'))
    check(direct['success'] and delta['success'] and semantic(direct['plan'])==semantic(delta['plan']),'尺寸及源绑定同步增量与完整草案一致')
    new.call('cad_create_model_plan',dict(draftId=dimensional['input_id'],drawingContextId=context_id,nativeOutputPath=native_path))
    check(not new.responses[-1].get('isError') and not new.call('cad_create_model_plan',dict(draftId=dimensional['input_id'],drawingContextId=context_id,nativeOutputPath=native_path))['success'],'只改执行尺寸未改源绑定仍被原编译器拒绝')
    incomplete=copy.deepcopy(context);incomplete['omission_review']['candidates']=[]
    incomplete_saved=new.call('cad_store_model_input',dict(kind='drawing_context',input=incomplete))
    check(not new.call('cad_create_model_plan',dict(draftId=draft_id,drawingContextId=incomplete_saved['input_id'],nativeOutputPath=native_path))['success'],'未审阅候选不因保存句柄而通过编译')
    incomplete=copy.deepcopy(context);incomplete['features']=[]
    incomplete_saved=new.call('cad_store_model_input',dict(kind='drawing_context',input=incomplete))
    check(not new.call('cad_create_model_plan',dict(draftId=draft_id,drawingContextId=incomplete_saved['input_id'],nativeOutputPath=native_path))['success'],'缺失源特征仍被原编译器拒绝')
    new.call('cad_create_model_plan',dict(draft=draft,draftId=draft_id,nativeOutputPath=native_path),error=True)
    new.call('cad_create_model_plan',dict(text='80 x 50 x 20 mm plate',drawingContextId=context_id,nativeOutputPath=native_path),error=True)
    new.call('cad_create_model_plan',dict(draft=draft,drawingContextId=context_id,nativeOutputPath=native_path),error=True)
    new.call('cad_review_drawing_coverage',dict(inventoryPath=inventory,drawingContext=context,drawingContextId=context_id),error=True)
    new.call('cad_store_model_input',dict(kind='draft',baseInputId=draft_id,edits=[dict(op='add',path='/diameter_typo',value=10)]),error=True)
    new.call('cad_store_model_input',dict(kind='draft',baseInputId=draft_id,edits=[dict(op='test',path='/name',value='错误前置条件')]),error=True)
    check(new.call('cad_read_result',dict(artifactId=context_id,section='/dimensions',limit=2))['total']==len(context['dimensions']),'存储输入仍可分页完整读取')
    new.stop();new.start()
    restarted=new.call('cad_create_model_plan',dict(draftId=draft_id,drawingContextId=context_id,nativeOutputPath=native_path,responseDetail='full'))
    check(semantic(restarted['plan'])==semantic(baseline['plan']),'重启服务后来源与草案句柄可继续使用')
    tamper=Path(changed['input_path']);tamper.write_bytes(tamper.read_bytes()+b' ')
    new.call('cad_create_model_plan',dict(draftId=changed_draft_id,drawingContextId=context_id,nativeOutputPath=native_path),error=True)
    # Compile inputs compare the same draft and drawing context; excludes image/system/LLM usage.
    full_review=dict(inventoryPath=inventory,drawingContext=context,limit=5)
    small_review=dict(inventoryPath=inventory,drawingContextId=context_id,limit=5)
    full_compile=dict(draft=draft,nativeOutputPath=native_path)
    small_compile=dict(draftId=draft_id,drawingContextId=context_id,nativeOutputPath=native_path)
    store_context=dict(kind='drawing_context',input=context)
    store_draft=dict(kind='draft',input=geometry)
    edit_request=dict(kind='draft',baseInputId=draft_id,edits=rename)
    baseline_work=[full_review]*3+[full_compile,dict(draft={**draft,'name':'拱形支座_增量修正'},nativeOutputPath=native_path)]
    optimized_work=[store_context,store_draft]+[small_review]*3+[small_compile,edit_request,small_compile]
    measurements=dict(context_chars=chars(context),geometry_chars=chars(geometry),
        coverage_request_before_chars=chars(full_review),coverage_request_after_chars=chars(small_review),
        compile_request_before_chars=chars(full_compile),compile_request_after_chars=chars(small_compile),
        correction_before_chars=chars(baseline_work[-1]),correction_after_chars=chars(edit_request)+chars(small_compile),
        workflow_before_chars=sum(map(chars,baseline_work)),workflow_after_chars=sum(map(chars,optimized_work)),
        workflow_description='initial uploads included; three coverage reviews + initial compile + one name correction/recompile',
        timing={**timing,'old_review_p50_ms':statistics.median(timing['old_review_ms']),'new_review_p50_ms':statistics.median(timing['new_review_ms'])},
        actual_model_tokens='not_measured',end_to_end_modeling_time='not_measured',native_execution='not_run')
    measurements['workflow_request_reduction']=1-measurements['workflow_after_chars']/measurements['workflow_before_chars']
finally:
    old.close();new.close()
    (output/'baseline-calls.json').write_text(json.dumps(old.calls,ensure_ascii=False,indent=2),encoding='utf-8')
    (output/'optimized-calls.json').write_text(json.dumps(new.calls,ensure_ascii=False,indent=2),encoding='utf-8')
check(not Path(native_path).exists() and not (output/'unexpected-native-logs').exists(),'未生成原生模型或启动执行器')
(output/'summary.json').write_text(json.dumps(dict(success=True,count=len(checks),checks=checks,measurements=measurements,fixture_path=str(fixture),fixture_sha256=hashlib.sha256(fixture.read_bytes()).hexdigest()),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(measurements,ensure_ascii=False,indent=2),flush=True)
