"""Exercise current native index rejection using isolated package evidence, restoring every byte."""
import argparse,hashlib,json,os
from pathlib import Path
from optimization_native import Session,ROOT,save
parser=argparse.ArgumentParser();parser.add_argument('--out',type=Path,required=True);args=parser.parse_args()
out=args.out.resolve();out.mkdir(parents=True,exist_ok=False);package=ROOT/'plugins/auto-solidworks'
index_path=package/'skills/auto-solidworks/references/current-native-evidence.json';original=index_path.read_bytes();index=json.loads(original)
target=index['task_families'][0];model=package/target['model_path'];model_bytes=model.read_bytes()
receipt=package/target['inspection_path'];receipt_bytes=receipt.read_bytes();records=[]
session=Session(package,out/'trace')
def check(name,assertion):
    caps=session.call('cad_get_capabilities',dict(responseDetail='full'));save(out,name,caps)
    assert assertion(caps['current_native_evidence']),caps
    records.append(dict(case=name,passed=True));print(json.dumps(records[-1]),flush=True)
def family_bad(e):return next(f for f in e['task_families'] if f['id']==target['id'])['status']=='stale_or_unverifiable'
def write(value):index_path.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
try:
    check('valid-index',lambda e:e['status']=='scoped_native_verified' and len(e['task_families'])==13 and all(f['status']=='native_verified_scoped' for f in e['task_families']))
    changed=json.loads(original);changed['runtime_version']='older-version';write(changed)
    check('wrong-version-rejected',lambda e:e['status']=='stale_or_unverifiable')
    changed=json.loads(original);changed['runtime_hashes']['runtime/executor/CadModeling.Executor.SolidWorks.dll']='0'*64;write(changed)
    check('wrong-runtime-identity-rejected',lambda e:e['status']=='stale_or_unverifiable')
    index_path.write_bytes(original);model.write_bytes(model_bytes+b'OFFLINE_TAMPER_TEST')
    check('changed-model-rejected',family_bad);model.write_bytes(model_bytes)
    changed=json.loads(original);bad=json.loads(receipt_bytes);bad['verification']['whole_model_inventory_checked']=False
    receipt.write_text(json.dumps(bad,ensure_ascii=False),encoding='utf-8')
    digest=hashlib.sha256(receipt.read_bytes()).hexdigest()
    for entry in changed['task_families'][0]['files']:
        if entry['path']==target['inspection_path']:entry['sha256']=digest
    write(changed);check('rehash-cannot-substitute-incomplete-native-inventory',family_bad);receipt.write_bytes(receipt_bytes)
    changed=json.loads(original);changed['task_families'][0]['files'][0]['path']='../../outside-evidence.sldprt';write(changed)
    check('outside-package-path-rejected',family_bad)
    index_path.write_bytes(b'{broken-json');check('malformed-index-rejected',lambda e:e['status']=='stale_or_unverifiable')
finally:
    index_path.write_bytes(original);model.write_bytes(model_bytes);receipt.write_bytes(receipt_bytes)
    assert index_path.read_bytes()==original and model.read_bytes()==model_bytes and receipt.read_bytes()==receipt_bytes
    (out/'mcp.log').write_text(''.join(session.logs),encoding='utf-8');session.close()
save(out,'summary',records)
