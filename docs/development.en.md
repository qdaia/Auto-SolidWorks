# Development and validation

[简体中文](development.md)

## Build 26.10.07 from source

Requires Windows, .NET 9 SDK, Python 3.10+ and Interop components from a licensed local SolidWorks installation.

```powershell
git clone https://github.com/qdaia/Auto-SolidWorks.git
Set-Location Auto-SolidWorks
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

Pass `-SolidWorksInteropDir` or set `SOLIDWORKS_INTEROP_DIR` for a custom installation. Output is written to `plugins/auto-solidworks/runtime/`, with a SHA-256 build identity binding source, capability metadata and runtime files.

## Offline checks

After building, select a new output directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-local-integration-offline.ps1 -OutputDirectory .\artifacts\offline-26.10.07
```

This runs 12 regression suites, document-contract checks and public MCP compilation/dry-run sequentially. Fake native-adapter checks require local Interop but do not start SolidWorks. Integration validation passed 1,101 offline checks; the installed public interface separately passed 128 checks.

For MCP checks alone:

```powershell
python scripts/verify-local-integration-mcp.py artifacts/mcp-26.10.07
```

Portable synthetic inputs are in `tests/fixtures/local-integration/`. The controlled-history test creates a clearly labeled path placeholder and prohibits executor startup. That file tests input-path/plan preflight only and is not native CAD evidence.

## GitHub checks

The workflow builds MCP, checks public structure and the version contract, and sequentially runs nine managed regression suites without SolidWorks Interop. Native fixture evidence remains tied to its original build identity; cloud checks do not certify CAD output.

## Package and isolated installation

```powershell
python scripts/package-release.py --output dist/26.10.07
python scripts/verify-release.py dist/26.10.07/auto-solidworks-26.10.07-windows-x64.zip --workdir artifacts/isolated-install-26.10.07
```

The packager refuses to overwrite an archive and includes runtime, skills, documentation, installer and per-file hashes. Proprietary SolidWorks Interop and PDB files are excluded. Installation verification uses a separate Codex configuration directory, checks file hashes and exercises the installed MCP interface.

Source, current regression projects and synthetic inputs are public. Private drawings, CAD results, logs, native stores, keys and backups stay local. Earlier public tests are retained for historical versions; use the entry points above for current validation.
