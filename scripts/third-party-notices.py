"""Collect package metadata and upstream license/notice texts for the built MCP runtime."""
import base64
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[1]
cache=Path.home()/'.nuget/packages'
deps=json.loads((root/'plugins/auto-solidworks/runtime/mcp/AutoSolidWorks.ModelingMcp.deps.json').read_text(encoding='utf-8'))
target=root/'third-party'
target.mkdir(exist_ok=True)
entries=[]
for identity, spec in sorted(deps['libraries'].items()):
    if spec['type']!='package':continue
    package,version=identity.split('/')
    xml=ET.parse(cache/identity.lower()/(package.lower()+'.nuspec'))
    metadata={e.tag.split('}')[-1]:(e.text or '').strip() for e in xml.iter()}
    entries.append(dict(package=package,version=version,license=metadata.get('license',''),
                        copyright=metadata.get('copyright',''),project=metadata.get('projectUrl','')))

sources=[
    ('dotnet/runtime','LICENSE.TXT','dotnet-LICENSE.txt'),
    ('dotnet/runtime','THIRD-PARTY-NOTICES.TXT','dotnet-NOTICES.txt'),
    ('modelcontextprotocol/csharp-sdk','LICENSE','mcp-LICENSE.txt'),
    ('modelcontextprotocol/csharp-sdk','THIRD-PARTY-NOTICES.txt','mcp-NOTICES.txt'),
    ('UglyToad/PdfPig','LICENSE','pdfpig-LICENSE.txt'),
    ('UglyToad/PdfPig','NOTICES.txt','pdfpig-NOTICES.txt'),
]
documents=[]
for repo,path,filename in sources:
    # Pin the fetched text's Git blob SHA in the provenance manifest.
    result=subprocess.run(['gh','api',f'repos/{repo}/contents/{path}'],capture_output=True,text=True,encoding='utf-8',check=True)
    data=json.loads(result.stdout)
    content=base64.b64decode(data['content'])
    (target/filename).write_bytes(content)
    documents.append(dict(file=filename,source=data['html_url'],git_blob=data['sha'],sha256=hashlib.sha256(content).hexdigest()))
(target/'dependencies.json').write_text(json.dumps(dict(packages=entries,license_documents=documents),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
lines=['# Third-party notices','',
       'Auto SolidWorks source is MIT licensed. Dependencies retain their own licenses. This list is generated from the built MCP dependency manifest and local NuGet metadata. Upstream license and notice texts are preserved under `third-party/`.',
       '', '| Package | Version | License | Copyright |','|---|---|---|---|']
lines += [f"| {e['package']} | {e['version']} | {e['license']} | {e['copyright']} |" for e in entries]
lines += ['', '## License texts','']+[f"- [{d['file']}](third-party/{d['file']})" for d in documents]
lines += ['', '## External local dependencies','',
          'SolidWorks and its interop assemblies are external proprietary dependencies. The public ZIP does not redistribute them. The installer copies the required interop assemblies from the user\'s own local SolidWorks installation.',
          '', '.NET Windows Desktop Runtime, Tesseract, language data and Poppler are installed separately and are not bundled.',
          '', 'Dependency versions and license-document provenance are recorded in [dependencies.json](third-party/dependencies.json).']
(root/'THIRD_PARTY_NOTICES.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
print(json.dumps(dict(packages=len(entries),license_documents=len(documents))))
