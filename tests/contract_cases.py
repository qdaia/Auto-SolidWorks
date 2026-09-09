import copy, json
from cases import sketch, boss, rect, draft

def check_contracts(call,root,output):
    source=output/'synthetic-contract-source.png'
    import base64
    source.write_bytes(base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII='))
    base=draft('binding_case',[sketch('profile',[rect(25.4,20)]),boss('body','profile',10)],
        drawing_context=dict(source_path=str(source),views=[dict(id='front',kind='Front')],dimensions=[
            dict(id='width',operation_id='profile',parameter_path='primitives.0.width_mm',value=1,unit='Inch',status='Stated',source_literal='1 inch',view_ids=['front'])]))
    base['drawing_context']['features']=[dict(id='plate',source_literal='Contract fixture width 1 inch',view_ids=['front'],operation_ids=['profile','body'],
        critical_parameters=[dict(operation_id='profile',parameter_path='primitives.0.width_mm',dimension_id='width')],verification_check_ids=['width_check'])]
    base['verification']=dict(bounds=[dict(id='width_check',source_literal='Contract fixture 25.4 x 20 x 10 mm',source_dimension_ids=['width'],size_mm=dict(x=25.4,y=20,z=10))],
        bindings=[dict(dimension_id='width',check_id='width_check',parameter_path='size_mm.x')])
    def compile(d):return call('cad_create_model_plan',{'nativeOutputPath':str(output/'contract.SLDPRT'),'draft':d})
    checks={}
    result=compile(base);assert result['success'],result
    assert result['plan']['drawing_context']['dimensions'][0]['value']==1
    checks['inch_dimension_binding']=True
    mismatch=copy.deepcopy(base);mismatch['drawing_context']['dimensions'][0]['value']=2
    assert not compile(mismatch)['success'];checks['conflicting_dimension_rejected']=True
    wrong_unit=copy.deepcopy(base);wrong_unit['drawing_context']['dimensions'][0].update(value=25.4,unit='Degree')
    assert not compile(wrong_unit)['success'];checks['angle_bound_to_length_rejected']=True
    missing=copy.deepcopy(base);missing['drawing_context']['dimensions'][0]['operation_id']='missing'
    assert not compile(missing)['success'];checks['missing_operation_rejected']=True
    unknown=copy.deepcopy(base);unknown['drawing_context']['dimensions'][0]['status']='Unknown'
    assert not compile(unknown)['success'];checks['unknown_bound_dimension_rejected']=True
    derived=copy.deepcopy(base);derived['drawing_context']['dimensions'][0]['status']='Derived'
    assert not compile(derived)['success'];checks['unexplained_derivation_rejected']=True
    caps=call('cad_get_capabilities',{})
    assert 'WeldmentMember' in caps['native_feature_kinds'] and 'sldasm' in caps['output_formats']
    checks['capabilities_match_upgrade']=True
    return dict(success=True,checks=checks)
