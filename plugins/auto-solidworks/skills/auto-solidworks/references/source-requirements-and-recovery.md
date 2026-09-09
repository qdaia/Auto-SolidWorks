# Source requirements, measured checks and local recovery

Keep this data internal. The agent is the primary visual interpreter; OCR is auxiliary. The runtime does not decide whether every feature in an image has been noticed. It checks the completeness of the declared inventory and its consistency with the executed plan, then measures the real model independently.

## Establish requirements before authoring operations

Inspect the source image and retain every identified feature, its source views/literals, dimensions and uncertainty. Identify origin and axes, then derive global coordinates explicitly. Preserve a requirement even when a modeling operation is revised. Never regenerate expected values from the failed geometry merely to make a check pass.

For an image/PDF draft, `drawing_context` includes:

- `source_path`: existing absolute drawing path; the compiler records its SHA-256.
- `views`: existing view records and source labels; default first-angle projection is Assumed.
- `dimensions`: ID, operation ID, parameter path, source value/unit/literal, view IDs and status. Derived values require `derivation`. `critical` defaults true. Counts explicitly use `Unitless`; length defaults remain mm.
- `features`: ID, source literal, view IDs, operation IDs, status and `critical` (default true). Every operation must appear in this inventory. Critical features require `critical_parameters` and `verification_check_ids`.
- Each critical parameter records `{operation_id, parameter_path, dimension_id}`. Every declared critical dimension must be covered. Critical facts/features cannot be Unknown or Assumed. Noncritical choices must remain explicitly marked, not silently promoted to source facts.

`require_complete_bindings` defaults true. Keep it true for drawing modeling; false exists for explicitly partial legacy contracts, not as an escape from a failed check. Construction-only operations can be assigned to a noncritical source feature, with an explicit source relationship and rationale.

The compiler checks source dimensions against operation fields and separate measured expectations. Compiled operations, source bindings and expected checks are protected against accidental IR edits: revise the typed draft and recompile. This adds no user approval step.

## Verification fields in a typed draft

`verification` contains `bounds`, `cylinder_groups`, `native_dimensions`, `surface_samples`, `boundary_clearances` and `bindings`. For source-defined partial walls, shoulders and cones see [local geometry verification](local-geometry-verification.md). Each check has a unique `id`, `source_literal`, and (for drawing modeling) `source_dimension_ids`.

`bindings` maps each referenced source dimension to a numeric expected check field:

```json
{"dimension_id":"width","check_id":"overall","parameter_path":"size_mm.x"}
```

These expected fields are authored from source requirements, independently of the modeling operations. The compiler compares them after unit conversion. Every source dimension named by a check needs a binding. Valid paths are bounds `size_mm.x/y/z`, cylinder `diameter_mm`, `length_mm`, `expected_count`, `axis_starts_mm.<index>.x/y/z`, and native dimension `value`. `expected_count` is derived from the number of axis starts and binds a Unitless count.

### Overall dimensions

```json
{"id":"overall","source_literal":"80 x 50 x 10 mm",
 "source_dimension_ids":["width","height","thickness"],
 "size_mm":{"x":80,"y":50,"z":10},"tolerance_mm":0.05}
```

Bounds measure model-space axis-aligned extents. Use the established coordinate convention. A bounding box alone does not verify internal holes, wall thicknesses or asymmetric details.

### Cylindrical geometry

```json
{"id":"mounting_holes","source_literal":"2 holes, diameter 10, axes at x=-20,+20; cylindrical wall z=0..10",
 "source_dimension_ids":["hole_diameter","wall_length","hole_count","hole_x1","hole_x2"],
 "diameter_mm":10,"axis_starts_mm":[{"x":-20,"y":0,"z":0},{"x":20,"y":0,"z":0}],
 "direction":{"x":0,"y":0,"z":1},"length_mm":10,
 "interior":true,"exact_count":true,"tolerance_mm":0.05,"direction_tolerance_degrees":0.1}
```

Axis starts and direction are in global model coordinates. `length_mm` measures the complete cylindrical wall; it excludes conical drill tips, chamfers and cosmetic/physical thread depth. `interior=true` checks cavities; false checks external cylindrical walls. Count and position use actual cylinder axes rather than raw face counts. Seam-split/adjacent faces are grouped; disjoint coaxial spans remain separate.

`exact_count=true` means all complete cylindrical spans with this diameter, axis direction and interior/exterior sense across the part must be listed. If unrelated holes share these characteristics, combine their expectations where possible, or use an explicitly scoped check with `exact_count=false` and check those other groups separately. Counterbores need separate checks for each diameter and axial span. Do not count faces as holes.

Partial, intersected, open or irregular cylinder walls may return `unverifiable`; do not claim they are complete drilled holes. This check verifies cylindrical wall geometry, not thread annotations, drill-tip shape or general through/blind semantic certification. Add suitable native dimension checks where available; otherwise report the unverified requirement or missing measurement capability rather than substituting a weak box check.

### Native driving dimensions

```json
{"id":"extrusion_depth","source_literal":"Thickness 10 mm","source_dimension_ids":["thickness"],
 "dimension_name":"D1@block","value":10,"unit":"Millimeter","tolerance":0.05}
```

Read the actual saved feature parameter. Values use the named unit; angles convert from native radians. Missing or suppressed feature dimensions are unverifiable. Use native driving dimensions in sketches for critical editable sizes. Native dimension names do not survive STEP import, so STEP verification uses only geometric checks. Geometry checks and native parameter checks complement each other.

The builder runs these checks before final save and after reopening the native file. An empty verification set is not reported as verified. A failed result has no deliverable `native_path`; the recorded file may be a checkpoint, a source copy, or a partial output. `completed` with successful declared checks is still not full source-drawing or GD&T equivalence certification.

## Recovery without rebuilding the unchanged core

```json
{"recovery":{"directory":"C:\\work\\task\\checkpoints","after_operation_ids":["main_body"]}}
```

Recovery defaults enabled. With no explicit boundaries, plans with more than two operations checkpoint the first supported primary boss/revolve/loft/sweep. Automatic checkpoints require a positive-volume solid and a complete feature map; they are not available for pure surface stages. An optional checkpoint failure is reported as `checkpoint_unavailable`, and the last valid checkpoint remains usable.

Each checkpoint saves a separate SLDPRT copy, verifies a read-only reopen, and records file hash, source requirements, completed operations and persistent feature references. It is a construction checkpoint, not a finished or accepted drawing model.

After failure:

1. Read the failed operation ID, exception/HRESULT, dependencies, measured mismatch, and `recovery` result. For reference failures, inspect the actual geometry and revise only the incorrect selection or feature parameters.
2. Keep the full operation list, all source requirements, and intended final acceptance. Set `recovery.resume_manifest_path` to the returned `manifest_path`, then recompile and build to a new output filename.
3. The runtime verifies the checkpoint and unchanged prefix, restores its actual features, skips those completed operations, and replays the remaining suffix in order. This conservatively replays all later operations, including dependent ones; it does not claim arbitrary middle-of-tree mutation.
4. If the corrected operation precedes the checkpoint, or source requirements/acceptance changed, choose an earlier compatible checkpoint or rebuild. Never suppress this rejection or overwrite the checkpoint.

Retry only after a concrete correction. Stop repeating the same failure after two targeted corrections; retain the last usable checkpoint and report the unresolved feature/capability or ask for genuinely missing source information. Never ask again for unchanged dimensions or plan approval.
