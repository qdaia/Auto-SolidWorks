"""Portable source/layout checks. Does not start SolidWorks or certify CAD output."""
import ast
import json
from pathlib import Path
import re
import sys

root=Path(__file__).resolve().parents[1]
plugin=root/'plugins/auto-solidworks'
manifest=json.loads((plugin/'.codex-plugin/plugin.json').read_text(encoding='utf-8'))
assert manifest['name']==plugin.name=='auto-solidworks'
assert (plugin/'.mcp.json').is_file()
assert (plugin/'skills/auto-solidworks/SKILL.md').is_file()
market=json.loads((root/'.agents/plugins/marketplace.json').read_text(encoding='utf-8'))
assert market['plugins'][0]['source']['path']=='./plugins/auto-solidworks'
for folder in ('tests','scripts'):
    for p in (root/folder).glob('*.py'):
        ast.parse(p.read_text(encoding='utf-8'),filename=str(p))
sys.path.insert(0,str(root/'tests'))
from cases import cases
assert 'box_fillet' in cases and 'workflow_opposite_holes' in cases
for p in list(root.glob('*.md'))+list((root/'docs').glob('*.md')):
    for link in re.findall(r'\]\(([^)]+)\)',p.read_text(encoding='utf-8-sig')):
        if '://' in link or link.startswith('#'):continue
        assert (p.parent/link.split('#')[0]).exists(),(p.name,link)
print(json.dumps(dict(success=True,synthetic_native_cases=len(cases),plugin_version=manifest['version'])))
