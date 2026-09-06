# Drawing-to-model execution notes

Use for multi-view parts with repeated holes or several feature families. Keep the public journey: capabilities → read drawing → compile typed draft → build. Keep intermediate plans internal unless requested.

## Read and retain the source

- Interpret actual page images and useful crops yourself. OCR is a candidate list, not authoritative dimensions.
- Retain the complete drawing response in a local artifact or working variable. Print only relevant page/region paths, counts, warnings and current dimensions; do not flood the conversation with a full OCR payload.
- Start with the available page render. For dense PDFs, use a bounded render around 300 dpi and crop the relevant area before increasing resolution. Do not repeatedly render an entire large page at extreme resolution after a memory failure.
- Keep an internal feature table: source view/literal, stated value, derived coordinate, assumption and target operation. A default first-angle convention is Assumed, not Stated. Labels and symbols override it.
- Ask only for a missing or conflicting critical value that prevents a defensible model. Record noncritical nominal choices separately.

## Establish coordinates and stages

Choose global origin, axes and positive directions. For a shaft, global X is often convenient. Make an ordered axial station/radius table for the outer profile and bore, including shoulders and reliefs. Preserve circular arcs analytically.

For local frames, local Y is normal × x_direction. Check cut direction against material on opposite faces. PCD, angular phase and viewing direction are separate facts. Combine sections and auxiliary views before classifying a hole as axial/radial, blind/through or an open matching half-hole. A matching-hole note at an outer seam does not automatically mean an ordinary bolt-circle hole.

For expensive models, build and save the base geometry and major cuts in the task working directory. Add details with source_model_path pointing to that verified checkpoint, always creating a separate copy. Record the checkpoint's source drawing and successful build; never reuse an unrelated model because it looks similar.

Verify a new entrance orientation, pattern seed or topology-sensitive feature before multiplying it. On failure, revise the failed feature and dependents. Avoid rebuilding an unchanged expensive core or asking again about unchanged dimensions. Keep failed attempts outside the final delivery directory: a copied source may exist even when its edit stage failed. Link final outputs only after checking success/status and evidence.

## Preserve hole and thread meaning

For each group retain count, axis/position, drill diameter, cylindrical drill depth, effective thread depth, thread designation and tip angle where relevant. Thread depth and drill depth are different values.

- Simple creates the explicitly supplied cylindrical cut.
- Tapped adds native cosmetic threads; depth_mm controls both cylindrical and cosmetic depth in one operation. For distinct depths, use a verified staged sequence and inspect annotations after changing the cylindrical cut.
- Cosmetic threads require a uniquely selectable circular entrance edge. Curved entrances and matching half-holes may not have one.
- When the user's requested deliverable permits nominal thread geometry, explicitly compile a Simple hole, preserve designation/effective depth in its name and assumptions, and disclose that representation in the final result. Never report a failed cosmetic thread as created. Preserve explicit requirements for native annotations or physical helical teeth.
- Never guess the drill diameter from an unrecognized thread designation. If noncritical allowances or tip angles are needed, mark the chosen values Assumed.

For a required conical drill point, a typed RevolveCut can use a triangular section about the hole axis. Radius r, cylindrical depth d, included angle a give tip extension r/tan(a/2). Section points are (0,d), (r,d), (0,d+extension), with local Y along the inward axis. Pattern the verified seed if useful. Do not add a tip-angle assumption when the requested model does not need it.

Typed sketches are created without automatic snapping. If an exact circle still fails for a supported frame, inspect that frame and failure first. An explicitly revised closed contour of exact circular arcs is a valid geometric alternative; a polygonal approximation is not an equivalent cylindrical hole. Recheck affected references and constraints.

## Read back and deliver

Inspect the saved native model for bodies, dimensions, relevant feature depths, pattern spacing and critical holes. Use radius-query tolerances and group numerically near-equal radii. A split cylindrical surface may yield several faces per hole, so use location/direction and ownership when counts are ambiguous.

The executor hides construction geometry without suppressing its dependent features and saves an isometric opening view. Inspect actual previews for completeness and framing: nonzero file size alone does not establish a usable image.

Inspect requested STEP/STP exports separately with cad_inspect_model. Compare solid count, dimensions and volume with the native result, allowing an explicit translation/integration tolerance. Face and edge counts can legitimately change. The importer uses a unique working copy, closes only its own document and recycles the temporary copy. It restores application preferences and never saves source files.

Return final files and concise status, including simplifications that affect use. Export success, round-trip success and full drawing equivalence are separate facts. Modeling checks do not certify all drawing tolerances or GD&T, and full drawing certification is not an additional approval gate in the ordinary flow.
