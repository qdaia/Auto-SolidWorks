"""Record hashes after both runtime projects have been freshly published. No CAD activation."""
import hashlib
import json
from pathlib import Path

root=Path(__file__).resolve().parents[1]
plugin=root/'plugins/auto-solidworks'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
source={p.relative_to(root).as_posix():sha(p) for p in (root/'src').rglob('*')
        if p.is_file() and p.suffix in ('.cs','.csproj','.props','.targets') and not {'bin','obj'}&set(p.parts)}
for name in ('Directory.Build.props','Directory.Build.targets','global.json','NuGet.Config'):
    path=root/name
    if path.is_file():source[name]=sha(path)
manifest=plugin/'skills/auto-solidworks/references/capability-manifest.json'
source[manifest.relative_to(root).as_posix()]=sha(manifest)
runtime={p.relative_to(plugin).as_posix():sha(p) for p in (plugin/'runtime').rglob('*')
         if p.is_file() and p.suffix in ('.dll','.exe','.json') and p.name!='build-identity.json' and not p.name.startswith('SolidWorks.Interop.')}
required=['runtime/mcp/AutoSolidWorks.ModelingMcp.dll','runtime/executor/CadModeling.Executor.SolidWorks.dll']
if not all(p in runtime for p in required):raise SystemExit('Required runtime outputs missing')
(plugin/'runtime/build-identity.json').write_text(json.dumps(dict(source_hashes=source,runtime_hashes=runtime),indent=2)+'\n',encoding='utf-8')
print(json.dumps(dict(source_files=len(source),runtime_files=len(runtime),status='recorded')))
