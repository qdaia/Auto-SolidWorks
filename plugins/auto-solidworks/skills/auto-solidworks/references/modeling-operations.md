# Typed geometry

For drawing-based drafts, also read [source requirements and recovery](source-requirements-and-recovery.md). `drawing_context.features` links all operations and critical parameters to source evidence; `verification` carries separate expected measurements. `recovery` controls verified checkpoints and suffix replay. Text-only drafts can omit drawing context.

Use the live MCP input schema for property spelling. All lengths in operations are mm and angles are degrees. Output paths are separate tool arguments.

## Coordinates and plans

A draft has `name`, `source_text`, ordered `operations`, optional `assumptions`, `source_model_path` and `drawing_context`. Each operation has a unique `id`, readable `name`, and `type`. The compiler adds dependencies from sketches, profiles, paths, axes, support features and entity selectors.

| Standard sketch | Coordinates | Extrusion axis |
|---|---|---|
| Front | X,Y | Z |
| Top | X,Z | Y |
| Right | Z,Y | X |

For arbitrary orientations, provide `frame: {origin_mm:{x,y,z}, normal:{x,y,z}, x_direction:{x,y,z}}`. The executor normalizes the normal, orthogonalizes X, and derives Y = normal cross X. Sketch coordinates are local to that frame. `plane_id` uses an earlier ReferencePlane; to control local axes precisely, use `frame`. `face_attachment` uses an existing planar face and a model-space pick point.

Geometry expectations: `expected_solid_body_count` defaults to 1; surface-only plans require 0 and a positive `expected_surface_body_count`. Optional `expected_volume_mm3`, `volume_tolerance_percent`, `expected_bounding_box_mm:{x,y,z}` and `bounding_box_tolerance_mm` compare native measurements. Use tolerances appropriate to native integration: a SW 2025 ellipse can differ from analytic volume by about 0.025% even at high mass-property accuracy.

## Sketches

`ProfileSketch` has a plane/frame/attachment and `primitives`.

| Primitive type | Fields |
|---|---|
| CenteredRectangle | center_xmm, center_ymm, width_mm, height_mm |
| ThreePointRectangle | three ordered corner points |
| Circle | center_xmm, center_ymm, diameter_mm |
| Ellipse | center_xmm, center_ymm, width_mm, height_mm |
| Polygon | ordered `points:[{xmm,ymm}]` |
| CompositeCurve | closed connected `curves`; Line or ThreePointArc with start/end and point_on_arc |
| OpenCurve | line/arc curves, optional construction=true |
| Spline | points and optional closed=true |
| Slot | two arc-center points and width_mm; total length is center distance plus width |
| Points | points for locations, constraints and sketch-driven patterns |

Use `role=Inner` for internal loops of a boss profile. Keep closed contours connected and non-self-intersecting. Use separate sketches for profiles, paths, guides and pattern locations.

`constraints` support Coincident, Horizontal, Vertical, Tangent, Concentric, Symmetric, Equal, Parallel, Perpendicular, Fixed and Midpoint. Each has `entities:[{primitive_index,segment_index,part}]`; part defaults to Segment and can be StartPoint, EndPoint or CenterPoint. For Points primitives, segment_index selects the point. Symmetric uses two entities/points followed by the construction symmetry line.

`dimensions` have name, kind, value, entities and label_position. Kinds: Distance, Horizontal, Vertical, Radius, Diameter, Angle. These are native driving dimensions. Primitive coordinates provide initial geometry: adding relations can move unconstrained entities or change a free radius. Supply driving dimensions when those values must stay fixed. Do not fix an entire entity if a requested driving dimension must later change it.

`edits` support Offset (`offset_mm`, `both_directions`, `chain`, `cap_ends`, entities) and TrimClosest (one segment entity and `pick_point` on the portion to remove). TrimClosest uses native power-trim to remove the interval bounded by nearest intersections. Edits occur after constraints and dimensions; later edits must not reference a segment already removed.

## Extrusion

ExtrudeBoss/ExtrudeCut use `sketch_id`, `end_condition` and `depth_mm`. Blind and MidPlane use positive depth; MidPlane is the total depth. Cut ThroughAll spans material along the cut direction. UpToNext and UpToSurface require material/target in that direction. UpToSurface has `end_reference:{support_operation_id,pick_xmm,pick_ymm,pick_zmm}`. `start_offset_mm`, `reverse_start_offset`, `reverse_direction` control orientation. Boss `merge=false` creates a separate body.

## Native features and existing models

Use `type=NativeFeature` and `feature:{kind:..., ...}`; see [native feature parameters](native-features.md). Inspect existing models with `cad_inspect_model` to read actual names, dimensions, configurations and entity references. To edit a part, set `source_model_path` and a different output path: the executor copies the source first. Existing feature selections use `name`; new operation references use `feature_id`.

SetDimension uses `dimension_name` such as `D1@BaseBoss`, `dimension_value` and optional `dimension_is_angle`. Suppress/Restore select named features. Save new versions rather than overwriting user files.

## Example

```json
{
  "name":"plate","source_text":"80 x 50 x 10 mm plate with a 10 mm through hole",
  "operations":[
    {"type":"ProfileSketch","id":"profile","name":"BaseSketch","plane":"Front",
     "primitives":[{"type":"CenteredRectangle","center_xmm":0,"center_ymm":0,"width_mm":80,"height_mm":50}]},
    {"type":"ExtrudeBoss","id":"plate","name":"BaseBoss","sketch_id":"profile","end_condition":"Blind","depth_mm":10},
    {"type":"NativeFeature","id":"hole","name":"CenterHole","feature":{"kind":"Hole","diameter_mm":10,"hole_centers":[{"xmm":0,"ymm":0}]}}
  ]
}
```
