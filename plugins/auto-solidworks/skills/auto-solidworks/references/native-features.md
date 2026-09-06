# Native feature parameters

Each row describes `feature` inside a NativeFeature operation. Irrelevant fields should be omitted. Names and enums are case-sensitive as shown in the MCP schema.

## Selecting geometry

`selections` contains typed queries with `kind` (Feature, Face, Edge, Body, Plane, Axis), optional `feature_id` for an earlier operation, `name` for an existing feature/body, `persistent_reference`, `geometry` (Any, Plane, Cylinder, Cone, Sphere, Torus, Line, Circle), `position_mm:{x,y,z}`, `direction:{x,y,z}`, `radius_mm`, `tolerance_mm` (default 0.05), `all_matches` and `selection_mark`.

Face/edge positions lie on the requested entity in model coordinates. A surface-body position must lie on one of its faces. Solid-body position filters use the body's bounding box; constrain ownership/name if boxes overlap. Direction is parallel in either orientation. Multiple matches cause a useful ambiguity error unless all_matches=true. A persistent reference is checked against geometric selectors and feature ownership; semantic selection can resolve an outdated reference after rebuild.

Obtain references with `cad_inspect_model(inputPath, queries)`. Do not guess a face/edge index or use a random first edge.

## Parameters

| Kind | Main fields and selection marks |
|---|---|
| ReferencePlane | frame with origin_mm, normal, x_direction |
| ReferenceAxis | axis_start_mm and axis_end_mm, distinct model points |
| Chamfer | selections, distance_mm; chamfer_mode DistanceAngle with angle_degrees in (0,90), TwoDistances with second_distance_mm, or EqualDistance |
| Fillet | radius_mm; edge selections mark 0, or face sets with marks 2 and 4; tangent_propagation defaults true |
| RevolveBoss / RevolveCut | sketch_id, axis_id, angle_degrees up to 360; thickness_mm>0 enables thin revolve; reverse, merge |
| Shell | thickness_mm and faces to remove; reverse controls outward shell |
| Draft | angle_degrees; neutral face mark 1, draft faces mark 2 |
| Mirror | feature mark 1 or body mark 256, plane mark 2; merge |
| LinearPattern | count, spacing_mm; axis mark 1, feature mark 4 |
| CircularPattern | count, angle_degrees; axis mark 1, feature mark 4 |
| SketchPattern | sketch_id containing placement points; feature selections mark 4 |
| Combine | body selections; boolean_mode Union, Subtract or Intersect. First body is subtraction target |
| MoveBody | body selections; translation_mm, rotation_degrees, copy, count. Translation occurs first, then XYZ rotation about the global origin; copies are made only once |
| Split | cutting plane/surface selections; resulting bodies remain in the part |
| ThinExtrude | open sketch_id, thickness_mm, depth_mm; reverse, merge |
| Rib | open line sketch_id, thickness_mm; extends a rib parallel to the sketch into existing supporting material |
| LoftBoss / LoftCut | profile_ids in order, optional guide_ids, merge for boss; thickness_mm for thin variants |
| SweepBoss / SweepCut | sketch_id, path_sketch_id, optional guide_ids, merge for boss; thickness_mm for thin variants |
| SheetMetalBase | sketch_id, thickness_mm, radius_mm, k_factor (default 0.5); depth_mm for open bent sections |
| EdgeFlange | edge selections, angle_degrees, distance_mm; native bend-outside flange with inherited bend radius |
| Flatten | flattened=true unfolds existing flat-pattern features; false restores folded state |
| WeldmentMember | path_sketch_id, existing absolute profile_path (.SLDLFP), profile_configuration, optional angle_degrees |
| TrimWeldment | target bodies mark 1, trimming bodies/faces mark 2; distance_mm is explicit weld gap, weldment_end_condition Trim/Miter/Butt1/Butt2 |
| SurfacePlanar | closed sketch_id |
| SurfaceExtrude | open/closed sketch_id, depth_mm, reverse |
| SurfaceLoft | ordered profile_ids, optional guide_ids |
| SurfaceTrim | trimming plane/sketch/surface mark 0; surface bodies to retain mark 2, each with position_mm inside the retained region |
| SurfaceKnit | surface selections; merge removes redundant topology, try_to_form_solid optionally closes a solid; distance_mm is knit tolerance (0.0001–0.1 mm) |
| Thicken | surface face/body selections, thickness_mm, reverse, merge |
| SetDimension | dimension_name, dimension_value, dimension_is_angle |
| Suppress / Restore | feature selections |

Profile sketches and guides must intersect as required by SolidWorks. Small radii, zero-thickness contacts, disconnected ribs, inconsistent guides and self-intersecting surfaces can be rejected by native geometry creation. The failure reports its operation id; revise that geometry and rebuild a new output.

## Holes

Hole uses diameter_mm, hole_centers:[{xmm,ymm}], optional frame, through_all (default true), depth_mm for blind holes, reverse and hole_kind:

- Simple: a drilled cylindrical cut.
- Counterbore: larger counterbore_diameter_mm and positive counterbore_depth_mm.
- Countersink: larger countersink_diameter_mm and countersink_angle_degrees (included angle, default 90).
- Tapped: diameter_mm is the drill diameter; supply thread_major_diameter_mm and thread_designation, e.g. M8 x 1.25. The model contains a drilled hole and cosmetic thread annotation, not a physical helix.

Tapped resolves the circular entrance on its own drilled feature. Curved/open entrances may not support cosmetic threads; failure does not silently become a Simple hole. If nominal representation fits the requested deliverable, explicitly recompile Simple geometry, preserve the specification/effective depth in the name and assumptions, and state this in the final result. depth_mm controls both cylinder and cosmetic depth in one Tapped operation. See [drawing execution notes](drawing-modeling-playbook.md) for separate depths and exact conical drill points.

Hole centers are coordinates in its frame. With no frame, they use Front X/Y at Z=0. Specify the entrance frame and reverse deliberately for holes entering another face.

Use `cad_list_weldment_profiles` to discover local profile files, then `cad_inspect_model` to read their native configurations. The plugin does not bundle a copy of the SolidWorks profile library.
