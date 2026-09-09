"""Package the built Windows plugin. Never delete or overwrite an existing archive."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, default=root/'dist')
args = parser.parse_args()
plugin = root/'plugins/auto-solidworks'
version = json.loads((plugin/'.codex-plugin/plugin.json').read_text(encoding='utf-8'))['version']
for file in ('runtime/mcp/AutoSolidWorks.ModelingMcp.exe','runtime/executor/CadModeling.Executor.SolidWorks.exe'):
    if not (plugin/file).is_file():
        raise SystemExit('Build the plugin before packaging: '+file)
args.output.mkdir(parents=True, exist_ok=True)
archive = args.output/f'auto-solidworks-{version}-windows-x64.zip'
selected = [root/p for p in ('README.md','README.en.md','LICENSE','CHANGELOG.md','THIRD_PARTY_NOTICES.md','install.ps1',
    '.agents/plugins/marketplace.json','scripts/find-solidworks.ps1')]
selected += [p for p in (root/'docs').rglob('*') if p.is_file()]
selected += [p for p in (root/'third-party').rglob('*') if p.is_file()]
selected += [p for p in plugin.rglob('*') if p.is_file() and p.suffix.lower() != '.pdb'
             and not p.name.startswith('SolidWorks.Interop.')]
hashes = {p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(selected)}
with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as z:
    for p in sorted(selected):
        z.write(p,p.relative_to(root).as_posix())
    z.writestr('SHA256SUMS.json',json.dumps(hashes,indent=2)+'\n')
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix('.zip.sha256').write_text(f'{digest}  {archive.name}\n',encoding='utf-8')
print(json.dumps({'archive':str(archive),'bytes':archive.stat().st_size,'files':len(hashes),'sha256':digest}))
