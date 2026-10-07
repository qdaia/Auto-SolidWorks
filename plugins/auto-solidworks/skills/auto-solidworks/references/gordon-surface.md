# Local Gordon curve-network surfaces

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Current local integration version: `26.10.07`; revision: `local-integration-20261007`. Incorporates the implemented `.34.validation` optimizations and preserves existing modeling/drawing capabilities. This integration is checked by a fresh build, offline regressions and public MCP compile/dry-run without starting SOLIDWORKS. B01-B06 engineering remains paused and incomplete. Native successes and failures remain bound to their original revisions and bounded fixtures; this version is not natively recertified. Complex Boundary, Curvature Fill/G2, general changed topology, standard thread fits and mechanism limits/couplings remain open. Read the [bundled capability manifest](capability-manifest.json) and `cad_get_capabilities`.
<!-- Capability manifest SHA256: 04e0dc6afa7d8b14dc5ebde410f73c1ae0b050afaacbde725a504d9aec910ac1 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

`cad_build_gordon_surface` was introduced in historical development version `0.6.0+surface.20260930.1`; current package/version status is in the bundled capability manifest. It calls the **unmodified upstream CurvesWB/TiGL Gordon interpolator**, pinned at `e47b47927f59c87b93a1820c9f6ad4b4dc187076`, through a separate local FreeCAD Python process. FreeCAD 1.1.4 / OCC 7.8.1 is the previously tested runtime. Execution is offline and does not launch SolidWorks; this hardening revision does not rerun FreeCAD/native import acceptance.

Use this for a single open patch defined by two intersecting families of curves, including more than two sections/guides per direction. Use SolidWorks SurfaceBoundary/SurfaceLoft when native feature history is required. This tool generates a neutral NURBS sheet, not a native SolidWorks Boundary feature.

## Input

Call `cad_build_gordon_surface(draft=..., outputDirectory=...)`. The output directory must be absolute and unused. `draft.profiles` and `draft.guides` each contain 2..16 curves. Every curve has a unique `id` and `points_mm`: 2..128 model-space XYZ **interpolation points**, not control poles. Points define open FreeCAD interpolating splines. Preserve arcs/analytic surfaces in the native workflow when exact radii are required. Default `tolerance_mm` is 0.01, supported range 0.0001..0.1. `samples_per_curve` defaults to 41, supported range 11..201.

Every profile must intersect every guide exactly once. Distinct crossings must form a full rectangular network and the outer intersections must cover every curve's endpoints. Closed networks, overlapping curves and ambiguous multi-intersections are rejected. The upstream algorithm sorts and reverses its private curve copies when needed; source input points remain recorded unchanged. Curves within a family should follow consistent spatial order; use explicit point geometry, never infer connectivity from a screenshot.

## Results and evidence

Success returns absolute paths to `Surface.step`, `Surface.brep`, `Surface.FCStd`, `preview.svg`, `input.json`, and `report.json`. The report records upstream commit, FreeCAD/OCC versions, every cross-family intersection gap, sampled distances of each original curve to the generated face and reopened STEP, area/readback checks, sampled normals, NURBS degrees/pole counts and file hashes. FCStd contains input curves, interpolation points and the generated face; regeneration uses `input.json` and this tool, not automatic document recompute.

Check `success` before delivering a model. Failed jobs retain input/report/log evidence in their directory. Do not use their partial outputs as successful models. Three 5x5 networks (dome, saddle and asymmetric reversed curves) and malformed networks are exercised by `tests/gordon_smoke.py` through public MCP.

These finite measurements do not certify global self-intersection freedom, G1/G2 continuity between patches, fairness, drawing equivalence, closed-solid creation or native SolidWorks editability. STEP readback is through OCC; SolidWorks import/save/reopen acceptance is separately **not run**. Do not describe stored FCStd geometry as a fully parametric SolidWorks model.

## Local dependency setup

`cad_get_capabilities.complex_surface_modeling` reports configuration presence, not proof of runtime health. The separate FreeCAD dependency is not bundled in the plugin ZIP. Obtain the official Windows portable FreeCAD package, verify its official SHA256 and extract locally. Run `scripts/configure-surface-runtime.ps1 -PythonExecutable <absolute-FreeCAD-bin/python.exe>` in the plugin directory. It probes FreeCAD/Part/numpy and writes `%LOCALAPPDATA%/AutoSolidWorks/dependencies/surface-runtime.json`, preserving an existing configuration backup. `CAD_SURFACE_RUNTIME_CONFIG` can select another local config. Modeling calls never download dependencies or contact a model service.

## Provenance

- [CurvesWB](https://github.com/tomate44/CurvesWB) is bundled under `surface/CurvesWB` with original source and LGPL/Apache license texts. Its Gordon and BSpline algorithms originate in [DLR TiGL](https://github.com/DLR-SC/tigl); source headers retain attribution.
- [FreeCAD](https://github.com/FreeCAD/FreeCAD/releases/tag/1.1.4) supplies the local OpenCascade runtime.
- [CadQuery](https://github.com/CadQuery/cadquery) and [ABC-1M](https://huggingface.co/datasets/ADSKAILab/ABC-1M) were researched; their algorithms/data are not used by this backend. ABC-1M is evaluation data, not an executable surface builder.
