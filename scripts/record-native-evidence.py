"""Bundle passed saved-file inspections; never infer native acceptance from offline tests."""
import argparse,datetime,hashlib,json
from pathlib import Path

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument('--verification',type=Path,required=True);parser.add_argument('--readback-name',default='delivery-readback');parser.add_argument('--evidence-directory',default='native-evidence');args=parser.parse_args()
verification=args.verification.resolve();package=root/'plugins/auto-solidworks'
readback=verification/args.readback_name;summary=json.loads((readback/'summary.json').read_text(encoding='utf-8'))
reviews={r['case']:r for r in json.loads((verification/'visual-review/manifest.json').read_text(encoding='utf-8'))}
assert len(summary)==13 and all(r['passed'] for r in summary),'Current runtime saved-file matrix incomplete'
index=dict(schema_version=1,runtime_version=json.loads((package/'.codex-plugin/plugin.json').read_text(encoding='utf-8'))['version'],
    recorded_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),runtime_hashes={},task_families=[],
    limits=['Only retained explicit text geometry; no drawing interpretation, assembly, complex surface or simulation certification.',
        'Retained optimized geometry was revalidated under the final delivery runtime. Current acceptance is saved-file/native readback within the listed scopes.',
        'Visual review is separate from native file-writing and does not certify full drawing/BREP equivalence.'])
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
for runtime in ('mcp/AutoSolidWorks.ModelingMcp.dll','mcp/CadModeling.Core.dll','mcp/CadModeling.Ir.dll',
    'executor/CadModeling.Executor.SolidWorks.dll','executor/CadModeling.Core.dll','executor/CadModeling.Ir.dll'):
    relative='runtime/'+runtime;index['runtime_hashes'][relative]=sha(package/relative)
destination=package/'skills/auto-solidworks/references'/args.evidence_directory;destination.mkdir(exist_ok=False)
for row in summary:
    folder=readback/row['case']
    extended=verification/'extended-probes'
    receipt_source=extended/'inspection.json' if row['case']=='recipe-flange' and (extended/'inspection.json').is_file() else folder/'inspection.json'
    request_source=extended/'request.json' if receipt_source.parent==extended else folder/'request.json'
    receipt=json.loads(receipt_source.read_text(encoding='utf-8'))
    build=json.loads((folder/'build.json').read_text(encoding='utf-8'))
    assert receipt['success'] and receipt['model_reopened'] and receipt['capture_complete'] and receipt['rebuild_succeeded']
    assert receipt['body_validity']['passed'] and receipt['geometry_status']=='passed' and receipt['parametric_status']=='passed'
    assert receipt['verification']['passed'] and receipt['verification']['whole_model_inventory_checked']
    assert all(p['passed'] and p['trial_geometry_passed'] and p['restore_geometry_passed'] and p['model_file_unchanged'] for p in receipt['editability_probes'])
    model=Path(receipt['input_path']);assert sha(model).upper()==receipt['model_sha256'].upper()
    target=destination/row['case'];target.mkdir()
    files=[]
    def copy(source,name=None):
        output=target/(name or source.name);output.write_bytes(source.read_bytes())
        relative=output.relative_to(package).as_posix();files.append(dict(path=relative,sha256=sha(output)));return relative
    model_path=copy(model);inspection_path=copy(receipt_source)
    copy(request_source)
    for name in ('plan.json','build.json'):copy(folder/name)
    for e in build['evidence']:
        if e['stage']=='preview' and e['passed']:copy(Path(e['data']['path']))
    review=reviews[row['case']]
    assert review['review_status']=='reviewed','Manual visual review not completed'
    assert all(sha(Path(f['path']))==f['sha256'] for f in review['files']),'Reviewed preview has changed'
    reviewed_hashes={f['sha256'] for f in review['files']}
    assert all(sha(Path(e['data']['path'])) in reviewed_hashes for e in build['evidence'] if e['stage']=='preview' and e['passed']), 'Current build preview has not been visually reviewed'
    copy(Path(review['contact_sheet']),'reviewed-previews.jpg')
    review_file=folder/'visual-review.json';review_file.write_text(json.dumps(review,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    visual_review_path=copy(review_file)
    revisions={e['data']['solidworks_revision'] for e in build['evidence'] if e['stage']=='executor_health' and e['passed']}
    assert len(revisions)==1,'Native revision not captured'
    index['task_families'].append(dict(id=row['case'],solidworks_revision=revisions.pop(),
        scope=['saved_native_reopen_rebuild_body_validity','whole_model_inventory','fully_defined_editable_consumed_design_sketches']+
            [p['probe_id']+':trial_actual_geometry_and_restoration' for p in receipt['editability_probes']],
        model_path=model_path,inspection_path=inspection_path,visual_review_path=visual_review_path,files=files))
(package/'skills/auto-solidworks/references/current-native-evidence.json').write_text(json.dumps(index,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(dict(runtime_version=index['runtime_version'],native_task_scopes=len(index['task_families']),hashed_files=sum(len(f['files']) for f in index['task_families']))))
