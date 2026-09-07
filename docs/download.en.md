# Download Windows plugin

[简体中文](download.zh-CN.md) | **English** · [Back to README](../README.en.md)

## Auto SolidWorks 0.5.1

Create, inspect and modify parametric parts and assemblies in your local SolidWorks installation through Codex / MCP, using natural-language descriptions or engineering drawings as input and producing SolidWorks part models.
This plugin relies on the LLM's ability to interpret drawings. GPT-6 Astra is recommended.

## Download and install

[Download Windows plugin ZIP](https://github.com/qdaia/Auto-SolidWorks/releases/download/v0.5.1/auto-solidworks-0.5.1-windows-x64.zip) · [SHA-256 checksum](https://github.com/qdaia/Auto-SolidWorks/releases/download/v0.5.1/auto-solidworks-0.5.1-windows-x64.zip.sha256) · [GitHub Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v0.5.1)

For agent-assisted installation, send this message to your agent: Help me install https://github.com/qdaia/Auto-SolidWorks

For manual installation, download `auto-solidworks-0.5.1-windows-x64.zip` using the link above, extract it, and run the following command in the extracted directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Requires Windows x64, an activated local SolidWorks installation, .NET 9 Windows Desktop Runtime x64, and a Codex CLI version with plugin support. Open a new Codex task after installation. The SHA-256 checksum is available through the matching `.zip.sha256` link above.

The installer copies the SolidWorks interop DLLs from the user's local installation. Install Tesseract for OCR and Poppler for PDF rasterization separately as needed. See the [installation guide (Chinese)](installation.md) for detailed steps.

GitHub's automatically generated Source code archives do not contain the runtime. Choose the Windows plugin ZIP for direct installation.

## Included

- 8 MCP tools covering capability queries, drawing input, plan compilation, part building, model inspection, assembly building, profile discovery and connection diagnostics.
- Workflows for native features, sheet metal, weldments, surfaces, geometric references and editing copies of existing models.
- C# source code, public synthetic tests, Chinese documentation, installation and build scripts, the MIT license and third-party notices.
