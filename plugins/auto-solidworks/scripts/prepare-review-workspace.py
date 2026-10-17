"""Create provenance-bound pending review inputs; performs no interpretation or CAD work."""
import argparse
import hashlib
import json
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(inventory_path, expected_sha, output):
    inventory_path = Path(inventory_path).resolve(strict=True)
    if sha(inventory_path).lower() != expected_sha.lower():
        raise ValueError('Inventory SHA-256 differs from the ingestion receipt')
    inventory = json.loads(inventory_path.read_text(encoding='utf-8-sig'))
    source = Path(inventory['source_path']).resolve(strict=True)
    if sha(source).lower() != inventory['source_sha256'].lower():
        raise ValueError('Source changed after ingestion')
    if not inventory['complete_extraction']:
        raise ValueError('Incomplete extraction cannot initialize review')
    candidates, regions = inventory['candidates'], inventory['regions']
    ids = [c['id'] for c in candidates]
    region_ids = [r['id'] for r in regions]
    if len(set(ids)) != len(ids) or len(set(region_ids)) != len(region_ids):
        raise ValueError('Duplicate inventory identifiers')
    region_set = set(region_ids)
    for candidate in candidates:
        if not set(candidate['review_region_ids']).issubset(region_set):
            raise ValueError('Candidate refers to a missing raw region')
    for region in regions:
        image = Path(region['image_path']).resolve(strict=True)
        if sha(image).lower() != region['image_sha256'].lower():
            raise ValueError('Raw region image changed: '+region['id'])

    # Every candidate appears exactly once in the worklist; all raw regions remain listed.
    groups = {r['id']: [] for r in regions}
    groups['unlocated'] = []
    for candidate in candidates:
        available = candidate['review_region_ids']
        group = next((r for r in available if 'detail' in r), available[0] if available else 'unlocated')
        groups[group].append(candidate)
    context = {
        'source_path': str(source), 'projection': 'FirstAngle', 'projection_status': 'Assumed',
        'require_complete_bindings': True, 'require_interpretation_review': True, 'require_dimension_evidence': True,
        'views': [], 'features': [], 'dimensions': [], 'interpretation_reviews': [],
        'omission_review': {
            'inventory_path': str(inventory_path), 'inventory_sha256': expected_sha.lower(),
            'candidates': [],
            'regions': [{'region_id': r['id'], 'state': 'Unresolved', 'feature_ids': [], 'findings': ''} for r in regions],
            'cross_view_checks': [], 'additional_findings': []}}
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    def write(name, value):
        path = output/name
        with path.open('x', encoding='utf-8') as handle:
            json.dump(value, handle, ensure_ascii=False, indent=2)
        return {'path': str(path), 'sha256': sha(path)}
    files = {'drawing_context': write('drawing-context.pending.json', context)}
    index = []
    for number, (group_id, values) in enumerate(groups.items()):
        if group_id == 'unlocated' and not values:
            continue
        artifact = write(f'worklist-{number:04d}.json', {'region_id': group_id, 'candidates': values})
        index.append({'region_id': group_id, 'candidate_count': len(values), **artifact})
    manifest = {
        'status': 'pending_independent_visual_review', 'semantic_review_complete': False,
        'source_path': str(source), 'source_sha256': inventory['source_sha256'],
        'inventory_path': str(inventory_path), 'inventory_sha256': expected_sha.lower(),
        'candidate_count': len(candidates), 'region_count': len(regions),
        'raw_regions': regions, 'files': files, 'worklists': index,
        'instructions': 'Inspect original and every raw region independently. Add source-derived views, feature/dimension bindings, alternative-hypothesis counterevidence and explicit candidate_ids decisions. No candidate has been accepted, discarded or marked noise. Keep unknown topology counts unresolved; never copy them from the produced model.'}
    write('index.json', manifest)
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('inventory_path')
    parser.add_argument('output_directory')
    parser.add_argument('--inventory-sha256', required=True)
    args = parser.parse_args()
    result = prepare(args.inventory_path, args.inventory_sha256, args.output_directory)
    print(json.dumps({k: result[k] for k in ('status', 'candidate_count', 'region_count')}, ensure_ascii=False))
