# Drawing interpretation and assemblies

## Drawing to typed parameters

Read the actual source image or PDF. `cad_read_drawing` provides page images, text/primitive observations and dimension candidates. It does not automatically establish every dimension's target or reconstruct arbitrary 3D topology.

Optional `viewHints` locate AI-interpreted views/annotation rectangles using normalized [0,1] coordinates: left, top, right, bottom, id, page_number and view_type. Local OCR is disabled; legacy ocr_rotation_clockwise and ocr_page_segmentation_mode fields remain schema-compatible but do not trigger recognition. The original image is preserved and must be inspected directly by the AI.

For unlabeled, nonconflicting three views, use first-angle: upper-left Front, lower-left Top, upper-right Left. Labels, projection symbols and user clarification override this default. A pictorial view is shape context, not a source of unmarked dimensions. A visible dimension overrides a pixel estimate. Upscaling cannot recover missing strokes or resolve a conflicting annotation.

In `cad_create_model_plan`, optionally include `draft.drawing_context`:

```json
{
  "source_path":"C:/Models/source.png",
  "projection":"FirstAngle",
  "projection_status":"Assumed",
  "views":[{"id":"front","kind":"Front","page_number":1}],
  "dimensions":[{
    "id":"diameter","operation_id":"hole","parameter_path":"feature.diameter_mm",
    "value":10,"unit":"Millimeter","status":"Stated","source_literal":"Ø10",
    "view_ids":["front"],"observation_ids":[]
  }]
}
```

Fact statuses are Stated, Derived, Assumed and Unknown. Derived facts include `derivation`; preserve the source literal and observation IDs when available. Parameter paths are relative to a typed draft operation, e.g. `primitives.0.width_mm`. Inch and Meter values are converted when checking the referenced mm field; angles use Degree. A mismatched value, unknown bound dimension, missing operation/view or unexplained derivation gives a parameter error. No additional approval is required.

The agent remains responsible for correct attachment of dimensions, section/hidden-line interpretation, hole depth/direction, coordinate derivation and choosing a supported modeling sequence. Record noncritical assumptions. Request a missing critical value only when it prevents a defensible model.

## Assembly

Create components first, or inspect existing SLDPRT/SLDASM files. Then call `cad_build_assembly` with `plan`:

- name, absolute native_path ending in SLDASM, optional STEP/STP/STL export_paths.
- Every successful build also writes same-directory, same-stem `.SLDDRW` and `.pdf` files automatically. Do not put these in `export_paths` or call the drawing exporter again for the same paths. Deliver all three files. Existing drawing/PDF outputs (including directories) are rejected before building, even with `overwrite_allowed=true`.
- components: unique id, existing native path, optional configuration, translation_mm, rotation_degrees, fixed.
- mates: name, kind, first and second. Each endpoint has component_id and an optional typed entity query in component-local coordinates. Omit entity for whole-component Lock mates.
- Mate kinds: Coincident, Concentric, Parallel, Perpendicular, Distance, Angle, Lock. value is mm for Distance or degrees for Angle; anti_aligned and lock_rotation are optional.
- `check_interference` and `reject_unapproved_interference` default true. Unapproved interference fails before save; declare exact component IDs and volume limits in `allowed_interferences` for intended overlaps. `require_fully_constrained_components` optionally rejects floating components. See [engineering reliability](engineering-reliability.md).

Component rotation applies X, then Y, then Z, before translation. Fix a base component if appropriate. Floating components may move or rotate during mating. Add enough compatible mates for the intended constraints; a successful underconstrained assembly is not necessarily fully fixed.

The result includes actual SolidWorks instance names, transforms, fixed states, native mate names, interference volumes and reopened status. It verifies component identities/transforms/fixed states and mate identities after native save and reopening. Source component files remain unchanged. `cad_inspect_model` also reads saved assemblies and lists their components; query part faces/edges using the component's part file.

The nested `drawing` result includes native/PDF paths, four first-angle A3 views and saved-drawing readback. Overall `success=true` requires both the assembly and its drawing/PDF output. On drawing failure, the verified assembly remains available with `reopened=true`, but overall success is false; report the partial result and use `cad_export_drawing` with unused paths to retry. Inspect the PDF pages. Native dimensions from the assembly and its components are imported into drawing views. The schedule lists assembly-level parameters only; `view_dimensions` includes the actually placed component parameters with qualified names, units and values. `dimensions_reopened=true` records a successful saved-drawing dimension readback. Exports with zero placed dimensions fail explicitly. This does not generate component drawings, BOM balloons or a complete manufacturing definition.

## Recovery

The executor identifies the failing operation and distinguishes invalid parameters, unresolved references, feature creation, rebuild, measurements, save/export and connection problems. Inspect geometry and correct that operation or its dependent sketches; rerun a revised plan to a new output. `source_model_path` lets a new plan edit a saved part copy. Do not blindly retry a geometric failure or substitute a different part. `cad_executor_health` is for connection diagnosis, not a mandatory pre-build step.

New drawing reviews require `require_dimension_evidence=true`. Follow the returned `review_workspace.recognition_checklist` and [dimension evidence contract](dimension-evidence.md); locate dimension endpoints before binding, and retain cross-view checks.
