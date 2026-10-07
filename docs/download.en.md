# Download the Auto SolidWorks Windows plugin

[简体中文](download.zh-CN.md) | **English** · [Back to README](../README.en.md)

Current version: **`26.10.07` (October 7, 2026; year/month/day)**. Create, inspect and edit parametric parts and assemblies in local SolidWorks through Codex / MCP, exporting native models, STEP / STL and drawings.

[Windows x64 package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.07/auto-solidworks-26.10.07-windows-x64.zip) · [SHA-256 checksum](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.07/auto-solidworks-26.10.07-windows-x64.zip.sha256) · [GitHub Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v26.10.07) · [Additions](release-26.10.07.en.md)

## Install

1. Prepare Windows x64, an activated local SolidWorks installation, .NET 9 Windows Desktop Runtime x64, and Codex with plugin support.
2. Download `auto-solidworks-26.10.07-windows-x64.zip` and extract it to a directory you will retain.
3. Run this command in the extracted directory:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

4. Open a new Codex chat and enable Auto SolidWorks.

The installer prefers the CLI bundled with the Codex desktop app and prepares Interop from the local SolidWorks installation. For a custom installation path, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -SolidWorksInteropDir 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS'
```

## Checksum and package contents

```powershell
Get-FileHash .\auto-solidworks-26.10.07-windows-x64.zip -Algorithm SHA256
Get-Content .\auto-solidworks-26.10.07-windows-x64.zip.sha256
```

Compare the two digests. The archive also contains a per-file `SHA256SUMS.json`, compiled runtime, 18 MCP tools, usage skills, installation scripts, documentation and license notices.

Gordon requires local FreeCAD. Tesseract is optional for OCR and Poppler for PDF rasterization. Source and tests are available in the repository or the Release `source-tests.zip`; choose the Windows ZIP for direct installation.
