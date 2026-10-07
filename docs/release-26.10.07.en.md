# Auto SolidWorks `26.10.07` release notes


[简体中文](release-26.10.07.zh-CN.md) | **English**

## Additions

- **Simplified Chinese defaults**: generated feature-tree names, sketches, assembly components, mates, drawing sheets and views, schedules, diagnostics and tool descriptions now have Chinese defaults.
- **Local Gordon surface tool**: adds curve-network modeling using pinned CurvesWB / TiGL with FreeCAD / OpenCascade, producing NURBS STEP, BREP, FCStd, previews and inspection reports.
- **Advanced modeling controls**: adds spatial curves, native helices, centerline lofts, guide influence, end tangent lengths, multi-body ownership and trim-to-thicken ownership checks.
- **Surface parameter contracts**: adds Boundary selection identity, marks, order and source-curve checks, plus Fill support-face ownership, continuity-control readback and saved-definition receipts.
- **Parameters and configurations**: adds global variables, configuration expressions, 15 fixed functions, joint variable/dimension dependency checks, unit and angle checks, configuration feature suppression/unsuppression and saved readback.
- **Dimension-description compilation**: adds centered flat-bottom blind holes, centered through holes and inward shells with explicit openings, together with unit normalization, requirement coverage and independent geometry contracts.
- **Persistent geometry references**: adds source semantics and root identity, controlled dimension/fillet history, receipt replay, and bounded complete spline control-net, knot, weight, trim and sphere-parameter revision checks.
- **Nominal physical threads and assembly checks**: adds nominal internal/external physical threads, left/right-handed inputs and thread-depth checks, plus instantaneous relative motion bases for spatial open-tree concentric mechanisms.
- **Drawing output**: adds matching assembly SLDDRW / PDF files, native model/component dimension import, saved dimension readback and configurable view layout and pagination.
- **Execution control and recovery**: adds `cad_execution_status`, `cad_pause_execution` and `cad_build_gordon_surface`, bringing the public MCP interface to 18 tools; adds cross-process native execution leases, deadlines, status receipts and recovery identity checks.
- **Installation and delivery**: adds desktop-bundled Codex CLI selection, version/build identity checks, source-test entry points and per-file SHA-256 manifests, with a Windows x64 package and checksum files.

## Get this release

[Windows package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.07/auto-solidworks-26.10.07-windows-x64.zip) · [SHA-256](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.07/auto-solidworks-26.10.07-windows-x64.zip.sha256) · [English installation guide](download.en.md) · [Source](../README.en.md)
