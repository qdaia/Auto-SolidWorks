# Auto SolidWorks

[简体中文](README.md) | **English**

Create and modify parametric parts and assemblies in your local SolidWorks installation from natural-language descriptions or engineering drawings, and save native models and STEP/STL exports.

**Windows · Local SolidWorks · mm by default · 8 MCP tools**

[Download Windows plugin](docs/download.en.md) · [Installation (Chinese)](docs/installation.md) · [Build from source (Chinese)](docs/development.md) · [Operation reference](plugins/auto-solidworks/skills/auto-solidworks/references/modeling-operations.md)

## Example: Hollow shaft

**PPL001M45.1-2 hollow shaft**: a complex shaft modeling example based on a multiview engineering drawing, showing stepped shaft sections, elongated side openings, keyways, annular grooves and end-face hole groups.

<table>
<tr>
<td align="center" width="33%"><a href="docs/examples/hollow-shaft/model-preview.png"><img src="docs/examples/hollow-shaft/model-preview.png" width="240" alt="Hollow shaft, isometric view"></a><br><sub>Isometric</sub></td>
<td align="center" width="33%"><a href="docs/examples/hollow-shaft/model-side.png"><img src="docs/examples/hollow-shaft/model-side.png" width="240" alt="Hollow shaft, side view"></a><br><sub>Side</sub></td>
<td align="center" width="33%"><a href="docs/examples/hollow-shaft/model-end.png"><img src="docs/examples/hollow-shaft/model-end.png" width="240" alt="Hollow shaft, end view"></a><br><sub>End</sub></td>
</tr>
</table>

Click a thumbnail to view the original full-size image.

[Example details (Chinese)](docs/examples/hollow-shaft/README.md) · [Original PDF drawing](docs/examples/hollow-shaft/drawing.pdf)

<details>
<summary>Expand to view the input drawing</summary>

![Hollow shaft input drawing with the main view, sections, end views and detail views](docs/examples/hollow-shaft/drawing-preview.png)

Open the PDF linked above for the complete dimensions and technical requirements.

</details>

This example presents the supplied drawing and model images. The screenshots do not establish that all dimensions, tolerances or equivalence to the drawing have been verified.

## Quick start

1. Install and activate SolidWorks locally, then install the **.NET 9 Windows Desktop Runtime (x64)** and a Codex CLI version with plugin support.
2. Download `auto-solidworks-0.5.1-windows-x64.zip` from the [download page](docs/download.en.md) and extract it to a directory you will keep for ongoing use.
3. Open PowerShell in the extracted directory and run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

4. Select **Auto SolidWorks** in a new Codex task, then describe the dimensions or provide a drawing:

   > Create an 80 × 50 × 10 mm rectangular plate with a centered through-hole 10 mm in diameter, and save SLDPRT and STEP files.

The installer reads the interop components from your local SolidWorks installation and then registers a local plugin marketplace. The plugin package does not distribute SolidWorks software or interop DLLs. If the installation directory cannot be detected automatically, use `-SolidWorksInteropDir`; see the [installation guide (Chinese)](docs/installation.md).

GitHub's automatically generated **Source code (zip)** contains source files without the compiled runtime. Source users must build the project first. Adding this repository directly as a remote plugin marketplace does not automatically download the runtime either; use the Release ZIP for installation.

## Capabilities

| Area | Features |
|---|---|
| Sketches | Rectangles, circles, arcs, polygons, slots, ellipses, splines, constraints and driving dimensions |
| Parts | Extrusions, cuts, revolves, holes, fillets, chamfers, shells, drafts, ribs, lofts and sweeps |
| Repetition and multibody | Patterns, mirrors, Boolean operations, splits, moves and copies |
| Sheet metal, weldments and surfaces | Base features and flanges, flattening, structural members and trimming, surfaces and thickening |
| Assemblies | Component insertion, configurations and placement, geometric mates and interference checks |
| Inspection and editing | Features, dimensions, configurations, body geometry, editing copies of native parts and STEP import inspection |
| Drawing input | Local images and PDFs, OCR candidates, region/rotation hints and dimension-binding checks |
| File output | SLDPRT, SLDASM, STEP/STP and STL |

Tool workflow: `cad_get_capabilities → cad_read_drawing (if a drawing is provided) → cad_create_model_plan → cad_build_model → cad_inspect_model`.

Assembly building, profile discovery and connection diagnostics use `cad_build_assembly`, `cad_list_weldment_profiles` and `cad_executor_health`, respectively. Plans pass typed drafts / IR directly; `dryRun` is optional, and modeling executes by default. CAD is not controlled through an arbitrary shell or script execution interface.

## Runtime and limitations

- CAD execution, drawing preprocessing and file storage run locally. How Codex or another AI client handles prompts, drawings and requests to model providers depends on that client and its settings; this project does not describe cloud AI sessions as fully offline.
- Dimensions default to mm when no unit is specified. Missing or conflicting critical dimensions require clarification; OCR numbers must be checked against the original drawing.
- `cad_executor_health` and actual modeling may start SolidWorks. New files are created by default, without overwriting existing models.
- The current version does not generate physical helical threads or native drawing sheets, and does not fully interpret arbitrary GD&T. Tapped holes use tap-drill holes and native cosmetic threads.
- Support for a feature family does not imply support for every SolidWorks option within it. Successful geometry creation, rebuilding and measurement do not automatically establish equivalence to an arbitrary engineering drawing.
- Local development validation used SolidWorks 2025; other versions require separate verification. See [release validation (Chinese)](docs/validation.md) for the checks performed for this public release.

## Development and feedback

Source code is in `src/`, and plugin files are in `plugins/auto-solidworks/`. See the [development guide (Chinese)](docs/development.md) for builds, non-CAD smoke checks and native tests. When reporting an issue, include the plugin version, SolidWorks version, a minimal dimensioned description and the error message.

## License

The project source code is licensed under the [MIT License](LICENSE). Third-party components retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). SolidWorks is an external proprietary dependency and requires the user's own installation and license. This is an independent project and does not represent Dassault Systèmes or the official SOLIDWORKS organization.
