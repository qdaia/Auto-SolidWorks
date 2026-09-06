import hashlib
from pathlib import Path

def check_references(call,root):
    path=sorted((root/'artifacts').glob('*/tube_chamfer.SLDPRT'))[-1]
    before=hashlib.sha256(path.read_bytes()).hexdigest()
    query=dict(kind='Face',feature_id='tube',geometry='Cylinder',radius_mm=30,position_mm=dict(x=30,y=0,z=50))
    def inspect(q):return call('cad_inspect_model',{'inputPath':str(path),'queries':[q]})
    initial=inspect(query);assert initial['success'],initial
    reference=initial['entities'][0]['persistent_reference'];assert reference
    restored=inspect(dict(kind='Face',persistent_reference=reference));assert restored['success'],restored
    assert restored['entities'][0]['radius_mm']==30
    wrong_owner=inspect(query|dict(feature_id='bore_chamfer',persistent_reference=reference));assert not wrong_owner['success']
    stale=inspect(query|dict(persistent_reference='AQID'));assert stale['success'],stale
    assert hashlib.sha256(path.read_bytes()).hexdigest()==before
    profiles=call('cad_list_weldment_profiles',{})['profiles'];assert profiles
    square=next(p for p in profiles if p['name']=='square-rods')
    configurations=call('cad_inspect_model',{'inputPath':square['path']});assert configurations['success']
    assert 'SQUARE BAR 10' in configurations['configurations']
    return dict(success=True,checks=['persistent_face_roundtrip','owner_mismatch_rejected','stale_reference_semantic_recovery','read_only_source_unchanged','native_profile_discovery_and_configurations'])
