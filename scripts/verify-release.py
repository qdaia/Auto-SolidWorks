"""Validate an archive and install it using an isolated Codex configuration directory."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import zipfile

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('archive',type=Path)
parser.add_argument('--workdir',type=Path,required=True)
args=parser.parse_args()
work=args.workdir.resolve()
work.mkdir(parents=True,exist_ok=False)
unpacked=work/'unpacked'
unpacked.mkdir()
with zipfile.ZipFile(args.archive) as archive:
    assert archive.testzip() is None
    for name in archive.namelist():
        target=(unpacked/name).resolve()
        assert target.is_relative_to(unpacked),('invalid ZIP path',name)
        assert not name.lower().endswith('.pdb')
        assert 'solidworks.interop.' not in name.lower()
    archive.extractall(unpacked)
hashes=json.loads((unpacked/'SHA256SUMS.json').read_text(encoding='utf-8'))
for relative,digest in hashes.items():
    assert hashlib.sha256((unpacked/relative).read_bytes()).hexdigest()==digest,relative
env=os.environ.copy()
env['CODEX_HOME']=str(work/'codex-home')
env['PYTHONUTF8']='1'
Path(env['CODEX_HOME']).mkdir()
result=subprocess.run(['powershell.exe','-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',
                       '-File',str(unpacked/'install.ps1')],cwd=unpacked,env=env,
                      capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=120)
(work/'install.log').write_text(result.stdout+'\n'+result.stderr,encoding='utf-8')
if result.returncode:
    raise SystemExit('Isolated install failed; see '+str(work/'install.log'))
version=json.loads((unpacked/'plugins/auto-solidworks/.codex-plugin/plugin.json').read_text(encoding='utf-8'))['version']
cache=Path(env['CODEX_HOME'])/'plugins/cache/auto-solidworks-local/auto-solidworks'/version
assert (cache/'.codex-plugin/plugin.json').is_file(),str(cache)
expected={p.relative_to(unpacked/'plugins/auto-solidworks').as_posix():hashlib.sha256(p.read_bytes()).hexdigest()
          for p in (unpacked/'plugins/auto-solidworks').rglob('*') if p.is_file()}
actual={p.relative_to(cache).as_posix():hashlib.sha256(p.read_bytes()).hexdigest()
        for p in cache.rglob('*') if p.is_file()}
assert expected==actual,'Installed-cache hashes differ'
subprocess.run(['python',str(root/'tests/smoke.py'),str(cache),'--report',str(work/'smoke.json')],
               env=env,check=True,timeout=120)
report=dict(success=True,archive_files=len(hashes),installed_files=len(actual),installed_hashes_match=True,
            isolated_codex_install=True,installed_smoke_passed=True,
            archive_sha256=hashlib.sha256(args.archive.read_bytes()).hexdigest())
(work/'result.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report))
