"""Real candidate MCP gates, schema, input handles and dry-run only; native executable forbidden."""
from pathlib import Path
import copy, hashlib, json, os, queue, subprocess, sys, threading, time, uuid

root=Path(__file__).resolve().parents[1]
plugin=root/'plugins/auto-solidworks'
output=Path(sys.argv[1]).resolve()
output.mkdir(parents=True,exist_ok=False)
fixture=json.loads(Path(sys.argv[2]).resolve().read_text(encoding='utf-8'))
checks=[]
def check(value,name):
    assert value,name
    checks.append(name)
    print('PASS:',name,flush=True)
env=dict(os.environ,AUTO_SOLIDWORKS_TOOL_PROFILE='compact',AUTO_SOLIDWORKS_ARTIFACT_DIRECTORY=str(output/'artifacts'),
    CAD_SOLIDWORKS_EXECUTOR=str(output/'NATIVE-EXECUTOR-FORBIDDEN.exe'),CAD_SOLIDWORKS_PIPE='report-offline-'+uuid.uuid4().hex,
    CAD_EXECUTOR_LOG_DIR=str(output/'unexpected-native-logs'))
stderr=(output/'stderr.log').open('x',encoding='utf-8')
proc=subprocess.Popen([str(plugin/'runtime/mcp/AutoSolidWorks.ModelingMcp.exe')],cwd=plugin,env=env,
    stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=stderr,text=True,encoding='utf-8')
responses=queue.Queue()
threading.Thread(target=lambda:[responses.put(line) for line in proc.stdout],daemon=True).start()
serial=0
def rpc(method,params):
    global serial
    serial+=1
    start=time.monotonic_ns()
    proc.stdin.write(json.dumps(dict(jsonrpc='2.0',id=serial,method=method,params=params))+'\n');proc.stdin.flush()
    deadline=time.monotonic()+55
    while time.monotonic()<deadline:
        try: response=json.loads(responses.get(timeout=.5))
        except queue.Empty:
            if proc.poll() is not None:raise RuntimeError('MCP exited')
            continue
        except ValueError:continue
        if response.get('id')!=serial:continue
        envelope=dict(method=method,duration_ns=time.monotonic_ns()-start,response=response)
        path=output/f'{serial:03d}-receipt.json'
        with path.open('x',encoding='utf-8') as f:
            f.write(json.dumps(envelope,ensure_ascii=False,indent=2));f.flush();os.fsync(f.fileno())
        assert 'error' not in response,response
        return response['result']
    raise TimeoutError(method)
def call(name,args):
    assert name in {'cad_get_capabilities','cad_get_contract','cad_create_model_plan','cad_build_model','cad_store_model_input','cad_read_result'}
    if name=='cad_build_model':assert args.get('dryRun') is True
    result=rpc('tools/call',dict(name=name,arguments=args))
    assert not result.get('isError'),result
    body=result.get('structuredContent')
    if body is None:body=json.loads(next(c['text'] for c in result['content'] if c['type']=='text'))
    return body.get('result',body)
def compile(draft):return call('cad_create_model_plan',dict(draft=draft,nativeOutputPath=str(output/(uuid.uuid4().hex+'.SLDPRT'))))
def has(result,code):return any(d['code']==code for d in result.get('diagnostics',[]))
try:
    rpc('initialize',dict(protocolVersion='2025-06-18',capabilities={},clientInfo=dict(name='report-issue-offline',version='1')))
    proc.stdin.write(json.dumps(dict(jsonrpc='2.0',method='notifications/initialized'))+'\n');proc.stdin.flush()
    caps=call('cad_get_capabilities',{})
    expected_version=json.loads((plugin/'skills/auto-solidworks/references/capability-manifest.json').read_text(encoding='utf-8'))['version']
    check(caps['server_version']==expected_version and caps['report_issue_hardening']['native_acceptance']=='not_run','candidate identity and native boundary public in compact capabilities')
    for contract,fields in [('drawing_context',['interpretation_reviews','require_interpretation_review','nominal_value','deviation_authorization']),('draft',['TrimmedAxisAndDiameter','CompleteWall'])]:
        schema=call('cad_get_contract',dict(contract=contract))
        text=json.dumps(schema)
        check(all(field in text for field in fields),'public schema exposes '+contract+' additions')
    draft=dict(name='offline fixture',source_text='nominal cylinder and holes',operations=[
        dict(type='ProfileSketch',id='s',frame=dict(origin_mm=dict(x=0,y=0,z=40),normal=dict(x=0,y=0,z=1),x_direction=dict(x=1,y=0,z=0)),auto_dimension_primitives=True,require_fully_defined=True,primitives=[dict(type='Circle',diameter_mm=80,center_xmm=0,center_ymm=0)]),
        dict(type='ExtrudeBoss',id='column',sketch_id='s',depth_mm=100,end_condition='Blind'),
        dict(type='NativeFeature',id='hole',feature=dict(kind='Hole',diameter_mm=15,depth_mm=160,through_all=False,reverse=True,frame=dict(origin_mm=dict(x=0,y=0,z=200),normal=dict(x=0,y=0,z=1),x_direction=dict(x=1,y=0,z=0)),hole_centers=[dict(xmm=0,ymm=32.5)]))])
    compiled=compile(draft)
    check(compiled['success'] and has(compiled,'HOLE_ZERO_THICKNESS_RISK') and has(compiled,'HOLE_DIRECTION_TRACE'),'public compile reports tangency and cut direction without changing nominal')
    dry=call('cad_build_model',dict(planId=compiled['plan_id'],dryRun=True))
    check(dry['success'] is True,'native-forbidden plan handle dry-run remains usable')
    repeated=copy.deepcopy(draft)
    repeated['operations'][0]['dimensions']=[dict(name='guessed-D1',kind='Diameter',value=80,entities=[dict(primitive_index=0,segment_index=0,part='Segment')])]
    rejected=compile(repeated)
    check(not rejected['success'] and has(rejected,'SKETCH_DUPLICATE_DRIVING_DIMENSION'),'public compile refuses duplicated driving diameter before native execution')
    trimmed=copy.deepcopy(draft)
    trimmed['verification']=dict(cylinder_groups=[dict(id='trimmed',source_literal='axis diameter count only',diameter_mm=15,length_mm=0,coverage='TrimmedAxisAndDiameter',axis_starts_mm=[dict(x=0,y=32.5,z=40)])])
    check(compile(trimmed)['success'],'public schema/compiler accepts explicit trimmed contract')
    full=copy.deepcopy(trimmed);full['verification']['cylinder_groups'][0]['coverage']='CompleteWall'
    rejected=compile(full)
    check(not rejected['success'] and has(rejected,'VERIFICATION_SPEC'),'public default full-wall semantics still reject zero length')
    counts=copy.deepcopy(draft)
    counts['verification']=dict(native_dimensions=[dict(id='count',source_literal='six instances',dimension_name='D1@array',unit='Unitless',value=6,tolerance=0)])
    check(not compile(counts)['success'],'public zero count tolerance rejected')
    counts['verification']['native_dimensions'][0]['tolerance']=.01
    check(compile(counts)['success'],'public integer count with .01 tolerance accepted')
    strict=copy.deepcopy(fixture);strict['drawing_context']['require_interpretation_review']=True
    strict['drawing_context']['interpretation_reviews']=[]
    rejected=compile(strict)
    check(not rejected['success'] and has(rejected,'SOURCE_INTERPRETATION_REQUIRED'),'strict drawing plan without independent interpretation records rejected through MCP')
    ctx=strict['drawing_context'];ctx['interpretation_reviews']=[]
    for feature in ctx['features']:
        if not feature.get('critical',True):continue
        observations=[dict(view_id=v,bounds=dict(left=.01,top=.01,right=.99,bottom=.99),observation='Synthetic fixture source observation for '+feature['source_literal']) for v in feature['view_ids']]
        ctx['interpretation_reviews'].append(dict(feature_id=feature['id'],selected_hypothesis_id='source',dimension_datum_derivation='Synthetic contract fixture uses predeclared source dimensions; no image understanding claim',hypotheses=[
            dict(id='source',description=feature['source_literal'],observations=observations),
            dict(id='omitted-feature',description='feature absent',observations=[dict(observations[0],observation='Known synthetic source fixture contains the feature',contradicts=True)])]))
    accepted=compile(strict)
    check(accepted['success'] and accepted['source_interpretation_review_status']=='declared_source_interpretations_checked','valid strict source contract passes public compile with explicit scope')
    check(accepted['full_drawing_equivalence'] is False,'strict review does not claim complete drawing equivalence')
    contrad=copy.deepcopy(strict)
    contrad['drawing_context']['interpretation_reviews'][0]['hypotheses'][0]['observations'][0]['contradicts']=True
    rejected=compile(contrad)
    check(not rejected['success'] and has(rejected,'SOURCE_INTERPRETATION_CONFLICT'),'chosen source counterevidence cannot be bypassed by consistent numeric verification')
    stored=call('cad_store_model_input',dict(kind='drawing_context',input=ctx))
    bare=copy.deepcopy(strict);bare.pop('drawing_context')
    reused=call('cad_create_model_plan',dict(draft=bare,drawingContextId=stored['input_id'],nativeOutputPath=str(output/'stored-uncreated.SLDPRT')))
    check(reused['success'] and reused['source_interpretation_review_status']=='declared_source_interpretations_checked','stored drawing context retains strict interpretation contract')
    dry=call('cad_build_model',dict(planId=reused['plan_id'],dryRun=True))
    check(dry['success'],'strict source-bound plan passes compiled revalidation without native executor')
    deviated=copy.deepcopy(strict)
    diameter=next(d for d in deviated['drawing_context']['dimensions'] if d['id']=='diameter')
    diameter.update(nominal_value=diameter['value'],value=5.95,status='Derived',derivation='synthetic authorized fixture: 6 - .05 = 5.95',deviation_reason='synthetic tolerance fixture',deviation_authorization='explicit fixture authorization; not a native modeling instruction')
    for op in deviated['operations']:
        if (op.get('feature') or {}).get('kind')=='Hole' and op['feature'].get('diameter_mm')==6:op['feature']['diameter_mm']=5.95
        for p in op.get('primitives',[]):
            if p.get('type')=='Circle' and p.get('diameter_mm')==6:p['diameter_mm']=5.95
    for c in deviated['verification']['cylinder_groups']:
        if 'diameter' in c['source_dimension_ids']:c['diameter_mm']=5.95
    check(compile(deviated)['success'],'authorized executed diameter retains original source annotation through public omission and binding gates')
    diameter.pop('deviation_authorization')
    rejected=compile(deviated)
    check(not rejected['success'] and has(rejected,'SOURCE_DEVIATION_INVALID'),'same public deviation without authorization record is refused')
    check(not list(output.glob('*.SLDPRT')) and not (output/'unexpected-native-logs').exists(),'no native artifact or native executor log created')
    report=dict(success=True,checks=len(checks),passed=checks,native_execution=False,scope='public MCP compiler and dry-run gates on synthetic source fixture',runtime_sha256=hashlib.sha256((plugin/'runtime/mcp/AutoSolidWorks.ModelingMcp.dll').read_bytes()).hexdigest())
    (output/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'success':True,'checks':len(checks),'native_execution':False}))
finally:
    proc.stdin.close();proc.wait(timeout=15);stderr.close()
