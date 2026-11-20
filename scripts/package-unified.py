"""Combine the verified single-target Windows ZIPs into a self-contained installer."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
from tempfile import NamedTemporaryFile
from zipfile import ZIP_DEFLATED, ZipFile

parser = argparse.ArgumentParser()
parser.add_argument("components", type=Path, help="One package-release.py ZIP under each build label")
parser.add_argument("output", type=Path, help="New output ZIP path")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
labels = ["2018", "2018-SP2", "2018-SP5", *map(str, range(2019, 2027))]
plugin = "plugins/auto-solidworks/"
runtime = plugin + "runtime/"
prefix = runtime + "executor/"
identity = runtime + "build-identity.json"
# These two managed libraries are rebuilt in each target-specific artifacts
# directory. Ship the complete MCP output of the first build as one unit.
rebuilt_libraries = {runtime + "mcp/" + name for name in
                     ("CadModeling.Core.dll", "CadModeling.Drawing.Ingestion.dll")}
catalog = []
hashes = {}
shared = None
shared_source = None
version = json.loads((root / plugin / ".codex-plugin/plugin.json").read_text(encoding="utf8"))["version"]

def add(archive, name, data):
    archive.writestr(name, data)
    hashes[name] = hashlib.sha256(data).hexdigest()

args.output.parent.mkdir(parents=True, exist_ok=True)
# Assemble off the public output path. A failed validation cannot publish a partial ZIP.
if args.output.exists():
    raise FileExistsError(args.output)
with NamedTemporaryFile(dir=args.output.parent, suffix=".partial", delete=False) as pending:
    pending_path = Path(pending.name)
with ZipFile(pending_path, "w", compression=ZIP_DEFLATED) as bundle:
    for label in labels:
        path = args.components / label / f"auto-solidworks-{version}-windows-x64.zip"
        with ZipFile(path) as component:
            manifest = json.loads(component.read("SHA256SUMS.json"))
            if set(component.namelist()) != set(manifest) | {"SHA256SUMS.json"} or len(component.namelist()) != len(manifest) + 1:
                raise ValueError(f"Invalid file list: {label}")
            common = {k:v for k,v in manifest.items() if not k.startswith(prefix) and k != identity}
            build_identity = json.loads(component.read(identity))
            source = build_identity["source_hashes"]
            if shared is not None:
                if (common.keys() != shared.keys() or source != shared_source or
                        any(common[k] != shared[k] for k in common.keys() - rebuilt_libraries)):
                    raise ValueError(f"Common payload or source changed between builds: {label}")
            for relative, digest in build_identity["runtime_hashes"].items():
                if manifest.get(plugin + relative) != digest:
                    raise ValueError(f"Build identity mismatch: {label} {relative}")
                if shared is not None and relative.startswith("runtime/mcp/"):
                    build_identity["runtime_hashes"][relative] = shared[plugin + relative]
            for name, expected in manifest.items():
                p = PurePosixPath(name)
                if p.is_absolute() or ".." in p.parts or "\\" in name or ":" in name or p.name.lower().startswith("solidworks.interop."):
                    raise ValueError(f"Unexpected archive member: {name}")
                data = component.read(name)
                if hashlib.sha256(data).hexdigest() != expected:
                    raise ValueError(f"Hash mismatch: {label} {name}")
                if name.startswith(prefix):
                    add(bundle, f"executors/{label}/executor/{name[len(prefix):]}", data)
                elif name == identity:
                    add(bundle, f"executors/{label}/build-identity.json",
                        (json.dumps(build_identity, indent=2) + "\n").encode())
                elif shared is None:
                    # Installation instructions reflect the final unified layout.
                    if name == "docs/installation.md":
                        data = (root / name).read_bytes()
                    add(bundle, "common/" + name, data)
            if shared is None:
                shared, shared_source = common, source
            deps = json.loads(component.read(prefix + "CadModeling.Executor.SolidWorks.deps.json"))
            target = deps["targets"][deps["runtimeTarget"]["name"]]
            dependencies, = [v["dependencies"] for k,v in target.items() if k.startswith("CadModeling.Executor.SolidWorks/")]
            entry = {key: dependencies["SolidWorks.Interop." + key] for key in ("sldworks", "swconst")}
            if any(int(v.split(".")[0]) + 1992 != int(label[:4]) for v in entry.values()):
                raise ValueError(f"Wrong release year: {label}")
            if any(other["sldworks"] == entry["sldworks"] for other in catalog):
                raise ValueError(f"Duplicate Interop version: {label}")
            catalog.append(dict(id=label, **entry))
    add(bundle, "install.ps1", (root / "scripts/install-unified.ps1").read_bytes())
    add(bundle, "README.md", (root / f"docs/release-{version}.zh-CN.md").read_bytes())
    add(bundle, "executors.json", json.dumps(dict(version=version, builds=catalog), indent=2).encode())
    bundle.writestr("SHA256SUMS.json", json.dumps(hashes, indent=2, sort_keys=True))
pending_path.rename(args.output)
digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
args.output.with_suffix(".zip.sha256").write_text(f"{digest}  {args.output.name}\n", encoding="ascii")
print(f"{len(catalog)} executor builds, {len(hashes)} verified files, SHA-256 {digest}")
