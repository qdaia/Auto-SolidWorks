# Auto SolidWorks

**Turn engineering drawings and dimension descriptions into editable SolidWorks parametric models.**

Auto SolidWorks is a local CAD plugin for Codex / MCP. It creates, inspects and edits parts and assemblies in your SolidWorks installation, exporting native models, STEP / STL and drawings. Its local Gordon tool builds NURBS surfaces from curve networks.

[简体中文](README.md) | **English**

**Version `26.10.11` (October 11, 2026) · Windows x64 · 16 default MCP tools (21 in the full profile) **

[Download Windows plugin](docs/download.en.md) · [What is new](docs/release-26.10.11.en.md) · [GitHub Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v26.10.11) · [Build from source](docs/development.en.md)

## From input to deliverable

Provide an engineering image, PDF or explicit dimensions. The plugin turns the input into an inspectable modeling plan and executes it through local SolidWorks. Continue by editing driving dimensions, checking geometry and assembly relationships, and saving editable native files.

| Workflow | Capabilities and outputs |
| --- | --- |
| Parametric parts | Sketches and constraints, extrudes, revolves, holes, fillets/chamfers, sweeps, lofts, patterns, multi-body parts, sheet metal and weldments; SLDPRT / STEP / STL |
| Surfaces and spatial curves | Typed Boundary / Fill / Sweep / Offset controls, spatial curves and helices; local Gordon networks and NURBS files |
| Design parameters | Global variables, dimension equations, configuration expressions, unit checks, feature suppression and saved readback |
| Existing models | Copy-based edits, persistent references, local/whole-model geometry checks, bounded topology history and repair receipts |
| Assemblies | Components, mates, interference checks and bounded spatial-tree mobility checks; SLDASM |
| Drawings | First-angle views, native dimension import, view layout and pagination; SLDDRW / PDF |
| Execution management | Status, pause, deadlines, cross-process leases, checkpoints and recovery identity checks |

## Updated on October 11, 2026 (version `26.10.11`)

- Includes earlier token input reductions from compact catalogs, plan handles and summary/paginated results.
- Reuses complete local drawing contexts/drafts and applies atomic incremental edits.
- Measured request-text reductions: 78.75% for a workflow including initial upload, and 99.928% for one hollow-shaft edit. Billed tokens were not recorded.
- Retains compilation, source identity and geometry checks; 16 default tools, 21 full-profile tools.

See the [English release notes](docs/release-26.10.11.en.md) and [validation report](docs/benchmark-26.10.11.en.md).

## Examples

### Arched support: Modeling process

The three-view drawing and dimensions are used to create the arched base, upright wall, cylindrical boss, through hole, semicircular notch, and R3 fillets.

<p>
<a href="docs/examples/arch-support/modeling-process.gif"><img src="docs/examples/arch-support/modeling-process.gif" width="800" alt="Arched support native SolidWorks modeling process"></a>
</p>

### Hollow shaft

The image below is a historical example. Both new from-scratch comparison attempts stopped at a curved M20 thread; this image is not release-wide acceptance evidence.

<picture><img src="docs/examples/hollow-shaft/model-preview.png" width="480" alt="Hollow shaft, isometric view"></picture>

<picture><source srcset="docs/examples/hollow-shaft/drawing-preview.svg" type="image/svg+xml"><img src="docs/examples/hollow-shaft/drawing-preview.png" width="1200" alt="Hollow shaft engineering drawing, vector PDF preview"></picture>

Engineering drawing PDF: `docs/examples/hollow-shaft/drawing.pdf`

## Quick start

1. Prepare Windows x64, an activated local SolidWorks installation, [.NET 9 Windows Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/9.0), and Codex with plugin support.
2. Download and extract the [26.10.11 Windows package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.11/auto-solidworks-26.10.11-windows-x64.zip). Run this command in the extracted directory:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

3. Open a new Codex chat, enable **Auto SolidWorks**, and provide dimensions or a drawing:

   > Create an 80 × 50 × 10 mm mounting plate with a centered 10 mm through hole and R2 corner fillets. Save SLDPRT and STEP.

The installer prefers the CLI bundled with the Codex desktop app and prepares Interop from your local SolidWorks installation. See the [English download and installation guide](docs/download.en.md) for detailed steps and custom paths.

## Local execution and capability queries

CAD execution, native files and geometry checks run locally. Users install and license SolidWorks; Gordon also requires local FreeCAD. Image / PDF interpretation uses your selected model and service. OCR and PDF rasterization dependencies can be configured as needed.

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Version: `26.10.11`. Query `cad_get_capabilities` for available features, input constraints and validation scope, or read the [bundled capability manifest](plugins/auto-solidworks/skills/auto-solidworks/references/capability-manifest.json). This release includes compact catalogs, input/plan handles, atomic incremental edits and summary/paginated results. See the release validation report for measured scope; native fixture evidence remains tied to its original revision.
<!-- Capability manifest SHA256: a154cbc0f3cbaf4bb5eb952ca94ef5a03fb20bd9c5654d42463f72fde2db9ea7 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

[Feature parameters](plugins/auto-solidworks/skills/auto-solidworks/references/modeling-operations.md) · [Gordon guide](plugins/auto-solidworks/skills/auto-solidworks/references/gordon-surface.md) · [Surface controls](plugins/auto-solidworks/skills/auto-solidworks/references/surface-modeling.md) · [Geometry verification](plugins/auto-solidworks/skills/auto-solidworks/references/local-geometry-verification.md)

## Development and feedback

[Build and test](docs/development.en.md) · [Architecture](docs/architecture.en.md) · [Third-party notices](THIRD_PARTY_NOTICES.md) · [Report an issue](https://github.com/qdaia/Auto-SolidWorks/issues)

This project uses the MIT license; see [LICENSE](LICENSE). SolidWorks, FreeCAD and other third-party components retain their own licenses.
