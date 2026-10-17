"""Local composition CLI using the installed Auto SolidWorks MCP contracts."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time

from composable_model import ModelSession, read_json, write_new, file_identity, split_draft


class McpClient:
    def __init__(self, plugin, output):
        self.output = Path(output)
        self.output.mkdir(parents=True, exist_ok=False)
        self.counter = 0
        self.messages = queue.Queue()
        self.timings = []
        self.stderr = (self.output/'stderr.log').open('x', encoding='utf-8')
        env = os.environ.copy()
        env['AUTO_SOLIDWORKS_ARTIFACT_DIRECTORY'] = str(self.output/'artifacts')
        self.process = subprocess.Popen(
            ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
             str(Path(plugin)/'scripts/run-auto-solidworks-mcp.ps1')],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr,
            text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
        threading.Thread(target=self.reader, daemon=True).start()
        try:
            self.rpc('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                                   'clientInfo': {'name': 'composable-source-workflow', 'version': '1'}})
            self.process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
            self.process.stdin.flush()
        except Exception:
            self.close()
            raise

    def reader(self):
        for line in self.process.stdout:
            try:
                self.messages.put(json.loads(line))
            except json.JSONDecodeError:
                pass
        self.messages.put({'transport_closed': True})

    def rpc(self, method, params, timeout=60):
        self.counter += 1
        request = {'jsonrpc': '2.0', 'id': self.counter, 'method': method, 'params': params}
        write_new(self.output/f'{self.counter:03d}-request.json', request)
        started = time.perf_counter()
        self.process.stdin.write(json.dumps(request, ensure_ascii=False, allow_nan=False)+'\n')
        self.process.stdin.flush()
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError('Outcome unknown; retain request journal and query execution status before any retry')
            try:
                result = self.messages.get(timeout=remaining)
            except queue.Empty:
                raise TimeoutError('Outcome unknown; inspect request journal; do not replay mutations') from None
            if result.get('transport_closed'):
                raise RuntimeError('MCP closed; outcome may be unknown; do not replay mutations')
            if result.get('id') == self.counter:
                write_new(self.output/f'{self.counter:03d}-response.json', result)
                self.timings.append({'method': method, 'tool': params.get('name'),
                                     'seconds': time.perf_counter()-started})
                if 'error' in result:
                    raise RuntimeError(str(result['error']))
                return result['result']

    def call(self, name, args, timeout=60):
        result = self.rpc('tools/call', {'name': name, 'arguments': args}, timeout)
        if result.get('isError'):
            raise RuntimeError(str(result))
        data = result.get('structuredContent')
        if data is None:
            data = json.loads(next(item['text'] for item in result['content'] if item['type']=='text'))
        return data.get('result', data)

    def close(self):
        if self.process.stdin and not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            # Only this helper's MCP process; never terminate SolidWorks or its executor.
            self.process.terminate()
            self.process.wait(timeout=10)
        self.stderr.close()


def prepare_packet(inventory_path, inventory_sha, output):
    spec = importlib.util.spec_from_file_location('prepare_review', Path(__file__).with_name('prepare-review-workspace.py'))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    result = helper.prepare(inventory_path, inventory_sha, output)
    groups = {}
    for region in result['raw_regions']:
        # Byte equality is conservative: every nonidentical image remains a separate review.
        key = region['image_sha256'].lower()
        group = groups.setdefault(key, {'image_path': region['image_path'], 'sha256': key, 'region_ids': []})
        group['region_ids'].append(region['id'])
    packet = {'status': 'pending_visual_review', 'source': file_identity(result['source_path']),
              'inventory': file_identity(inventory_path), 'unique_raw_images': list(groups.values()),
              'all_regions_retained': True, 'candidate_count': result['candidate_count'],
              'original_region_count': result['region_count'], 'unique_image_count': len(groups),
              'semantic_review_complete': False,
              'instruction': 'Read original and every unique raw image; identical bytes may share visual inspection, '
                             'but decide all candidate and region IDs with their original page/view context.'}
    write_new(Path(output)/'review-packet.json', packet)
    return packet


def run_session(session, native_path, client, execute=False, dry_run=False):
    args = session.compose(native_path)
    write_new(client.output/'compiled-input.json', args)
    plan = client.call('cad_create_model_plan', args)
    result = {'bundle_revision': session.revision, 'compiled': plan.get('success') is True,
              'plan': plan, 'native_started': False, 'full_drawing_equivalence': False}
    if result['compiled'] and plan.get('plan_id') and (execute or dry_run):
        # Recheck source images immediately before dispatch, then let runtime verify plan identity.
        session.verify_sources()
        result['native_started'] = execute
        write_new(client.output/'build-dispatch.json', {'plan_id': plan['plan_id'], 'dry_run': not execute,
                  'bundle_revision': session.revision, 'state': 'dispatching_outcome_not_yet_known'})
        result['build'] = client.call('cad_build_model', {'planId': plan['plan_id'], 'dryRun': not execute},
                                      timeout=3600 if execute else 60)
    result['timings'] = client.timings
    result['visual_review_complete'] = False
    write_new(client.output/'workflow-result.json', result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    prepare = sub.add_parser('prepare')
    prepare.add_argument('inventory')
    prepare.add_argument('--sha256', required=True)
    prepare.add_argument('--output', required=True)
    ingest = sub.add_parser('ingest')
    ingest.add_argument('image')
    ingest.add_argument('--output', required=True)
    ingest.add_argument('--plugin-root', default=str(Path(__file__).resolve().parents[1]))
    init = sub.add_parser('init')
    init.add_argument('draft')
    init.add_argument('--output', required=True)
    emit = sub.add_parser('emit')
    emit.add_argument('bundle')
    emit.add_argument('--native-output', required=True)
    emit.add_argument('--output', required=True)
    readiness = sub.add_parser('readiness')
    readiness.add_argument('bundle')
    stage = sub.add_parser('emit-stage')
    stage.add_argument('bundle')
    stage.add_argument('spec', help='JSON: step_ids, context, recovery; reviewed explicit stage coverage')
    stage.add_argument('--native-output', required=True)
    stage.add_argument('--output', required=True)
    revise = sub.add_parser('revise')
    revise.add_argument('bundle')
    revise.add_argument('correction', help='JSON: expected_revision, replacements, optional context/fact_dependencies')
    revise.add_argument('--output', required=True)
    review = sub.add_parser('review')
    review.add_argument('bundle')
    review.add_argument('--revision', required=True)
    review.add_argument('--steps', nargs='+', required=True)
    review.add_argument('--note', required=True)
    review.add_argument('--output', required=True)
    run = sub.add_parser('run')
    run.add_argument('bundle')
    run.add_argument('--native-output', required=True)
    run.add_argument('--output', required=True)
    run.add_argument('--plugin-root', default=str(Path(__file__).resolve().parents[1]))
    mode = run.add_mutually_exclusive_group()
    mode.add_argument('--execute', action='store_true', help='Native modeling only when authorized by the task')
    mode.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    if args.command == 'prepare':
        result = prepare_packet(args.inventory, args.sha256, args.output)
        print(json.dumps({k:result[k] for k in ('status', 'candidate_count', 'unique_image_count')}))
        return
    if args.command == 'ingest':
        client = McpClient(args.plugin_root, args.output)
        try:
            result = client.call('cad_read_drawing', {'inputPath':str(Path(args.image).resolve(strict=True)),
                'artifactRoot':str(client.output/'drawing'), 'prepareReview':True, 'responseDetail':'full'}, timeout=600)
            write_new(client.output/'ingestion.json', result)
            if (result.get('outcome') not in ('Succeeded', 'SucceededWithWarnings')
                    or any(d.get('blocking') is True for d in result.get('diagnostics', []))
                    or not result.get('omission_inventory_path')):
                raise RuntimeError('Drawing ingestion incomplete; inspect ingestion.json before proceeding')
            packet = prepare_packet(result['omission_inventory_path'], result['omission_inventory_sha256'],
                                    client.output/'review')
            write_new(client.output/'timings.json', client.timings)
            print(json.dumps({'status':packet['status'], 'review':str(client.output/'review/review-packet.json'),
                              'diagnostics': result.get('diagnostics', [])}, ensure_ascii=False))
        finally:
            client.close()
        return
    if args.command == 'init':
        draft = read_json(args.draft)
        context = draft.get('drawing_context')
        sources = [] if context is None else [file_identity(context['source_path']),
                      file_identity(context['omission_review']['inventory_path'])]
        session = split_draft(draft, sources)
        write_new(args.output, session.bundle)
        print(json.dumps({'revision':session.revision, 'reviewed':False}))
        return
    session = ModelSession(read_json(args.bundle))
    if args.command == 'readiness':
        print(json.dumps(session.readiness(), ensure_ascii=False))
    elif args.command == 'emit-stage':
        write_new(args.output, session.compose_stage(args.native_output, **read_json(args.spec)))
    elif args.command == 'emit':
        write_new(args.output, session.compose(args.native_output))
    elif args.command == 'revise':
        correction = read_json(args.correction)
        new = session.patch(**correction) if 'changes' in correction else session.revise(**correction)
        write_new(args.output, new.bundle)
        print(json.dumps({'revision': new.revision, 'change': new.bundle['history'][-1]}))
    elif args.command == 'review':
        new = session.confirm_review(args.revision, args.steps, args.note)
        write_new(args.output, new.bundle)
        print(json.dumps({'revision': new.revision}))
    else:
        # Block unreviewed or stale inputs before starting the MCP process.
        session.compose(args.native_output)
        client = McpClient(args.plugin_root, args.output)
        try:
            result = run_session(session, args.native_output, client, args.execute, args.dry_run)
            print(json.dumps({'compiled': result['compiled'], 'native_started': result['native_started'],
                              'result': str(client.output/'workflow-result.json')}))
            if not result['compiled'] or ('build' in result and result['build'].get('success') is not True):
                raise SystemExit(2)
        finally:
            client.close()


if __name__ == '__main__':
    main()
