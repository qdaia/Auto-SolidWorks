# Surface workflows

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Current local integration version: `26.10.07`; revision: `local-integration-20261007`. Incorporates the implemented `.34.validation` optimizations and preserves existing modeling/drawing capabilities. This integration is checked by a fresh build, offline regressions and public MCP compile/dry-run without starting SOLIDWORKS. B01-B06 engineering remains paused and incomplete. Native successes and failures remain bound to their original revisions and bounded fixtures; this version is not natively recertified. Complex Boundary, Curvature Fill/G2, general changed topology, standard thread fits and mechanism limits/couplings remain open. Read the [bundled capability manifest](capability-manifest.json) and `cad_get_capabilities`.
<!-- Capability manifest SHA256: 04e0dc6afa7d8b14dc5ebde410f73c1ae0b050afaacbde725a504d9aec910ac1 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

Current revision is defined in the bundled capability manifest. The trim adapter displays selectable regions and releases locks in finally; `SurfaceTrim.merge` controls sewing. Boundary now uses InsertNetBlend2 ordered selection roles, checks the selected inventory and reads back both curve families. Selected edge Contact/Tangent Fill and seam fixtures now have native saved readback. Boundary itself was not rerun in this upgrade, and Curvature Fill failed retained-control acceptance. Use [advanced modeling](advanced-modeling.md) for current scope; no broad G2 certificate is claimed.

For dimensioned domed parts, distinguish `SR` (spherical radius) from a cylindrical hole. A construction-centre offset is not an overall height. Preserve exact arcs and analytic surfaces where the drawing defines them; a fitted spline through points does not certify their extrema or curvature. Always declare envelope and source geometry verification before calling a build successful.

## Choose the feature

| Intent | Operation | Bounded controls |
|---|---|---|
| Multiple ordered sections with a second curve direction | SurfaceBoundary | `profile_ids` direction 1, `guide_ids` direction 2; None/NormalToProfile first/last direction-1 end conditions |
| Patch a closed boundary with optional interior guide constraints | SurfaceFill | Contact-only sketch `profile_ids`, or explicit edge/support-face `surface.fill_boundaries`; Contact/Tangent native fixtures, guarded Curvature requests; fill resolution 1–3 |
| Carry an open/closed section along a path | SurfaceSweep | `sketch_id`, `path_sketch_id`, optional `guide_ids`; legacy surface orientation or advanced `sweep` controls |
| Copy or offset selected faces | SurfaceOffset | Face queries, `distance_mm >= 0`; zero is a face copy; `reverse` chooses the other direction |
| Join sheets / close a solid | SurfaceKnit | Explicit body/face queries; tolerance 0.0001–0.1 mm, zero/omitted means 0.01 mm |
| Turn a valid sheet into a solid wall | Thicken | Explicit sheet selections and thickness |

Use existing SurfaceLoft for a loft request; Boundary uses a native boundary feature, not a renamed loft or an imported mesh. No external FreeCAD/OCC process or remote model service is required. The surface sweep adapter uses the legacy `InsertSweepSurface3` family already compatible with the local interop; actual native behavior still needs acceptance.

## Curve and frame requirements

Create earlier `ProfileSketch` or `SpatialCurve` operations for profiles, paths and guides. A native `Helix` can also serve as a sweep path. Preserve their order. Different IDs are required in each group, and a curve cannot serve both directions. At most 128 sketches per direction are accepted. Compiler-generated `depends_on` contains all referenced sketches; raw IR is rechecked. Source-model feature names alone cannot replace these typed sketch IDs in the new surface operations.

For cross-plane curves, express origin, normal and X direction in model millimeters using explicit `frame`. Local Y is normal cross normalized/projected X. Native spline interpolation consumes the supplied points; they are not an arbitrary NURBS control net. Verify intended meeting points in model space, not screenshot proximity.

For a four-sided patch whose two profiles and two guides meet at their endpoints, set:

```json
"surface": {
  "require_boundary_corner_match": true,
  "connection_tolerance_mm": 0.01
}
```

This assertion requires exactly 2x2 curves, each one open spline or connected open line/arc primitive on an explicit frame. Sketch constraints, dimensions, edits, face attachments and plane-ID attachments can alter the actual coordinates and therefore make this offline assertion unverifiable; fix the input or defer the check to native inspection. Endpoint orientation may be reversed, but input curves are never reordered, snapped or moved. A gap above tolerance fails with `SURFACE_CORNER_GAP`. Missing/wrong-type references fail with `SURFACE_SKETCH_REFERENCE`.

Do not enable the endpoint assertion for networks intended to intersect in their interiors. The check does not detect interior self-intersections, twisting, tangent mismatch, curvature mismatch or surface fairness. When omitted, ordinary native boundary construction remains available, but connectivity has not been certified offline. Do not label it a Gordon interpolator: the execution kernel is SolidWorks Boundary Surface.

## Fill, sweep and downstream solids

Fill takes ordered boundary sketch IDs separately from interior constraints. It currently uses **Contact/G0 only**. Do not promise tangent/G1 or curvature/G2 support against adjacent faces. `surface.optimize_fill` defaults true; resolution is 1–3 and controls non-optimized fills. Native SolidWorks must verify that all boundary pieces form the intended patch.

Boundary end conditions belong in `surface.start_condition` / `surface.end_condition`; accepted values are `None` and `NormalToProfile`. NormalToProfile is not continuity certification against an adjacent face. Sweep orientation belongs in `surface.sweep_orientation`; accepted values are `FollowPath` and `KeepNormalConstant`. Unsupported controls must not be silently substituted.

Declare `expected_solid_body_count: 0` and an explicit `expected_surface_body_count` for sheet-only models. Offset normally retains the original sheet, so account for both sheets. Form solids explicitly with SurfaceKnit or Thicken after the surfaces exist. Do not raise knit tolerance to hide an unresolved gap.

## Offline examples

Historical native evidence for `0.6.0+surface.20261001.1` (2026-10-01): contact Fill, FollowPath Sweep and planar Offset passed creation/rebuild/native reopen/STEP readback; bowed 2×2 Boundary returned no feature. The current revision corrects the legacy selection-role mismatch; native acceptance was not rerun. Capability responses separate historical fixture status from current native status. No other networks, end conditions, G1/G2 or fairness are certified.

These are **drafts**, passed to `cad_create_model_plan(draft=..., nativeOutputPath=...)`, not executable IR:

- [Bowed 2x2 boundary patch](examples/surface-boundary.json)
- [Four-boundary contact fill](examples/surface-fill.json)
- [Open-profile surface sweep](examples/surface-sweep.json)
- [Offset of a planar circular sheet](examples/surface-offset.json)

When the user excludes native validation, stop at compilation and `cad_build_model(irJson=..., dryRun=true)`. Health, inspection, export and ordinary build can start SolidWorks. When native acceptance is later authorized, verify feature type, rebuild, sheet/solid counts, save/reopen and STEP readback, then inspect shape, seams and continuity appropriate to the requested design.

## Design references

- [CurvesWB Gordon implementation](https://github.com/tomate44/CurvesWB/blob/main/freecad/Curves/gordon.py): informed native Boundary connectivity preconditions. The separate [Gordon pipeline](gordon-surface.md) bundles pinned, unmodified upstream code and executes it through local FreeCAD/OCC; native SolidWorks Boundary remains a different kernel path.
- [CadQuery surface filling](https://github.com/CadQuery/cadquery/blob/master/cadquery/occ_impl/shapes.py): inspired separate boundary and interior constraints. No OCC dependency was added.
- [ABC-1M on Hugging Face](https://huggingface.co/datasets/ADSKAILab/ABC-1M) and [BrepGen](https://github.com/samxuxiang/BrepGen): research/evaluation candidates only; no model weights or dataset were downloaded, trained or accepted.
