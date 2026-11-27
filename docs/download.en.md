# Download Auto SolidWorks 1127

[简体中文](download.zh-CN.md) | **English** · [README](../README.en.md)

Current version: **1127**, released November 27, 2026.

[Windows x64 package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v1127/auto-solidworks-1127-windows-x64.zip) · [SHA-256](https://github.com/qdaia/Auto-SolidWorks/releases/download/v1127/auto-solidworks-1127-windows-x64.zip.sha256) · [Source ZIP](https://github.com/qdaia/Auto-SolidWorks/archive/refs/tags/v1127.zip) · [Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v1127) · [Release notes](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v1127)

Prepare Windows x64, activated local SolidWorks, .NET 9 Windows Desktop Runtime x64 and Codex with plugin support. Verify the checksum, extract to a retained directory and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Open a new Codex chat and enable Auto SolidWorks. Custom SolidWorks installations use -SolidWorksInteropDir; see [installation](installation.md).

The ZIP includes runtime, skills, documentation, licenses and SHA256SUMS.json. Interop comes from local SolidWorks. AI reads drawings without OCR. PDF rasterization uses a local dependency. The default four stages do not add a separate delivery audit.
