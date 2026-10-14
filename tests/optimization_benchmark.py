import argparse,json,statistics,time
from pathlib import Path
from optimization_native import Session,ROOT,save
parser=argparse.ArgumentParser();parser.add_argument('--out',type=Path,required=True);args=parser.parse_args()
out=args.out.resolve();out.mkdir(parents=True,exist_ok=False)
session=Session(ROOT/'plugins/auto-solidworks',out/'trace');rows=[]
try:
    base=json.loads(Path(r'C:\Users\csj17\Desktop\CODEX TEST\solidworks-real-comparison-20261013-183436\evidence\draft.json').read_text(encoding='utf-8'))
    for round_index in range(5):
        # Alternate order to reduce simple warm-up and time-order bias.
        for mode in (('default','declared') if round_index%2==0 else ('declared','default')):
            name=mode+'-'+str(round_index+1);native=out/(name+'.sldprt')
            draft=json.loads(json.dumps(base))
            for op in draft['operations']:
                if op['type']=='ProfileSketch':op['require_editable']=True
            if mode=='declared':
                draft['preview_views']=['isometric','front']
                draft['preview_scope_reason']='This source-defined planar flange is fully checked by inventory, all hole walls and positions; front and isometric show the full silhouette and every through opening.'
                draft['recovery']=dict(enabled=True,checkpoint_mode='Explicit',after_operation_ids=[])
            start=time.perf_counter()
            plan=session.call('cad_create_model_plan',dict(draft=draft,nativeOutputPath=str(native),responseDetail='full'));save(out,name+'-plan',plan);assert plan['success'],plan
            compiled=time.perf_counter()
            result=session.call('cad_build_model',dict(planId=plan['plan_id'],responseDetail='full'));ended=time.perf_counter();save(out,name+'-build',result)
            row=dict(round=round_index+1,mode=mode,compile_seconds=compiled-start,build_seconds=ended-compiled,total_seconds=ended-start,
                success=result['success'],parametric_status=result.get('parametric_status'),request_id=result.get('request_id'),
                trace_path=result.get('performance_trace_path'),native_path=str(native),
                checkpoint_count=sum(e['stage']=='checkpoint_saved' for e in result.get('evidence',[])),
                preview_count=sum(e['stage']=='preview' for e in result.get('evidence',[])))
            rows.append(row);save(out,'runs',rows);print(json.dumps(row,ensure_ascii=False),flush=True)
            assert result['success'] and result['parametric_status']=='passed' and result['verification']['passed'] and result['verification']['whole_model_inventory_checked'],result
    summary={m:dict(n=5,median_total_seconds=statistics.median(r['total_seconds'] for r in rows if r['mode']==m),
        min_total_seconds=min(r['total_seconds'] for r in rows if r['mode']==m),max_total_seconds=max(r['total_seconds'] for r in rows if r['mode']==m)) for m in ('default','declared')}
    summary['scope']='Same optimized runtime and flange source, same final geometry, editable sketches, save/reopen and native-body gates; explicit front/isometric coverage vs five default previews, explicit no intermediate checkpoint vs automatic prefix. No agent reasoning, OCR or general CAD speed claim.'
    summary['relative_median_reduction']=1-summary['declared']['median_total_seconds']/summary['default']['median_total_seconds']
    save(out,'summary',summary);print(json.dumps(summary,ensure_ascii=False),flush=True)
finally:
    (out/'mcp.log').write_text(''.join(session.logs),encoding='utf-8');session.close()
