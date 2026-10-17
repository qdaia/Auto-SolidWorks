"""Compose reviewed source-bound steps; native contracts remain authoritative.

No COM calls, geometry inference, OCR acceptance, or native execution occurs here.
"""
import copy
import hashlib
import json
import math
from pathlib import Path

REVISION = 'composable-source-1'
CONTEXT_ARRAYS = ('dimensions', 'features', 'interpretation_reviews')
CHECK_ARRAYS = ('bounds', 'cylinder_groups', 'native_dimensions', 'surface_samples',
                'boundary_clearances', 'bindings')


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False,
                                    allow_nan=False, separators=(',', ':')).encode()).hexdigest()


def read_json(path):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError('Duplicate JSON key: ' + key)
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=pairs,
                      parse_constant=lambda x: (_ for _ in ()).throw(ValueError(x)))


def write_new(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('x', encoding='utf-8') as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2, allow_nan=False)


def file_identity(path):
    path = Path(path)
    if not path.is_absolute():
        raise ValueError('Source paths must be absolute: ' + str(path))
    path = path.resolve(strict=True)
    return {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings(item)


def unique(items, key, label):
    result = {}
    for item in items:
        identity = item.get(key)
        if not isinstance(identity, str) or not identity or identity in result:
            raise ValueError('Missing/duplicate ' + label + ': ' + str(identity))
        result[identity] = item
    return result


def ordered(graph):
    pending = {key: set(value) for key, value in graph.items()}
    if any(not deps.issubset(pending) for deps in pending.values()):
        raise ValueError('Unknown step/fact dependency')
    result = []
    while pending:
        ready = [key for key, deps in pending.items() if not deps]
        if not ready:
            raise ValueError('Cyclic step/fact dependency: ' + ', '.join(pending))
        for key in ready:
            result.append(key)
            del pending[key]
        for deps in pending.values():
            deps.difference_update(ready)
    return result


class ModelSession:
    """Immutable revisions of small steps, plus a single source dimension table."""
    def __init__(self, bundle):
        self.bundle = copy.deepcopy(bundle)
        bundle = self.bundle
        allowed = {'schema', 'draft', 'context', 'sources', 'steps', 'fact_dependencies', 'history'}
        if set(bundle) - allowed or bundle.get('schema') != REVISION:
            raise ValueError('Unknown bundle fields/schema')
        self.steps = unique(bundle['steps'], 'id', 'step')
        for step in self.steps.values():
            if set(step) - {'id', 'depends_on', 'reviewed', 'review_note', 'operations',
                            'dimensions', 'features', 'interpretation_reviews', 'verification'}:
                raise ValueError('Unknown step fields: ' + step['id'])
            if not isinstance(step.get('depends_on', []), list):
                raise ValueError('depends_on must be a list')
            if set(step.get('verification', {})) - set(CHECK_ARRAYS):
                raise ValueError('Unsupported step verification field; place global options in draft')
        draft = bundle['draft']
        if any(key in draft for key in ('operations', 'drawing_context', 'recipe')):
            raise ValueError('Operations/context belong to steps; recipe expansion is a separate route')
        if any(draft.get('verification', {}).get(key) for key in CHECK_ARRAYS):
            raise ValueError('Check collections belong to steps')
        context = bundle.get('context')
        if context is not None and any(context.get(key) for key in CONTEXT_ARRAYS):
            raise ValueError('Source collections belong to steps')
        self.dimensions = unique(self.collect('dimensions'), 'id', 'dimension')
        self.operations = unique(self.collect('operations'), 'id', 'operation')
        unique(self.collect('features'), 'id', 'feature')
        unique(self.collect('interpretation_reviews'), 'feature_id', 'interpretation review')
        checks = [check for step in self.steps.values() for kind, values in
                  step.get('verification', {}).items() if kind != 'bindings' for check in values]
        unique(checks, 'id', 'verification check')
        fact_graph = {key: [] for key in self.dimensions}
        for key, deps in bundle.get('fact_dependencies', {}).items():
            if key not in fact_graph or not isinstance(deps, list):
                raise ValueError('Unknown fact dependency owner')
            fact_graph[key] = deps
        # Infer numeric premises from the same explicit source representation sent to MCP.
        # Calculation remains in the server; do not create a second formula evaluator here.
        for key, fact in self.dimensions.items():
            deps = set(fact_graph[key]) | set(fact.get('derived_from_dimension_ids', []))
            pending = [(fact.get('expression'), 0)]
            count = 0
            while pending:
                expression, depth = pending.pop()
                if expression is None:
                    continue
                count += 1
                if count > 256 or depth > 32 or not isinstance(expression, dict):
                    raise ValueError('Invalid/bounded dimension expression: ' + key)
                if expression.get('kind') == 'Reference':
                    deps.add(expression['dimension_id'])
                pending.extend((arg, depth + 1) for arg in expression.get('arguments', []))
            fact_graph[key] = sorted(deps)
        ordered(fact_graph)
        self.fact_graph = fact_graph
        self.graph = self.dependency_graph()
        self.order = ordered(self.graph)
        # Operation IDs are independent of step IDs and must also form a DAG.
        op_graph = {}
        for key, op in self.operations.items():
            deps = set(op.get('depends_on', []))
            if op.get('sketch_id'):
                deps.add(op['sketch_id'])
            if deps - self.operations.keys():
                raise ValueError('Unknown operation dependency for ' + key)
            op_graph[key] = deps
        ordered(op_graph)

    @property
    def revision(self):
        return digest(self.bundle)

    def collect(self, key):
        return [value for step in self.steps.values() for value in step.get(key, [])]

    def dependency_graph(self):
        owners = {}
        for step in self.steps.values():
            for collection in ('operations', 'dimensions', 'features'):
                for item in step.get(collection, []):
                    owners.setdefault(item['id'], set()).add(step['id'])
        graph = {}
        for key, step in self.steps.items():
            deps = set(step.get('depends_on', []))
            # Conservatively infer references, including nested native feature references.
            for value in strings({k: v for k, v in step.items() if k not in ('id', 'depends_on')}):
                deps.update(owners.get(value, set()) - {key})
            for fact in step.get('dimensions', []):
                for parent in self.fact_graph[fact['id']]:
                    deps.update(owners.get(parent, set()) - {key})
            graph[key] = deps
        return graph

    def verify_sources(self):
        actual = {}
        for identity in self.bundle['sources']:
            current = file_identity(identity['path'])
            if current != identity:
                raise ValueError('Source identity changed: ' + identity['path'])
            actual[current['path']] = current['sha256']
        context = self.bundle.get('context')
        if context is not None:
            if context.get('require_complete_bindings') is not True or context.get('require_interpretation_review') is not True:
                raise ValueError('Complete binding and interpretation gates must remain enabled')
            source = file_identity(context['source_path'])
            omission = context.get('omission_review') or {}
            inventory_id = file_identity(omission['inventory_path'])
            if actual.get(source['path']) != source['sha256'] or actual.get(inventory_id['path']) != inventory_id['sha256']:
                raise ValueError('Source/inventory not pinned in bundle')
            if omission['inventory_sha256'].lower() != inventory_id['sha256']:
                raise ValueError('Inventory identity mismatch')
            inventory = read_json(inventory_id['path'])
            if file_identity(inventory['source_path']) != source or inventory['source_sha256'].lower() != source['sha256']:
                raise ValueError('Inventory belongs to another source')
            for region in inventory['regions']:
                if file_identity(region['image_path'])['sha256'] != region['image_sha256'].lower():
                    raise ValueError('Raw review image changed: ' + region['id'])
        elif self.dimensions or self.collect('features') or self.collect('interpretation_reviews'):
            raise ValueError('Drawing source facts require a drawing context')

    def resolve(self, value):
        if isinstance(value, dict):
            if '$dimension' in value:
                if set(value) != {'$dimension', 'unit'}:
                    raise ValueError('A dimension reference needs exactly $dimension and unit')
                fact = self.dimensions[value['$dimension']]
                if fact.get('unit', 'Millimeter') != value['unit']:
                    raise ValueError('Dimension reference unit mismatch: ' + fact['id'])
                if fact.get('expression') is not None:
                    if fact.get('status') != 'Derived':
                        raise ValueError('Expression requires a derived source fact: ' + fact['id'])
                    return copy.deepcopy(value)  # Evaluated with current premises by the common MCP decoder.
                number = fact['value']
                if fact.get('status') not in ('Stated', 'Derived') or isinstance(number, bool) or not isinstance(number, (float, int)) or not math.isfinite(number):
                    raise ValueError('Unresolved/non-numeric dimension: ' + fact['id'])
                return number
            return {key: self.resolve(item) for key, item in value.items()}
        if isinstance(value, list):
            return [self.resolve(item) for item in value]
        return value

    def compose(self, native_output_path):
        self.verify_sources()
        pending = [key for key, step in self.steps.items() if step.get('reviewed') is not True or not step.get('review_note', '').strip()]
        if pending:
            raise ValueError('Steps require source/dependency review: ' + ', '.join(pending))
        if not self.operations:
            raise ValueError('No modeling operations')
        if not Path(native_output_path).is_absolute():
            raise ValueError('Native output path must be absolute')
        draft = copy.deepcopy(self.bundle['draft'])
        draft['operations'] = [op for key in self.order for op in self.steps[key].get('operations', [])]
        verification = draft.setdefault('verification', {})
        for kind in CHECK_ARRAYS:
            values = [item for key in self.order for item in self.steps[key].get('verification', {}).get(kind, [])]
            if values:
                verification[kind] = values
        context = self.bundle.get('context')
        if context is not None:
            context = copy.deepcopy(context)
            for kind in CONTEXT_ARRAYS:
                context[kind] = [item for key in self.order for item in self.steps[key].get(kind, [])]
            draft['drawing_context'] = context
        return {'draft': self.resolve(draft), 'nativeOutputPath': str(native_output_path),
                'overwriteAllowed': False}

    def readiness(self):
        """Explain the dependency-closed reviewed prefix; never certify source semantics."""
        self.verify_sources()
        ready, blocked = [], {}
        for key in self.order:
            step = self.steps[key]
            reasons = []
            if step.get('reviewed') is not True or not step.get('review_note', '').strip():
                reasons.append('source_review_required')
            if self.graph[key] - set(ready):
                reasons.append('dependency_not_ready')
            if any(f.get('status') not in ('Stated', 'Derived') for f in step.get('dimensions', [])):
                reasons.append('unresolved_dimension')
            if blocked:
                reasons.append('outside_verified_prefix')
            if reasons:
                blocked[key] = reasons
            else:
                ready.append(key)
        return {'revision': self.revision, 'ready_prefix': ready, 'blocked': blocked,
                'native_ready': False, 'requires_server_compilation': True}

    def compose_stage(self, native_output_path, step_ids, context, recovery):
        """Use explicitly reviewed stage coverage. Never invent Deferred classifications."""
        ready = self.readiness()['ready_prefix']
        if not step_ids or step_ids != self.order[:len(step_ids)] or step_ids != ready[:len(step_ids)]:
            raise ValueError('Stage must be a nonempty reviewed dependency-closed prefix')
        if len(step_ids) == len(self.order):
            raise ValueError('Final scope must use ordinary complete composition')
        original = self.bundle.get('context') or {}
        if context.get('source_path') != original.get('source_path') or not context.get('stage_scope'):
            raise ValueError('Stage needs the same source and explicit stage_scope')
        original_inventory = original.get('omission_review', {})
        stage_inventory = context.get('omission_review', {})
        if any(stage_inventory.get(k) != original_inventory.get(k) for k in ('inventory_path', 'inventory_sha256')):
            raise ValueError('Stage cannot substitute its source inventory')
        selected = [self.steps[k] for k in step_ids]
        for kind in CONTEXT_ARRAYS:
            expected = [x for s in selected for x in s.get(kind, [])]
            if digest(context.get(kind, [])) != digest(expected):
                raise ValueError('Stage context differs from selected reviewed ' + kind)
        pending = {f['id'] for k in self.order if k not in step_ids for f in self.steps[k].get('features', [])}
        if pending != set(context['stage_scope'].get('pending_feature_ids', [])):
            raise ValueError('Stage pending features must match the remaining bundle')
        if not recovery.get('enabled') or not recovery.get('source_revision_id'):
            raise ValueError('Stage requires verified native checkpoint recovery')
        new = copy.deepcopy(self.bundle)
        new['steps'] = copy.deepcopy(selected)
        new['context'] = copy.deepcopy(context)
        for kind in CONTEXT_ARRAYS:
            new['context'].pop(kind, None)
        new['draft']['recovery'] = copy.deepcopy(recovery)
        selected_facts = {f['id'] for s in selected for f in s.get('dimensions', [])}
        new['fact_dependencies'] = {k:v for k,v in new.get('fact_dependencies', {}).items() if k in selected_facts}
        return ModelSession(new).compose(native_output_path)

    def revise(self, expected_revision, replacements, *, context=None, fact_dependencies=None):
        if expected_revision != self.revision:
            raise ValueError('Stale revision; read current bundle before correction')
        if not replacements and context is None and fact_dependencies is None:
            raise ValueError('Empty correction')
        changed = unique(replacements, 'id', 'replacement')
        if set(changed) - self.steps.keys():
            raise ValueError('Unknown replacement step')
        new = copy.deepcopy(self.bundle)
        new['steps'] = [copy.deepcopy(changed.get(step['id'], step)) for step in new['steps']]
        if context is not None:
            new['context'] = copy.deepcopy(context)
        if fact_dependencies is not None:
            new['fact_dependencies'] = copy.deepcopy(fact_dependencies)
        candidate = ModelSession(new)
        affected = set(changed)
        if context is not None or fact_dependencies is not None:
            affected.update(self.steps)
        while True:
            expanded = affected | {key for key in self.steps if
                                   (self.graph[key] | candidate.graph[key]) & affected}
            if expanded == affected:
                break
            affected = expanded
        for step in new['steps']:
            if step['id'] in affected:
                step['reviewed'] = False
                step['review_note'] = ''
        new.setdefault('history', []).append({'parent_revision': self.revision,
            'changed': sorted(changed), 'invalidated': sorted(affected)})
        return ModelSession(new)

    def confirm_review(self, expected_revision, step_ids, note):
        if expected_revision != self.revision or not note.strip() or not step_ids:
            raise ValueError('Review needs current revision, step IDs and source-based notes')
        if set(step_ids) - self.steps.keys():
            raise ValueError('Unknown review step')
        self.verify_sources()
        new = copy.deepcopy(self.bundle)
        for step in new['steps']:
            if step['id'] in step_ids:
                step['reviewed'], step['review_note'] = True, note
        return ModelSession(new)

    def patch(self, expected_revision, changes):
        """Replace existing fields by stable step/item IDs, guarded by old values."""
        if expected_revision != self.revision or not changes:
            raise ValueError('Patch requires current revision and nonempty changes')
        replacements = {}
        for change in changes:
            if set(change) != {'step_id', 'collection', 'id', 'path', 'expected', 'value'}:
                raise ValueError('Unknown/missing patch fields')
            step_id = change['step_id']
            if step_id not in self.steps:
                raise ValueError('Unknown patch step')
            step = replacements.setdefault(step_id, copy.deepcopy(self.steps[step_id]))
            collection = change['collection']
            if collection in ('operations',) + CONTEXT_ARRAYS:
                items = step.get(collection, [])
            elif collection.startswith('verification.') and collection[13:] in CHECK_ARRAYS and collection != 'verification.bindings':
                items = step.get('verification', {}).get(collection[13:], [])
            else:
                raise ValueError('Unsupported stable-ID patch collection')
            key = 'feature_id' if collection == 'interpretation_reviews' else 'id'
            matches = [item for item in items if item.get(key) == change['id']]
            if len(matches) != 1:
                raise ValueError('Patch item ID missing or ambiguous')
            path = change['path']
            if not isinstance(path, str) or not path.startswith('/'):
                raise ValueError('Patch path must be a nonroot JSON Pointer')
            tokens = path[1:].split('/')
            import re
            if any(re.search(r'~(?![01])', t) for t in tokens):
                raise ValueError('Invalid JSON Pointer escape')
            tokens = [t.replace('~1', '/').replace('~0', '~') for t in tokens]
            if tokens[0] == key:
                raise ValueError('Stable IDs cannot be changed by field patch')
            parent = matches[0]
            for index, token in enumerate(tokens):
                if isinstance(parent, list):
                    if not re.fullmatch(r'0|[1-9][0-9]*', token) or int(token) >= len(parent):
                        raise ValueError('Invalid patch array index')
                    token = int(token)
                elif not isinstance(parent, dict) or token not in parent:
                    raise ValueError('Patch path does not exist')
                if index == len(tokens)-1:
                    if digest(parent[token]) != digest(change['expected']):
                        raise ValueError('Patch expected value differs; no changes committed')
                    parent[token] = copy.deepcopy(change['value'])
                else:
                    parent = parent[token]
        return self.revise(expected_revision, list(replacements.values()))


def split_draft(draft, sources, fact_dependencies=None):
    """Bootstrap one reviewed draft as a pending step; never certify it automatically."""
    draft = copy.deepcopy(draft)
    if draft.get('recipe') is None:
        draft.pop('recipe', None)
    context = draft.pop('drawing_context', None)
    step = {'id': 'main', 'depends_on': [], 'reviewed': False, 'review_note': '',
            'operations': draft.pop('operations'), 'verification': {}}
    if context is not None:
        for key in CONTEXT_ARRAYS:
            step[key] = context.pop(key, [])
    for key in CHECK_ARRAYS:
        if key in draft.get('verification', {}):
            step['verification'][key] = draft['verification'].pop(key)
    return ModelSession({'schema': REVISION, 'draft': draft, 'context': context,
                         'sources': sources, 'steps': [step],
                         'fact_dependencies': fact_dependencies or {}, 'history': []})
