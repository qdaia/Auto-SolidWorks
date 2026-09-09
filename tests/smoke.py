"""Local regression checks for the modeling-only package, kept outside the plugin."""
import argparse
import json
from pathlib import Path
import queue
import struct
import subprocess
import threading
import zlib

parser = argparse.ArgumentParser()
parser.add_argument('plugin', type=Path)
parser.add_argument('--report', type=Path, required=True)
args = parser.parse_args()
args.report.parent.mkdir(parents=True, exist_ok=True)
proc = subprocess.Popen(['powershell.exe', '-NoLogo', '-NoProfile', '-NonInteractive',
    '-ExecutionPolicy', 'Bypass', '-File', str(args.plugin / 'scripts/run-auto-solidworks-mcp.ps1')],
    cwd=args.plugin, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, encoding='utf-8', errors='replace')
responses = queue.Queue()
errors = []
def reader():
    for line in proc.stdout:
        try:
            responses.put(json.loads(line))
        except json.JSONDecodeError:
            errors.append(line)
    responses.put({'eof': True})
threading.Thread(target=reader, daemon=True).start()
def stderr_reader():
    for line in proc.stderr:
        errors.append(line)
threading.Thread(target=stderr_reader, daemon=True).start()
counter = 0
def rpc(method, params=None):
    global counter
    counter += 1
    request = {'jsonrpc':'2.0', 'id':counter, 'method':method, 'params':params or {}}
    proc.stdin.write(json.dumps(request, ensure_ascii=False) + '\n')
    proc.stdin.flush()
    while True:
        response = responses.get(timeout=90)
        if response.get('eof'):
            raise RuntimeError('Server exited: ' + ''.join(errors)[-2000:])
        if response.get('id') == counter:
            if 'error' in response:
                raise RuntimeError(response['error'])
            return response['result']
def call(name, **arguments):
    result = rpc('tools/call', {'name':name, 'arguments':arguments})
    if result.get('isError'):
        raise RuntimeError(result)
    content = result.get('structuredContent')
    content = content if content is not None else json.loads(result['content'][0]['text'])
    return content['result'] if list(content) == ['result'] else content
def png(path):
    width, height = 160, 100
    rows = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            border = ((x in (20,140) and 20 <= y <= 80) or (y in (20,80) and 20 <= x <= 140))
            row.extend(bytes([0 if border else 255]) * 3)
        rows.append(b'\x00' + row)
    def chunk(kind, data):
        return struct.pack('>I',len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind+data))
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR',struct.pack('>IIBBBBB',width,height,8,2,0,0,0))
        + chunk(b'IDAT',zlib.compress(b''.join(rows))) + chunk(b'IEND',b''))
report = {'plugin':str(args.plugin), 'checks':{}}
try:
    report['initialize'] = rpc('initialize', {'protocolVersion':'2024-11-05',
        'capabilities':{}, 'clientInfo':{'name':'modeling-regression','version':'1'}})
    proc.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n')
    proc.stdin.flush()
    listed = rpc('tools/list')['tools']
    report['tools'] = listed
    names = {t['name'] for t in listed}
    assert names == {'cad_get_capabilities','cad_read_drawing','cad_create_model_plan','cad_build_model','cad_executor_health','cad_inspect_model','cad_build_assembly','cad_list_weldment_profiles','cad_export_drawing'}
    report['checks']['nine_modeling_tools'] = True
    report['capabilities'] = call('cad_get_capabilities')
    assert report['capabilities']['mode'] == 'modeling_only'
    native = str(args.report.parent / 'model-check.SLDPRT')
    export = str(args.report.parent / 'model-check.STEP')
    schemas = {t['name']:t['inputSchema'] for t in listed}
    report['schemas'] = schemas
    assert schemas['cad_build_model']['properties']['dryRun'].get('default') is False
    assert 'run_id' not in schemas['cad_build_model']['properties']
    report['checks']['direct_build_default'] = True
    # Method parameter names are exposed by the MCP SDK in camelCase.
    plan = call('cad_create_model_plan', text='80 x 50 x 10 mm plate', nativeOutputPath=native, exportPaths=[export])
    assert plan['success'], plan
    assert plan['plan']['output']['export_paths'] == [export]
    report['checks']['text_plan_and_export'] = True
    check = call('cad_build_model', irJson=plan['ir_json'], dryRun=True)
    assert check['success'] and check['status'] == 'dry_run_passed', check
    report['checks']['optional_dry_run'] = True
    operations = [
        {'type':'ProfileSketch','id':'base','name':'Base','plane':'Front','primitives':[
            {'type':'CenteredRectangle','center_xmm':0,'center_ymm':0,'width_mm':80,'height_mm':50}]},
        {'type':'ExtrudeBoss','id':'boss','name':'Boss','sketch_id':'base','depth_mm':10,'end_condition':'Blind'},
        {'type':'ProfileSketch','id':'hole','name':'Hole','plane':'Front','primitives':[
            {'type':'Circle','center_xmm':0,'center_ymm':0,'diameter_mm':10}]},
        {'type':'ExtrudeCut','id':'cut','name':'Cut','sketch_id':'hole','depth_mm':10,'end_condition':'Blind'}]
    generic = call('cad_create_model_plan', nativeOutputPath=native,
        draft={'name':'plate-with-hole','source_text':'80x50x10 mm plate; center hole diameter 10 mm','operations':operations})
    assert generic['success'], generic
    checked = call('cad_build_model', irJson=generic['ir_json'], dryRun=True)
    assert checked['success'], checked
    report['checks']['typed_sketch_boss_cut'] = True
    invalid = json.loads(generic['ir_json'])
    invalid['operations'][1]['depth_mm'] = -10
    rejected = call('cad_build_model', irJson=json.dumps(invalid), dryRun=True)
    assert not rejected['success'], rejected
    report['checks']['invalid_geometry_reported'] = True
    input_path = args.report.parent / 'drawing-input.png'
    png(input_path)
    drawing = call('cad_read_drawing', inputPath=str(input_path), artifactRoot=str(args.report.parent/'drawings'))
    report['drawing_read'] = drawing
    assert drawing['outcome'] in ('Succeeded','SucceededWithWarnings'), drawing
    assert len(drawing['pages']) == 1
    assert Path(drawing['pages'][0]['observation_path']).is_file()
    assert any(o['kind'] == 'VisibleLine' for o in drawing['pages'][0]['observation']['observations'])
    report['checks']['drawing_pages_and_lines'] = True
    report['checks']['dry_run_creates_no_model'] = not Path(native).exists()
    assert report['checks']['dry_run_creates_no_model']
    report['status'] = 'passed'
finally:
    proc.stdin.close()
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.terminate()
    report['stderr_tail'] = ''.join(errors)[-2000:]
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'status':report['status'],'checks':report['checks'],'report':str(args.report)},ensure_ascii=False))
