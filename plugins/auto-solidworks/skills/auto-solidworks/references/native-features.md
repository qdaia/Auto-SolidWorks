# Native feature parameters

Each row describes `feature` inside a NativeFeature operation. Irrelevant fields should be omitted. Names and enums are case-sensitive as shown in the MCP schema.

## Selecting geometry

`selections` contains typed queries with `kind` (Feature, Face, Edge, Body, Plane, Axis), optional `feature_id` for an earlier operation, `name` for an existing feature/body, `persistent_reference`, `geometry` (Any, Plane, Cylinder, Cone, Sphere, Torus, Line, Circle), `position_mm:{x,y,z}`, `direction:{x,y,z}`, `radius_mm`, `tolerance_mm` (default 0.05), `all_matches` and `selection_mark`.

Face/edge positions lie on the requested entity in model coordinates. A surface-body position must lie on one of its faces. Solid-body position filters use the body's bounding box; constrain ownership/name if boxes overlap. Direction is parallel in either orientation. Multiple matches cause a useful ambiguity error unless all_matches=true. A persistent reference is checked against geometric selectors and feature ownership; semantic selection can resolve an outdated reference after rebuild.

Obtain references with `cad_inspect_model(inputPath, queries)`. Do not guess a face/edge index or use a random first edge.

Feature-scoped Body queries resolve generated faces first and, for modifying features such as Draft, affected faces when there are no generated faces. They never substitute unrelated bodies from the whole part. Sphere face queries support actual `radius_mm` filtering; inspection returns analytic centers and radii separately from representative probe points. Failed selection returns scope, candidate/match counts and up to five closest candidates from a bounded diagnostic search. These candidates explain failure; they are not automatically selected or snapped.

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
| SurfaceBoundary | ordered profile_ids (direction 1, at least 2), guide_ids (direction 2); surface.start_condition/end_condition None or NormalToProfile on first/last direction-1 profile; optional explicit-frame 2x2 corner precheck |
| SurfaceFill | ordered boundary sketch profile_ids, optional internal constraint sketch guide_ids; contact only; surface.fill_resolution 1–3 and optimize_fill; one closed sketch or a connected boundary network |
| SurfaceSweep | sketch_id, path_sketch_id, optional guide_ids; surface.sweep_orientation FollowPath or KeepNormalConstant; reverse selects opposite sweep direction, merge controls smooth-face merging |
| SurfaceOffset | face selections, nonnegative distance_mm (zero copies faces), reverse |
| SurfaceTrim | trimming plane/sketch/surface mark 0; surface bodies to retain mark 2, each with position_mm inside the retained region |
| SurfaceKnit | surface selections; merge removes redundant topology, try_to_form_solid optionally closes a solid; distance_mm is knit tolerance (0.0001–0.1 mm) |
| Thicken | surface face/body selections, thickness_mm, reverse, merge |
| SetDimension | dimension_name, dimension_value, optional dimension_unit; legacy dimension_is_angle |
| Suppress / Restore | feature selections |

Profile sketches and guides must intersect as required by SolidWorks. Small radii, zero-thickness contacts, disconnected ribs, inconsistent guides and self-intersecting surfaces can be rejected by native geometry creation. The failure reports its operation id; revise that geometry and rebuild a new output.

See [surface workflows](surface-modeling.md) for exact scope and offline examples. SurfaceBoundary/Fill/Sweep/Offset are locally implemented but native acceptance is not run. For SurfaceKnit, omitted/zero distance_mm uses 0.01 mm; other values must be 0.0001–0.1 mm. Negative values are errors. Surface references must identify earlier sketch operations; raw IR must retain their dependencies.

## Holes

Hole uses diameter_mm, hole_centers:[{xmm,ymm}], optional frame, through_all (default true), depth_mm for blind holes, reverse and hole_kind:

- Simple: a drilled cylindrical cut.
- Counterbore: larger counterbore_diameter_mm and positive counterbore_depth_mm.
- Countersink: larger countersink_diameter_mm and countersink_angle_degrees (included angle, default 90).
- Tapped: diameter_mm is the drill diameter; supply thread_major_diameter_mm and thread_designation, e.g. M8 x 1.25. The model contains a drilled hole and cosmetic thread annotation, not a physical helix.

Tapped resolves the circular entrance on its own drilled feature. Curved/open entrances may not support cosmetic threads; failure does not silently become a Simple hole. If nominal representation fits the requested deliverable, explicitly recompile Simple geometry, preserve the specification/effective depth in the name and assumptions, and state this in the final result. `depth_mm` controls drill depth; optional `thread_depth_mm` sets independent cosmetic thread depth and cannot exceed blind drill depth. See [engineering reliability](engineering-reliability.md) for units and saved readback, and [drawing execution notes](drawing-modeling-playbook.md) for exact conical drill points.

Hole centers are coordinates in its frame. With no frame, they use Front X/Y at Z=0. Specify the entrance frame and reverse deliberately for holes entering another face.

Use `cad_list_weldment_profiles` to discover local profile files, then `cad_inspect_model` to read their native configurations. The plugin does not bundle a copy of the SolidWorks profile library.

## 名义实体螺纹

`PhysicalThread` 使用固定文件哈希的本机线程库创建可编辑实体牙型。`physical_thread.location` 默认为 `External`，使用 `Metric Die.SLDLFP`；`Internal` 使用 `Metric Tap.SLDLFP`，必须另外声明 `bore_diameter_mm` 和 `maximum_cut_diameter_mm`。入口必须是实际内孔壁的圆边。其余显式字段包括 `designation`（如 M10x1.5）、`major_diameter_mm`、`pitch_mm`、`length_mm`、`runout_allowance_mm`、`right_handed`、`axis_origin_mm`、`axis_into_part`、`profile_path` 和 `profile_sha256`。

名义规格不替代实际切削直径。本机 M10x1.5 默认 Metric Tap 的校准槽底直径约 10.1238 mm；省略底孔、将名义 10 mm 当实际槽底或选择外圆作为内螺纹入口均拒绝。实际牙边按其裁剪区间采样，检查入口、底孔、槽底、螺距、旋向和牙长，并要求两条几何独立的完整螺旋边。保存重开再次核对定义和几何。

SolidWorks 默认库是名义牙型。本合同不认证 6H／6g 等配合等级、ISO 制造公差、牙型公差或生产级螺纹；标准配合验收仍需独立的尺寸与牙型合同。本轮 `.29` 内外螺纹右旋、左旋生产正例及两个预期负例已返回，内外 STEP 均经独立几何检查。证据在 `artifacts/native-acceptance-20261004/physical-thread-production-native-29-01`，后续运行包需按当前身份重新认证。

## Advanced upgrade

See [advanced modeling](advanced-modeling.md) for variable endpoint fillets, two-direction/body patterns, sweep twist, normal-end lofts, spatial curves, native helices, edge Fill support faces, extended selectors and shape verification. These typed controls supersede the abbreviated legacy rows above. Curvature Fill native acceptance failed and is guarded against downgrade.
