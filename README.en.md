# Auto SolidWorks

**Turn engineering drawings and dimension descriptions into editable SolidWorks parametric models.**

Auto SolidWorks is a local CAD plugin for Codex / MCP. It creates, inspects and edits parts and assemblies in your SolidWorks installation, exporting native models, STEP / STL and drawings. Its local Gordon tool builds NURBS surfaces from curve networks.

[简体中文](README.md) | **English**

**Version `26.10.17` · October 17, 2026 · Windows x64 · Codex / MCP**

[Download Windows plugin](docs/download.en.md) · [Release notes](docs/release-26.10.17.en.md) · [GitHub Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v26.10.17) · [Build from source](docs/development.en.md)

## 26.10.17: Review, correct and iterate before delivery

Review the model after creation. Correct evidence-backed discrepancies and model again instead of treating the first result as final. Codex works with the plugin, preserving source bindings, failed receipts, checkpoints and final save/reopen gates; remaining parametric or source uncertainties are disclosed.

![Actual modeling iteration: rejected candidates and delivered geometry](docs/images/modeling-iteration-26.10.17.en.png)

Also includes direct AI drawing review and endpoint binding, source expressions, serial background execution, staged continuation and fixed-frame projection comparison. See the [English release notes](docs/release-26.10.17.en.md) and [release validation scope](docs/release-validation-26.10.17.md).

## Synchronized October 14, 2026 (version `26.10.13-opt.1`)

- Adds driving hole positions, sketch quality readback, actual geometry edit probes, and bounded flange, plate and hole-family recipes.
- Validates native capability evidence against task scope, runtime hashes and the SolidWorks revision. Source bindings, body validity and final save/reopen gates remain required.
- Retains essential final review: one bounded success summary and actual review of an isometric/necessary-view contact sheet.

See the [release notes](docs/release-26.10.13-opt.1.en.md) for validation scope and limits.

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

Auto SolidWorks **26.10.11** focuses on repeated input and local edits in drawing-based modeling workflows. The new version stores complete drawing contexts and modeling drafts locally, **substantially reducing token consumption and improving modeling speed**.

For **token input burden**, the text size of the default tool catalog is approximately **97.16%** smaller than in the original version. In a test workflow that includes the initial complete upload, multiple reviews and corrected compilation, request text is approximately **78.75%** smaller. For one local edit and recompilation of the hollow shaft, request text is approximately **99.928%** smaller. Overall token consumption is reduced by approximately **25%**.

For **modeling time**, the from-scratch arched-support comparison includes drawing interpretation, plan preparation, native modeling, retries and export checks. Total time decreased from **9 minutes 19 seconds to 3 minutes 15 seconds**, a reduction of approximately **65.1%** in this observation. Both versions passed the declared dimension, geometry and save/reopen checks. This result comes from a single test in the same conversation and includes the effects of warm-up and debugging experience.

See the [English release notes](docs/release-26.10.11.en.md) and [validation report](docs/benchmark-26.10.11.en.md) for the complete data and scope.

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
2. Download and extract the [26.10.17 Windows package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.17/auto-solidworks-26.10.17-windows-x64.zip). Run this command in the extracted directory:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

3. Open a new Codex chat, enable **Auto SolidWorks**, and provide dimensions or a drawing:

   > Create an 80 × 50 × 10 mm mounting plate with a centered 10 mm through hole and R2 corner fillets. Save SLDPRT and STEP.

The installer prefers the CLI bundled with the Codex desktop app and prepares Interop from your local SolidWorks installation. See the [English download and installation guide](docs/download.en.md) for detailed steps and custom paths.

## Local execution and capability queries

CAD execution, native files and geometry checks run locally. Users install and license SolidWorks; Gordon also requires local FreeCAD. Image / PDF interpretation uses your selected model and service. OCR and PDF rasterization dependencies can be configured as needed.

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Local version: `26.10.17`; revision: `review-iterate-deliver-1`. Unit-checked source expressions, geometry anchors, explicit stage scope, serial background execution, verified checkpoint resume and fixed-frame source projection differences. Controlled plate validation does not certify general first-attempt accuracy or speedup. Complete source coverage and final native reopen gates remain. See [capability manifest](plugins/auto-solidworks/skills/auto-solidworks/references/capability-manifest.json).
<!-- Capability manifest SHA256: ff92dc2459f941264add1388925e1b3f1de9bc01dd189a69d2939e761b91d3fb -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

[Feature parameters](plugins/auto-solidworks/skills/auto-solidworks/references/modeling-operations.md) · [Gordon guide](plugins/auto-solidworks/skills/auto-solidworks/references/gordon-surface.md) · [Surface controls](plugins/auto-solidworks/skills/auto-solidworks/references/surface-modeling.md) · [Geometry verification](plugins/auto-solidworks/skills/auto-solidworks/references/local-geometry-verification.md)

## Development and feedback

[Build and test](docs/development.en.md) · [Architecture](docs/architecture.en.md) · [Third-party notices](THIRD_PARTY_NOTICES.md) · [Report an issue](https://github.com/qdaia/Auto-SolidWorks/issues)

This project uses the MIT license; see [LICENSE](LICENSE). SolidWorks, FreeCAD and other third-party components retain their own licenses.
