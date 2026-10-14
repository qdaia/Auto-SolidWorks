"""Validate a local package and its extracted MCP runtime without changing Codex configuration or starting CAD."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, zipfile
root=Path(__file__).resolve().parents[1]
archive=Path(sys.argv[1]).resolve()
work=Path(sys.argv[2]).resolve()
work.mkdir(parents=True,exist_ok=False)
unpacked=work/'unpacked'
unpacked.mkdir()
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for name in z.namelist():
        assert (unpacked/name).resolve().is_relative_to(unpacked)
        assert 'solidworks.interop.' not in name.lower()
    z.extractall(unpacked)
hashes=json.loads((unpacked/'SHA256SUMS.json').read_text(encoding='utf-8'))
assert all(hashlib.sha256((unpacked/name).read_bytes()).hexdigest()==expected for name,expected in hashes.items())
# SkipCodex prepares only local interop in the extracted package; it neither registers nor activates a plugin.
result=subprocess.run(['powershell.exe','-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(unpacked/'install.ps1'),'-SkipCodex'],
    cwd=unpacked,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60)
(work/'prepare-runtime.log').write_text(result.stdout+'\n'+result.stderr,encoding='utf-8')
assert result.returncode==0,result.stderr
plugin=unpacked/'plugins/auto-solidworks'
env=dict(os.environ,AUTO_SOLIDWORKS_VERIFY_PLUGIN=str(plugin),PYTHONUTF8='1')
with (work/'mcp.log').open('x',encoding='utf-8') as log:
    result=subprocess.run([sys.executable,str(root/'scripts/verify-local-integration-mcp.py'),str(work/'mcp')],env=env,stdout=log,stderr=subprocess.STDOUT,timeout=120)
assert result.returncode==0,'Extracted public MCP failed'
summary=json.loads((work/'mcp/summary.json').read_text(encoding='utf-8'))
report=dict(success=True,archive_files=len(hashes),archive_hashes_match=True,extracted_runtime_prepared=True,codex_configuration_changed=False,
    current_plugin_replaced=False,native_execution=False,public_mcp_checks=summary['passed'],archive_sha256=hashlib.sha256(archive.read_bytes()).hexdigest())
(work/'result.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report))
