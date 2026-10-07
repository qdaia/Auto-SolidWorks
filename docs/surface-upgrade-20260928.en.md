# Local surface modeling upgrade: 2026-09-28

Development build `0.6.0+surface.20260928.1` adds native Boundary, contact Fill, Sweep and Offset/copy feature adapters. This is a local development build; SolidWorks was not launched for acceptance and nothing was published to GitHub.

## Scope

- Boundary uses ordered direction-1 profiles and direction-2 guides, with None/NormalToProfile conditions on the first/last direction-1 profiles.
- Fill separates boundary sketches from interior constraints. It exposes resolution 1–3 and optimization, with contact/G0 only.
- Sweep accepts a section, path and optional guides, with FollowPath or KeepNormalConstant orientation.
- Offset accepts explicit face queries, millimeter distances, reverse direction and zero-offset face copies.
- Offline validation rejects broken/non-sketch references, missing IR dependencies, duplicate curves and invalid controls. Knit tolerance is bounded to 0.0001–0.1 mm, with zero selecting the existing 0.01 mm default.
- An optional explicit-frame 2x2 endpoint assertion checks four distinct model-space corners without snapping curves. Solver-driven coordinates are unverifiable offline. It does not certify interior intersections or surface continuity.

Read the [surface workflow and four JSON drafts](../plugins/auto-solidworks/skills/auto-solidworks/references/surface-modeling.md).

## References and adoption

[CurvesWB's Gordon implementation](https://github.com/tomate44/CurvesWB/blob/main/freecad/Curves/gordon.py) informed cross-direction connectivity preconditions. [CadQuery's filling implementation](https://github.com/CadQuery/cadquery/blob/master/cadquery/occ_impl/shapes.py) informed the separation of boundaries and interior constraints. New C# code adapts these design ideas to native SolidWorks; neither external geometry implementation is bundled, and this is not a Gordon interpolator.

[BrepGen](https://github.com/samxuxiang/BrepGen) and [ABC-1M on Hugging Face](https://huggingface.co/datasets/ADSKAILab/ABC-1M) were inspected as research/evaluation candidates only. No weights or dataset were downloaded, trained or reproduced.

## Evidence limits

Local interop signatures and builds, offline geometry/contract regressions, managed production-adapter tests and public MCP compile/dry-run checks cover development behavior. Managed proxies are not COM acceptance. Native creation, save/reopen, STEP readback, visual fairness and G1/G2 continuity remain unrun. The sweep adapter uses the legacy `InsertSweepSurface3` API family; native acceptance must establish its behavior on the installed SolidWorks version.
