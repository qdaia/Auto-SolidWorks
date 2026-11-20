# Download Auto SolidWorks 1120

[简体中文](download.zh-CN.md) | **English** · [README](../README.en.md)

Current version: **1120**, released November 20, 2026.

[Windows x64 package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v1120/auto-solidworks-1120-windows-x64.zip) · [SHA-256](https://github.com/qdaia/Auto-SolidWorks/releases/download/v1120/auto-solidworks-1120-windows-x64.zip.sha256) · [Source ZIP](https://github.com/qdaia/Auto-SolidWorks/archive/refs/tags/v1120.zip) · [Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v1120) · [Release notes](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v1120)

Prepare Windows x64, activated local SolidWorks, .NET 9 Windows Desktop Runtime x64 and Codex with plugin support. Verify the checksum, extract to a retained directory and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Open a new Codex chat and enable Auto SolidWorks. Custom SolidWorks installations use -SolidWorksInteropDir; see [installation](installation.md).

The ZIP includes runtime, skills, documentation, licenses and SHA256SUMS.json. Interop comes from local SolidWorks. AI reads drawings without OCR. PDF rasterization and optional surface tools use their respective local dependencies. The default four stages do not add a separate delivery audit.
