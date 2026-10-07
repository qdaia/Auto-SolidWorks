# Advanced modeling upgrade

For the subsequent complex-modeling revision, read [complex controls and scoped native evidence](complex-modeling.md). It adds centerline/guide/tangent-length controls to boss/cut lofts, expands spatial corner validation and fixes standard trim-to-thicken ownership. Previous native cases were not rerun.

Current scope is native part modeling. Parameters below are typed members of `feature` on a `NativeFeature` operation. Natural-language deterministic templates do not infer these controls; use `cad_create_model_plan(draft=...)`. Use mm and degrees, and preserve independent source requirements.

## Advanced features

`Fillet.variable_fillet.edges` specifies one edge query and `start_point_mm`, `end_point_mm`, `start_radius_mm`, `end_radius_mm` per edge. Leave ordinary `selections` empty. Endpoint coordinates determine radius assignment even if the native edge is reversed. Open edges only; no closed circular edge, intermediate radius handles, setback, asymmetric fillet or conic profile. `curvature_continuous` is optional, but its retained flag is checked. Native acceptance covers an open straight-edge 1-to-3 mm variable fillet, not all edge types or all curvature-continuous shapes.

`LinearPattern.linear_pattern` exposes `second_count`, `second_spacing_mm`, `reverse_second_direction`, `geometry_pattern`, `second_direction_seed_only`. Select direction 1 with mark 1, direction 2 with mark 2, feature seeds with mark 4 or **body seeds with mark 256**. Count and spacing for direction 1 stay in `count`/`spacing_mm`. At most 10000 total instances. Native acceptance covers a 3×3 body pattern; a disjoint whole-boss feature seed failed and is not presented as successful.

`SweepBoss`, `SweepCut`, `SurfaceSweep` accept `sweep.orientation`: FollowPath, KeepNormalConstant, FollowFirstGuide, FollowTwoGuides, TwistAlongPath, TwistWithConstantNormal. `twist_angle_degrees` is permitted only for twist modes (maximum absolute 36000); guide modes require their guides. `keep_tangency`, `advanced_smoothing`, `merge_smooth_faces` are separate controls. Do not also set a conflicting legacy `surface.sweep_orientation`. The created twist mode/angle is read back. Native acceptance covers a 180-degree rectangular boss sweep and a round-section helix coil; Cut, two-guide and surface-twist variants still need their own native fixtures.

`LoftBoss`, `LoftCut`, `SurfaceLoft` accept `loft.maintain_tangency`, `close`, `start_condition`, `end_condition`. End conditions are None or NormalToProfile. A closed loft needs at least three profiles and no start/end conditions. Normal matching is not G1/G2 attachment to a support face. A two-circle boss with normal end conditions was built and saved; general connector, tangent influence and seam alignment controls remain outside this upgrade.

## Spatial curves and helix paths

`SpatialCurve.spatial_curve` accepts `curve_kind` (InterpolatingSpline or Polyline), `points_mm:[{x,y,z},...]`, `closed`, `natural_ends`. Coordinates are explicit global model coordinates. Supply 2..4096 points (at least three for closed); do not repeat the first point, since `closed` adds closure. Consecutive duplicates, nonfinite coordinates and an already-open sketch are rejected. Creation uses a real 3D sketch; its native type and segment inventory are checked. This is an interpolation spline, not arbitrary NURBS control points, weights or knots.

`Helix` requires `sketch_id` referring to an earlier one-circle planar sketch and `helix:{pitch_mm,revolutions,start_angle_degrees,clockwise}`. `reverse` controls axial reversal. A constant-pitch cylindrical native helix is created, and its pitch, revolutions, starting angle and orientation are read back. It can be a sweep path; the path need not be a sketch. It does not provide variable pitch, taper, automatic thread standards, runout or lead-in geometry. A solid helical wire coil was verified; a standards-compliant screw thread was not.

Spatial curve operations are intermediate features in a part. The existing acceptance route still requires a solid or surface body, so a curve-only SLDPRT is not currently accepted.

## Edge-based Fill with support faces

Use `surface.fill_boundaries` instead of `profile_ids`: each item has an `edge` query, `contact` (Contact, Tangent, Curvature), and `support_face` for Tangent/Curvature. Each query must identify exactly one entity. The support face must be adjacent to the boundary edge. Compiler dependencies include nested edge/support-face feature IDs. Legacy sketch-boundary Fill stays Contact-only.

Native Contact and Tangent fixtures passed creation and saved constraint readback. Curvature requests are routed explicitly to the API but **did not retain the requested control in the planar and cylindrical fixtures**; the executor rejects them. The earlier unguarded Curvature result was not confirmed by strict retained-control readback; an isolated setter probe returned unknown controls (-1). Do not infer its actual class from a generic mismatch message. Do not advertise or deliver those models as G2. Retained-contact type is verified after creation, and an attempted control update must succeed; there is no substitution to a lower continuity class.

## Selection and independent shape verification

Edge queries additionally accept `length_mm`, paired `start_point_mm`/`end_point_mm` (orientation independent), and `adjacent_face_count` (1 or 2). Face queries accept `area_mm2` and `area_tolerance_mm2`. Length uses the trimmed native curve parameter interval, not its unbounded supporting curve. These constraints supplement persistent identity and geometric ownership. A failed or ambiguous match stays an error; no first candidate is chosen. Solid-body position queries still use a bounding-box test, so do not use them alone to distinguish overlapping or concave bodies.

`verification.edge_shapes` checks independent native edge length and endpoints. Each check needs id, source_literal, edge, length_mm, start_point_mm, end_point_mm and tolerance_mm. `verification.surface_continuity` needs id, source_literal, edge, samples (3..4096), gap_tolerance_mm, angle_tolerance_degrees, require_tangency. The seam must have exactly two adjacent faces. Every uniform native edge parameter sample measures distance to both trimmed faces and unsigned tangent-plane angle. Missing, incomplete or nonfinite measurements are unverifiable. A 33-sample fillet seam passed, and a 90-degree cube crease was rejected. These checks also run on saved-file inspection. For verification queries, use the saved native feature name or persistent reference; source operation IDs with different native names are not automatically rebound by the read-only inspection route.

Edge length is a full trimmed-curve metric. Seam checks remain samples: they do not prove all unsampled points, G2 curvature or exact BREP equivalence. Use independent source criteria, never measured output values copied back as expected values.

## Evidence and references

Local evidence: `artifacts/modeling-upgrade-20261001` in the source checkout, with baseline snapshots, managed regression logs, all native failures, saved readback JSON and version/runtime identity. Separate current fixture proof from historical surface/Boundary results and untested variants.

Official APIs: [FeatureFillet3](https://help.solidworks.com/2025/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IFeatureManager~FeatureFillet3.html), [InsertFillSurface2](https://help.solidworks.com/2022/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IFeatureManager~InsertFillSurface2.html), [Fill retained control](https://help.solidworks.com/2025/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IFillSurfaceFeatureData~SetCurvatureControl.html). Source-level comparisons with pinned build123d, CurvesWB and SolidWorks MCP implementations are recorded in the earlier modeling review; no reference project or model weights were silently substituted for native SolidWorks history.
