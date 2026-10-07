"""Source runtime schema/compile/dry-run only. No CAD, health, inspect or native build calls."""
import copy
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid

root = Path(__file__).resolve().parents[1]
plugin = Path(os.environ.get('AUTO_SOLIDWORKS_VERIFY_PLUGIN', str(root / 'plugins/auto-solidworks'))).resolve()
output = Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=False)
identity = json.loads((plugin/'runtime/build-identity.json').read_text(encoding='utf-8'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
for name, expected in identity['source_hashes'].items():
    assert sha(root/name) == expected, name
for name, expected in identity['runtime_hashes'].items():
    assert sha(plugin/name) == expected, name
env = os.environ.copy()
env['CAD_SOLIDWORKS_EXECUTOR'] = str(output/'NATIVE-EXECUTOR-FORBIDDEN.exe')
env['CAD_SOLIDWORKS_PIPE'] = 'offline-only-' + uuid.uuid4().hex
env['CAD_EXECUTOR_LOG_DIR'] = str(output/'unexpected-executor-logs')
stderr = (output/'mcp-stderr.log').open('x', encoding='utf-8')
proc = subprocess.Popen([str(plugin/'runtime/mcp/AutoSolidWorks.ModelingMcp.exe')],
    cwd=plugin, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr, text=True, encoding='utf-8')
lines = queue.Queue()
def receive():
    for line in proc.stdout:
        lines.put(line)
threading.Thread(target=receive, daemon=True).start()
serial = 0
checks = []
calls = []
def rpc(method, params):
    global serial
    serial += 1
    proc.stdin.write(json.dumps(dict(jsonrpc='2.0', id=serial, method=method, params=params))+'\n')
    proc.stdin.flush()
    deadline = time.monotonic()+45
    while time.monotonic() < deadline:
        try:
            line = lines.get(timeout=.5)
        except queue.Empty:
            if proc.poll() is not None:
                raise RuntimeError('MCP exited prematurely')
            continue
        try:
            response = json.loads(line)
        except ValueError:
            continue
        if response.get('id') != serial:
            continue
        (output/f'{serial:02d}-{method.replace("/", "-")}.json').write_text(json.dumps(response, ensure_ascii=False, indent=2), encoding='utf-8')
        assert 'error' not in response, response
        return response['result']
    raise TimeoutError('Offline MCP request deadline exceeded')
def call(name, arguments):
    assert name in {'cad_get_capabilities', 'cad_create_model_plan', 'cad_build_model'}, 'Native-capable tool forbidden'
    if name == 'cad_build_model':
        assert arguments.get('dryRun') is True, 'Native build forbidden'
    calls.append(dict(name=name, arguments=arguments))
    result = rpc('tools/call', dict(name=name, arguments=arguments))
    assert not result.get('isError'), result
    if 'structuredContent' in result:
        body = result['structuredContent']
        return body.get('result', body)
    return json.loads(next(c['text'] for c in result['content'] if c['type']=='text'))
def check(condition, name):
    assert condition, name
    checks.append(name)
    print('PASS:', name, flush=True)
try:
    rpc('initialize', dict(protocolVersion='2025-06-18', capabilities={}, clientInfo=dict(name='offline-design-intent-regression', version='1')))
    proc.stdin.write(json.dumps(dict(jsonrpc='2.0', method='notifications/initialized'))+'\n')
    proc.stdin.flush()
    listing = rpc('tools/list', {})
    capabilities = call('cad_get_capabilities', {})
    expected_version=json.loads((plugin/'.codex-plugin/plugin.json').read_text(encoding='utf-8'))['version']
    check(capabilities['server_version']==expected_version, 'source runtime offline version')
    check(capabilities['current_native_acceptance'] == 'not_recertified_local_integration_engineering_paused', 'native acceptance explicitly unrun')
    check(capabilities['design_intent_authoring']['native_acceptance'].startswith('historical26_') and 'not_natively_recertified' in capabilities['design_intent_authoring']['native_acceptance'], 'authoring evidence historical only for this release')
    check(capabilities['offline_modeling_boundaries']['native_acceptance'].startswith('historical26_') and 'not_natively_recertified' in capabilities['offline_modeling_boundaries']['native_acceptance'], 'text and mobility evidence historical only for this release')
    check('unavailable_and_rejected' in capabilities['offline_modeling_boundaries']['assembly_native_mobility_adapter'], 'moving native basis limitation public')
    check(capabilities['concentric_tree_mobility']['source_implementation'] is True, 'bounded tree mobility source contract public')
    check(any('closed_loops' in x and 'unavailable_and_rejected' in x for x in capabilities['concentric_tree_mobility']['limits']), 'tree mobility does not claim limits or coupled closed loops')
    check(capabilities['semantic_topology']['source_implementation'] is True, 'semantic core implementation public')
    check(capabilities['physical_thread_contract']['native_acceptance'].startswith('bounded_nominal_internal_external_production_native_pass'),
          'public thread evidence remains bounded nominal geometry')
    check(any('standard_fit' in limit and 'unavailable' in limit for limit in capabilities['physical_thread_contract']['limits']),
          'public thread standard-fit certification remains unavailable')
    check(capabilities['semantic_topology']['controlled_history_native_acceptance'].startswith('bounded_production_native_pass'), 'bounded producer native evidence is explicit')
    check('generic_changed_history_still_unsupported' in capabilities['semantic_topology']['native_history_backend'], 'generic changed-history limitation retained'); check('authenticated_local_record' in capabilities['semantic_topology']['native_history_backend'], 'caller history is not trusted as producer evidence')
    check(capabilities['fill_surface_contract']['source_implementation'] is True, 'fill support source contract public')
    check('Curvature_failed' in capabilities['fill_surface_contract']['native_acceptance'] and 'not_natively_recertified' in capabilities['fill_surface_contract']['native_acceptance'], 'Curvature failure retained and release native acceptance unrun')
    check('ambiguous_two_source_faces_unavailable' in capabilities['fill_surface_contract']['native_adapter'], 'two support face native limitation public')
    check(capabilities['fill_surface_contract']['geometric_G2_acceptance'].startswith('failed_historical32_') and 'current_release_not_run' in capabilities['fill_surface_contract']['geometric_G2_acceptance'], 'saved controls do not claim G2')
    check(capabilities['boundary_surface_contract']['source_implementation'] is True, 'Boundary unified source execution contract public')
    check(capabilities['boundary_surface_contract']['native_acceptance']=='historical33_bounded_two_planar_single_spline_normal_native_and_public_pass; current_release_requires_fresh_native_acceptance; complex_boundaries_not_certified', 'Changed executor requires fresh native Boundary acceptance')
    basis=capabilities['boundary_surface_contract']['validation_basis']
    check(basis['executor_dll_sha256']!=sha(plugin/'runtime/executor/CadModeling.Executor.SolidWorks.dll') and 'historical only' in basis['condition'], 'Historical Boundary evidence cannot recertify changed executor')
    audit = root/basis['audit']
    if audit.is_file():
        check(basis['audit_sha256']==sha(audit), 'Boundary declared acceptance audit immutable hash matches')
    else:
        check(len(basis['audit_sha256']) == 64 and all(c in '0123456789abcdef' for c in basis['audit_sha256']),
              'Historical Boundary audit digest retained in contract; native artifact is not public test input')
    check('complete_control_nets_knots_weights_orientation_ordered_coedge_identities' in capabilities['semantic_topology']['saved_revision_certification'], 'Complete spline definition required for revision certification')
    check(any('one_outer_loop' in limit for limit in capabilities['semantic_topology']['saved_revision_spline_limits']), 'Periodic and multi-loop spline scope stays unsupported')
    check('spheres_require_actual_center_radius_orientation_and_face_edge_incidence' in capabilities['semantic_topology']['saved_revision_certification'], 'Sphere revision requires complete analytic parameters and actual edge incidence')
    check('historical34_bounded_spline6_sphere4_durable_box23_entity_public_pass' in capabilities['semantic_topology']['saved_revision_native_acceptance'] and 'current_release_requires_fresh_native_acceptance' in capabilities['semantic_topology']['saved_revision_native_acceptance'], 'Changed spline implementation requires separate current native proof')
    check(any('TrimByD2_getter_unavailable' in x for x in capabilities['boundary_surface_contract']['limits']), 'Boundary trim getter limit public')
    check(any('unresolved_natively' in x for x in capabilities['boundary_surface_contract']['limits']), 'Boundary influence discrepancy public')
    inspection_schema = json.dumps(next(t for t in listing['tools'] if t['name']=='cad_inspect_model')['inputSchema'],ensure_ascii=False)
    for field in ['semantic', 'semantic_key', 'root_token', 'root_owner_persistent_reference', 'root_configuration', 'allowed_operation_ids', 'require_persistent_identity']:
        check('"'+field+'"' in inspection_schema, 'public inspection schema '+field)
    check('"topology_history"' not in inspection_schema and '"snapshots"' not in inspection_schema, 'caller cannot submit history as native inspection evidence')
    schema = json.dumps(listing, ensure_ascii=False)
    for field in ['bore_diameter_mm','maximum_cut_diameter_mm']:
        check('"'+field+'"' in schema,'public nominal internal thread schema '+field)
    internal_fixture=json.loads((root/'tests/fixtures/local-integration/internal-nominal-thread.json').read_text(encoding='utf-8'))
    internal_compilation=call('cad_create_model_plan',dict(draft=internal_fixture,nativeOutputPath=str(output/'internal-uncreated.SLDPRT')))
    check(internal_compilation['success'],'public explicit nominal internal thread compiles')
    thread_options=internal_compilation['plan']['operations'][-1]['options']['physical_thread']
    check(thread_options['location']=='Internal' and thread_options['bore_diameter_mm']==8.5 and thread_options['maximum_cut_diameter_mm']==10.123797625,
          'public internal thread keeps independent physical dimensions')
    internal_preview=call('cad_build_model',dict(irJson=internal_compilation['ir_json'],dryRun=True))
    check(internal_preview['success'] and internal_preview['status']=='dry_run_passed','internal thread public dry run never starts native executor')
    for label,field,value in [('missing-bore','bore_diameter_mm',None),('missing-cut-envelope','maximum_cut_diameter_mm',None),('wrong-library','profile_path',str(output/'Metric Die.SLDLFP'))]:
        invalid_internal=copy.deepcopy(internal_fixture);invalid_internal['operations'][-1]['feature']['physical_thread'][field]=value
        invalid_result=call('cad_create_model_plan',dict(draft=invalid_internal,nativeOutputPath=str(output/(label+'.SLDPRT'))))
        check(not invalid_result['success'],'public internal thread rejects '+label)
    for field in ['feature_suppression','feature_name','suppressed']:
        check('"'+field+'"' in schema,'feature suppression schema '+field)
    check('complete_feature_inventory' in capabilities['design_intent_authoring']['feature_suppression'],'complete suppression readback contract public')
    for field in ['dimension_inputs','dimension_input_values','equation_values','function','arguments','Dimension','Negate','Power']:
        check('"'+field+'"' in schema, 'expression public schema '+field)
    check('AutomaticSolveOrder' in capabilities['design_intent_authoring']['solve_order'], 'automatic solve requirement public')
    for field in ['design_intent', 'global_variables', 'equations', 'configurations', 'active_configuration',
                  'create_from_configuration', 'reuse_existing', 'expected_value', 'replace_existing', 'require_fully_defined_sketches']:
        check('"'+field+'"' in schema, 'public schema '+field)
    assembly_schema = json.dumps(next(t for t in listing['tools'] if t['name']=='cad_build_assembly'), ensure_ascii=False)
    for field in ['mobility', 'expected_basis', 'relative_to_component_id', 'source_literal', 'direction', 'point_mm',
                  'pitch_mm_per_radian', 'evaluation_point_mm', 'characteristic_length_mm', 'subspace_tolerance']:
        check('"'+field+'"' in assembly_schema, 'public assembly schema '+field)
    draft = json.loads((plugin/'skills/auto-solidworks/references/examples/design-intent-plate.json').read_text(encoding='utf-8'))
    compilation = call('cad_create_model_plan', dict(draft=draft, nativeOutputPath=str(output/'uncreated.SLDPRT')))
    check(compilation['success'], 'public design-intent fixture compiles offline')
    def same_requested_fields(actual, requested):
        if isinstance(requested, dict):
            return isinstance(actual, dict) and all(k in actual and same_requested_fields(actual[k], v) for k, v in requested.items())
        if isinstance(requested, list):
            return isinstance(actual, list) and len(actual)==len(requested) and all(same_requested_fields(a, b) for a, b in zip(actual, requested))
        return actual == requested
    check(same_requested_fields(compilation['plan']['design_intent'], draft['design_intent']), 'all requested design intent fields preserved in public plan')
    dry_run = call('cad_build_model', dict(irJson=compilation['ir_json'], dryRun=True))
    check(dry_run['success'] and dry_run['status']=='dry_run_passed', 'public dry run passes without executor')
    feature_draft=copy.deepcopy(draft)
    feature_draft['design_intent']['configurations'][1]['feature_suppression']=[dict(feature_name='圆角',suppressed=True)]
    feature_plan=call('cad_create_model_plan',dict(draft=feature_draft,nativeOutputPath=str(output/'feature.SLDPRT')))
    check(feature_plan['success'],'feature suppression typed compile; target existence remains native preflight')
    check(feature_plan['plan']['design_intent']['configurations'][1]['feature_suppression']==feature_draft['design_intent']['configurations'][1]['feature_suppression'],'suppression field preserved')
    preview=call('cad_build_model',dict(irJson=feature_plan['ir_json'],dryRun=True))
    check(preview['success'] and preview['status']=='dry_run_passed','feature suppression dry run')
    for label,target in [('required-sketch','尺寸轮廓'),('dimension-owner','凸台')]:
        bad_feature=copy.deepcopy(feature_draft);bad_feature['design_intent']['configurations'][1]['feature_suppression'][0]['feature_name']=target
        invalid_feature=call('cad_create_model_plan',dict(draft=bad_feature,nativeOutputPath=str(output/(label+'.SLDPRT'))))
        check(not invalid_feature['success'],'suppression conflict rejected '+label)
    tampered=json.loads(feature_plan['ir_json']);tampered['design_intent']['configurations'][1]['feature_suppression']*=2
    invalid_feature=call('cad_build_model',dict(irJson=json.dumps(tampered,ensure_ascii=False),dryRun=True))
    check(not invalid_feature['success'],'duplicate suppression rejected by dry run')
    expression_draft=json.loads((plugin/'skills/auto-solidworks/references/examples/design-expression-plate.json').read_text(encoding='utf-8'))
    expression_plan=call('cad_create_model_plan',dict(draft=expression_draft,nativeOutputPath=str(output/'expression.SLDPRT')))
    check(expression_plan['success'], 'configuration function and dimension input public compile')
    check(same_requested_fields(expression_plan['plan']['design_intent'],expression_draft['design_intent']), 'all expression fields preserved')
    preview=call('cad_build_model',dict(irJson=expression_plan['ir_json'],dryRun=True))
    check(preview['success'] and preview['status']=='dry_run_passed','expression public dry run')
    for label in ['wrong-config-value','wrong-input','unknown-function','mixed-cycle','trig-unit']:
        bad_expression=copy.deepcopy(expression_draft)
        intent=bad_expression['design_intent']
        if label=='wrong-config-value': intent['configurations'][1]['equation_values'][0]['value']['value']=101
        elif label=='wrong-input': intent['dimension_inputs'][0]['dimension_name']='missing@sketch'
        elif label=='unknown-function': intent['global_variables'][0]['expression']=dict(kind='Function',function='999',arguments=[])
        elif label=='mixed-cycle': intent['global_variables'][0]['expression']=dict(kind='Dimension',dimension_name=intent['equations'][0]['dimension_name'])
        else: intent['global_variables'][-1]['expression']['arguments'][0]['literal']['unit']='Unitless'
        # Invalid enums can be rejected by the tool binder; exercise typed invalid AST through known functions instead.
        if label=='unknown-function': intent['global_variables'][0]['expression']=dict(kind='Function',function='Sqrt',arguments=[])
        rejected=call('cad_create_model_plan',dict(draft=bad_expression,nativeOutputPath=str(output/(label+'.SLDPRT'))))
        check(not rejected['success'],'public expression reject '+label)
    tampered=json.loads(expression_plan['ir_json']);tampered['design_intent']['configurations'][1]['global_variables'][0]['expected_value']['value']=51
    rejected=call('cad_build_model',dict(irJson=json.dumps(tampered,ensure_ascii=False),dryRun=True))
    check(not rejected['success'],'dry run rejects tampered scoped variable expectation')
    bad = copy.deepcopy(draft)
    bad['design_intent']['equations'][0]['expected_value']['value'] = 81
    invalid = call('cad_create_model_plan', dict(draft=bad, nativeOutputPath=str(output/'invalid.SLDPRT')))
    check(not invalid['success'], 'public compiler rejects wrong independent value')
    bad = copy.deepcopy(draft)
    bad['design_intent']['global_variables'][0]['expression'] = dict(kind='Variable', variable='倍宽')
    invalid = call('cad_create_model_plan', dict(draft=bad, nativeOutputPath=str(output/'cycle.SLDPRT')))
    check(not invalid['success'], 'public compiler rejects cyclic globals')
    ir = json.loads(compilation['ir_json'])
    ir['design_intent']['equations'][0]['expected_value']['value'] = 81
    invalid = call('cad_build_model', dict(irJson=json.dumps(ir, ensure_ascii=False), dryRun=True))
    check(not invalid['success'], 'public dry-run revalidates tampered design intent')
    ir = json.loads(compilation['ir_json'])
    ir['operations'].append(dict(type='native_feature', id='strict_invalid', name='严格选择负例',
        depends_on=[ir['operations'][-1]['id']], options=dict(kind='Fillet', radius_mm=1,
        selections=[dict(kind='Edge', require_persistent_identity=True)])))
    invalid = call('cad_build_model', dict(irJson=json.dumps(ir,ensure_ascii=False), dryRun=True))
    check(not invalid['success'], 'public dry-run rejects strict selection without persistent identity')
    for label, boundary in [
        ('G2-without-support', dict(edge=dict(kind='Edge'), contact='Curvature')),
        ('Contact-with-support', dict(edge=dict(kind='Edge'), contact='Contact', support_face=dict(kind='Face'))),
        ('G1-ambiguous-support', dict(edge=dict(kind='Edge'), contact='Tangent', support_face=dict(kind='Face', all_matches=True)))]:
        ir = json.loads(compilation['ir_json'])
        ir['operations'].append(dict(type='native_feature', id='invalid_fill', name='填充支撑负例',
            depends_on=[ir['operations'][-1]['id']], options=dict(kind='SurfaceFill',surface=dict(fill_boundaries=[boundary]))))
        rejected = call('cad_build_model', dict(irJson=json.dumps(ir,ensure_ascii=False),dryRun=True))
        check(not rejected['success'], 'public fill support contract rejection '+label)
    bowed = json.loads((plugin/'skills/auto-solidworks/references/examples/surface-boundary.json').read_text(encoding='utf-8'))
    spatial = json.loads((root/'tests/fixtures/local-integration/spatial-boundary.json').read_text(encoding='utf-8'))
    for label, fixture in [('bowed',bowed),('spatial',spatial)]:
        built = call('cad_create_model_plan',dict(draft=fixture,nativeOutputPath=str(output/(label+'.SLDPRT'))))
        check(built['success'] and any(o.get('options',{}).get('kind')=='SurfaceBoundary' for o in built['plan']['operations']), 'public Boundary compile '+label)
        preview = call('cad_build_model',dict(irJson=built['ir_json'],dryRun=True))
        check(preview['success'] and preview['status']=='dry_run_passed', 'public Boundary dry run '+label)
    broken = copy.deepcopy(spatial)
    broken['operations'][2]['feature']['spatial_curve']['points_mm'][0]['x']=100
    rejected = call('cad_create_model_plan',dict(draft=broken,nativeOutputPath=str(output/'boundary-gap.SLDPRT')))
    check(not rejected['success'], 'public Boundary compiler refuses unmatched spatial corner')
    simple = call('cad_create_model_plan', dict(text='80 x 50 x 10 cm plate', nativeOutputPath=str(output/'units.SLDPRT')))
    check(simple['success'] and simple['plan']['acceptance']['expected_bounding_box_mm']==dict(x=800, y=500, z=100), 'legacy text unit conversion retained')
    for label, text, operation_id in [
        ('blind-plate', '80 x 50 x 20 mm plate centered blind hole diameter 10 mm depth 6 mm', 'text_center_hole_cut'),
        ('blind-cylinder', 'cylinder diameter 40 mm height 60 mm centered blind hole diameter 10 mm depth 15 mm', 'text_center_hole_cut'),
        ('open-shell', '80 x 50 x 20 mm plate shell wall thickness 2 mm open top', 'text_shell'),
        ('closed-shell', 'cylinder diameter 40 mm height 60 mm shell thickness 2 mm closed', 'text_shell')]:
        compiled = call('cad_create_model_plan', dict(text=text, nativeOutputPath=str(output/(label+'.SLDPRT'))))
        check(compiled['success'] and any(o['id']==operation_id for o in compiled['plan']['operations']), 'public text operation '+label)
        if label == 'blind-plate':
            cut = next(o for o in compiled['plan']['operations'] if o['id']==operation_id)
            check(cut['end_condition']=='Blind' and cut['depth_mm']==6, 'public blind depth never substituted through')
        preview = call('cad_build_model', dict(irJson=compiled['ir_json'], dryRun=True))
        check(preview['success'] and preview['status']=='dry_run_passed', 'public text dry run '+label)
    for label, text in [
        ('no-depth', '80 x 50 x 20 mm plate centered blind hole diameter 10 mm'),
        ('too-deep', '80 x 50 x 20 mm plate centered blind hole diameter 10 mm depth 20 mm'),
        ('contradiction', '80 x 50 x 20 mm plate centered blind through hole diameter 10 mm depth 6 mm'),
        ('no-opening', '80 x 50 x 20 mm plate shell thickness 2 mm'),
        ('unmodeled-thread', '80 x 50 x 20 mm plate centered blind hole diameter 10 mm depth 6 mm thread M12')]:
        rejected = call('cad_create_model_plan', dict(text=text, nativeOutputPath=str(output/(label+'.SLDPRT'))))
        check(not rejected['success'], 'public text rejection '+label)
    tracked = json.loads((root/'tests/fixtures/local-integration/controlled-box-history.json').read_text(encoding='utf-8'))
    # This labeled placeholder only exercises path/plan preflight. The forbidden
    # executor ensures it can never be opened or treated as a native CAD model.
    inputs = output/'offline-input-placeholders'
    inputs.mkdir()
    placeholder = inputs/'NOT-A-CAD-MODEL.SLDPRT'
    placeholder.write_bytes(b'OFFLINE PATH PLACEHOLDER ONLY; NOT A SOLIDWORKS MODEL\n')
    tracked['source_model_path'] = str(placeholder)
    tracked['output']['native_path'] = str(output/'tracked.SLDPRT')
    dry = call('cad_build_model',dict(irJson=json.dumps(tracked),dryRun=True))
    check(dry['success'] and dry['status']=='dry_run_passed','public bounded history dry run')
    for label, mutate in [
        ('propagation',lambda p:p['operations'][1]['options'].update(tangent_propagation=True)),
        ('wrong-dimension',lambda p:p['box_edge_history'].update(thickness_dimension_name='wrong@基础拉伸')),
        ('overwrite',lambda p:p['output'].update(overwrite_allowed=True))]:
        bad = copy.deepcopy(tracked); mutate(bad)
        rejected = call('cad_build_model',dict(irJson=json.dumps(bad),dryRun=True))
        check(not rejected['success'],'public bounded history rejects '+label)
    check(not list(output.glob('*.SLDPRT')), 'no native model created')
    check(not (output/'unexpected-executor-logs').exists(), 'executor never started')
    check(len(listing['tools'])==18, 'public tool count retained')
    (output/'summary.json').write_text(json.dumps(dict(status='pass', passed=len(checks), checks=checks,
        evidence_layer='source_runtime_mcp_schema_compile_and_dry_run_only', native_acceptance='not_run',
        source_files=len(identity['source_hashes']), runtime_files=len(identity['runtime_hashes']),
        executor_dll_sha256=sha(plugin/'runtime/executor/CadModeling.Executor.SolidWorks.dll'),
        actual_calls=calls), ensure_ascii=False, indent=2), encoding='utf-8')
    print('Offline public checks passed:', len(checks), flush=True)
finally:
    proc.stdin.close()
    proc.wait(timeout=15)
    stderr.close()
