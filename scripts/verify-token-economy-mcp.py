"""Real stdio/schema/compile/dry-run/read-drawing only. Native executor is forbidden."""
import hashlib
import json
import os
from pathlib import Path
import queue
import struct
import subprocess
import sys
import threading
import time
import uuid

root = Path(__file__).resolve().parents[1]
plugin = Path(os.environ.get('AUTO_SOLIDWORKS_VERIFY_PLUGIN', root/'plugins/auto-solidworks')).resolve()
output = Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=False)
checks = []
def check(condition, name):
    assert condition, name
    checks.append(name)
    print('PASS:', name, flush=True)
def size(value):
    return len(json.dumps(value, ensure_ascii=False, separators=(',', ':')))

class Session:
    def __init__(self, directory, profile='compact', target=plugin):
        self.directory = directory
        directory.mkdir()
        env = dict(os.environ, AUTO_SOLIDWORKS_TOOL_PROFILE=profile,
            AUTO_SOLIDWORKS_ARTIFACT_DIRECTORY=str(output/'local-artifacts'),
            CAD_SOLIDWORKS_EXECUTOR=str(output/'NATIVE-EXECUTOR-FORBIDDEN.exe'),
            CAD_SOLIDWORKS_PIPE='token-offline-'+uuid.uuid4().hex,
            CAD_EXECUTOR_LOG_DIR=str(output/'unexpected-native-logs'))
        self.stderr = (directory/'stderr.log').open('x', encoding='utf-8')
        self.proc = subprocess.Popen([str(target/'runtime/mcp/AutoSolidWorks.ModelingMcp.exe')], cwd=target,
            env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr, text=True, encoding='utf-8')
        self.queue = queue.Queue()
        self.serial = 0
        threading.Thread(target=lambda: [self.queue.put(line) for line in self.proc.stdout], daemon=True).start()
        self.rpc('initialize', dict(protocolVersion='2025-06-18', capabilities={}, clientInfo=dict(name='token-economy-offline', version='1')))
        self.proc.stdin.write(json.dumps(dict(jsonrpc='2.0', method='notifications/initialized'))+'\n')
        self.proc.stdin.flush()
    def rpc(self, method, params):
        self.serial += 1
        self.proc.stdin.write(json.dumps(dict(jsonrpc='2.0', id=self.serial, method=method, params=params))+'\n')
        self.proc.stdin.flush()
        deadline = time.monotonic()+55
        while time.monotonic()<deadline:
            try: line = self.queue.get(timeout=.5)
            except queue.Empty:
                if self.proc.poll() is not None: raise RuntimeError('MCP exited: '+self.directory.name)
                continue
            try: response = json.loads(line)
            except ValueError: continue
            if response.get('id') != self.serial: continue
            (self.directory/f'{self.serial:02d}-{method.replace("/", "-")}.json').write_text(json.dumps(response,ensure_ascii=False,indent=2),encoding='utf-8')
            assert 'error' not in response, response
            return response['result']
        raise TimeoutError('MCP deadline')
    def call(self, name, arguments, error=False):
        assert name in {'cad_get_capabilities','cad_get_contract','cad_create_model_plan','cad_build_model','cad_read_result','cad_read_drawing'}
        if name=='cad_build_model': assert arguments.get('dryRun') is True
        result=self.rpc('tools/call',dict(name=name,arguments=arguments))
        if error:
            check(result.get('isError') is True,'公开拒绝 '+name)
            return result
        assert not result.get('isError'),result
        body=result.get('structuredContent')
        if body is None: body=json.loads(next(c['text'] for c in result['content'] if c['type']=='text'))
        return body.get('result',body)
    def close(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=15)
        self.stderr.close()

baseline=Path(os.environ.get('AUTO_SOLIDWORKS_BASELINE_PLUGIN', str(Path.home()/'.codex/plugins/cache/personal/auto-solidworks/26.10.07')))
measurements={}
comparison_output=str(output/'comparison-uncreated.SLDPRT')
session=Session(output/'baseline', target=baseline)
try:
    old_catalog=session.rpc('tools/list',{})
    old_plan=session.call('cad_create_model_plan',dict(text='80 x 50 x 20 mm plate',nativeOutputPath=comparison_output))
    check(old_plan['success'],'相同文本基线编译成功')
    measurements['before']={'catalog_chars':size(old_catalog),'plan_response_chars':size(old_plan),'build_arguments_chars':size(dict(irJson=old_plan['ir_json'],dryRun=True)),'tool_count':len(old_catalog['tools'])}
finally: session.close()

session=Session(output/'compact')
try:
    catalog=session.rpc('tools/list',{})
    names={t['name'] for t in catalog['tools']}
    check(len(names)==16 and {'cad_get_contract','cad_read_result','cad_build_gordon_surface'}<=names,'精简目录保留常用建模与新增查询工具')
    check('cad_verify_edge_treatment' not in names,'专用验收 schema 不注入默认目录')
    check(size(catalog)<measurements['before']['catalog_chars']*.15,'默认目录字符降低至少 85%')
    status=next(t for t in catalog['tools'] if t['name']=='cad_execution_status')
    check(size(status['outputSchema'])<4000,'状态返回 schema 有界')
    caps=session.call('cad_get_capabilities',{})
    expected_version=json.loads((plugin/'.codex-plugin/plugin.json').read_text(encoding='utf-8'))['version']
    check(caps['server_version']==expected_version and caps['tool_profile']=='compact','新版本与默认配置可发现')
    expected_native=json.loads((plugin/'skills/auto-solidworks/references/capability-manifest.json').read_text(encoding='utf-8'))['current_native_acceptance']
    check(caps['current_native_acceptance']==expected_native and expected_native.startswith(('not_run','not_recertified')),'当前原生验收未运行边界与本地候选一致')
    schema=session.call('cad_get_contract',dict(contract='draft'))
    check('verification' in json.dumps(schema) and 'design_intent' in json.dumps(schema),'完整合同按需读取')
    compilation=session.call('cad_create_model_plan',dict(text='80 x 50 x 20 mm plate',nativeOutputPath=comparison_output))
    check(compilation['success'] and compilation['plan_id'].startswith('plan_'),'真实 MCP 编译产生句柄')
    check('ir_json' not in compilation and 'plan' not in compilation,'默认编译无完整计划往返')
    plan_id=compilation['plan_id']
    preview=session.call('cad_build_model',dict(planId=plan_id,dryRun=True))
    check(preview['success'] and preview['status']=='dry_run_passed','句柄通过真实 MCP dry-run')
    page=session.call('cad_read_result',dict(artifactId=preview['artifact_id'],section='/evidence',limit=1))
    check(page['total']>=0 and page['returned']<=1,'结果句柄真实分页可读')
    full=session.call('cad_create_model_plan',dict(text='80 x 50 x 20 mm plate',nativeOutputPath=str(output/'legacy-uncreated.SLDPRT'),responseDetail='full'))
    check(full['success'] and 'ir_json' in full and 'plan' in full,'完整编译模式兼容旧字段')
    legacy=session.call('cad_build_model',dict(irJson=full['ir_json'],dryRun=True,responseDetail='full'))
    check(legacy['success'],'旧 IR 执行入口兼容')
    session.call('cad_build_model',dict(planId=plan_id,irJson=full['ir_json'],dryRun=True),error=True)
    session.call('cad_build_model',dict(planId='../escape',dryRun=True),error=True)
    session.call('cad_read_result',dict(artifactId=preview['artifact_id'],limit=201),error=True)
    # A deterministic raster exercises the real offline ingestion path without external downloads.
    width,height=160,120
    stride=(width*3+3)//4*4
    pixels=bytearray()
    for y in range(height):
        row=bytearray()
        for x in range(width):
            black=(x in (30,130) and 25<=y<=95) or (y in (25,95) and 30<=x<=130)
            row.extend(b'\x00\x00\x00' if black else b'\xff\xff\xff')
        row.extend(b'\x00'*(stride-width*3));pixels.extend(row)
    bitmap=output/'drawing.bmp'
    bitmap.write_bytes(struct.pack('<2sIHHI',b'BM',54+len(pixels),0,0,54)+struct.pack('<IiiHHIIiiII',40,width,height,1,24,0,len(pixels),2835,2835,0,0)+pixels)
    drawing=session.call('cad_read_drawing',dict(inputPath=str(bitmap),artifactRoot=str(output/'drawing-artifacts')))
    check('source_facts' not in drawing and drawing.get('artifact_id') and drawing.get('omission_inventory_path'),'真实读图返回摘要及遗漏清单')
    details=session.call('cad_read_result',dict(artifactId=drawing['artifact_id'],section='/pages',limit=1))
    check(details['total']==1 and details['returned']==1,'原始图纸页面结果仍可完整读取')
    measurements['after']={'catalog_chars':size(catalog),'plan_response_chars':size(compilation),'build_arguments_chars':size(dict(planId=plan_id,dryRun=True)),'tool_count':len(catalog['tools']),'status_schema_chars':size(status['outputSchema']),'drawing_summary_chars':size(drawing)}
    tamper=Path(compilation['plan_path']);tamper.write_text(tamper.read_text(encoding='utf-8')+' ',encoding='utf-8')
    session.call('cad_build_model',dict(planId=plan_id,dryRun=True),error=True)
    persisted_id=full['plan_id']
finally: session.close()

session=Session(output/'restart','full')
try:
    full_catalog=session.rpc('tools/list',{})
    check(len(full_catalog['tools'])==21 and set(t['name'] for t in old_catalog['tools'])<=set(t['name'] for t in full_catalog['tools']),'完整配置保留全部旧工具名称')
    check(session.call('cad_build_model',dict(planId=persisted_id,dryRun=True))['success'],'服务重启后同版本计划可执行')
    measurements['full_profile_catalog_chars']=size(full_catalog)
finally: session.close()
check(not list(output.glob('*.SLDPRT')) and not (output/'unexpected-native-logs').exists(),'无原生模型或执行器启动证据')
measurements['reductions']={k:1-measurements['after'][k]/measurements['before'][k] for k in ['catalog_chars','plan_response_chars','build_arguments_chars']}
(output/'summary.json').write_text(json.dumps(dict(success=True,checks=checks,count=len(checks),measurements=measurements,actual_model_tokens='unknown',native_acceptance='not_run'),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(measurements,ensure_ascii=False,indent=2),flush=True)
