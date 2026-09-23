# Drawing omission review

This workflow is internal. Keep the user's direct-result experience: do not show candidate lists or ask for unchanged dimensions/plan approval. Ask only when a critical source requirement remains genuinely missing or conflicting after targeted inspection.

## Read before planning

1. Call `cad_read_drawing` on the actual source. Retain `omission_inventory_path` and `omission_inventory_sha256`; never synthesize or modify that inventory. Read all source pages. Selecting only some pages retains the unexamined pages as gaps; ingest the full document before claiming complete coverage.
2. Call `cad_review_drawing_coverage(inventoryPath, pageNumber?, offset?, limit?)`. Its initial status is `requires_visual_review`. Pagination covers candidates, raw regions and issues; continue until `next_offset=null` and use `onlyUnresolved=false` to retrieve accounted candidates again.
3. Inspect the unannotated full-page image first to understand layout. Then inspect every returned nonblank detail crop and any additional source/view crop needed for annotation context. The normal grid uses 1400-pixel tiles with 280-pixel overlap and keeps source resolution. The colored candidate overview is navigation only, never a substitute for the raw image.
4. Create an independent visual feature list from the source before comparing it to the intended model. Record previously missed holes, slots, shoulders, internal lines, section references, notes and uncertainty. Preserve this list when revising operations.

Candidate locations use normalized [0,1] page coordinates: origin at top left, x right, y down. Native PDF points are mapped through recorded transforms. `related_candidate_ids` is a proximity/fragment suggestion, never an asserted dimension attachment.

Candidates include Annotation, Geometry and ResidualInk. Native path commands are grouped by original subpath. A separate raster channel locates ink not covered by text boxes or primitive strokes; a geometry bounding box never explains all pixels inside it. ResidualInk may include a missed hole, hatch, caption or harmless rendering difference. Inspect it before classification. Original images remain untouched.

Split callouts such as `4×` + `⌀6` can yield a `spatial-annotation-assembler` candidate. The originals and their observation IDs remain present. Do not infer absent operators or combine neighboring bare numbers. Conflicting alternatives stay candidates for actual source reading.

## Retain explicit review data

Put the review in `drawing_context.omission_review`:

```json
{
  "inventory_path": "C:\\work\\ingestion\\omission-inventory.json",
  "inventory_sha256": "<exact hash returned by ingestion>",
  "candidates": [
    {
      "candidate_id": "<actual annotation candidate ID>",
      "disposition": "Feature",
      "feature_ids": ["mounting_holes"],
      "dimension_ids": ["hole_diameter", "hole_count"],
      "rationale": "The source label specifies four diameter-6 holes; its leader and front-view hole group agree."
    }
  ],
  "regions": [
    {
      "region_id": "<actual raw region ID>",
      "state": "Reviewed",
      "feature_ids": ["mounting_holes"],
      "findings": "Inspected the original crop. Four hole mouths and their shared callout are accounted for; no unresolved additional feature in this crop."
    }
  ],
  "cross_view_checks": [],
  "additional_findings": []
}
```

This is a shape example, not a ready-to-use review. Supply every actual candidate and required raw region, with findings based on images you inspected. Do not automatically manufacture these statements from the model plan or from detector output.

Allowed dispositions:

- `Feature`: supply existing feature IDs. Quantitative annotations need matching dimension IDs, including both values of a compound annotation. Each dimension must cite the candidate's actual `observation_ids` and belong to the mapped feature's operation. Repeated-hole count uses `Unitless`, the computed operation field `feature.hole_center_count`, and independent verification `expected_count`.
- `NonModel`: supply one of `border`, `title_block`, `dimension_graphic`, `centerline`, `hatch`, `note`, `noise`, with a specific rationale. A numeric annotation cannot be classified as a border or line. Notes that affect geometry, thread representation or delivery requirements must remain requirements, not be dismissed as decorative content. Never bulk-dismiss unread candidates.
- `Duplicate`: supply `duplicate_of` naming a directly resolved candidate of the same kind at the same page location. Text must agree. A source fragment may instead point to a resolved assembler candidate that explicitly contains that fragment. Duplicate chains, cycles, distant repeats and missing targets fail.
- `Unresolved`: preserves the gap and blocks a complete plan.

An explicit `candidate_ids` array can replace `candidate_id` when every listed item really shares the same disposition, targets and rationale. This saves repetition; it does not waive individual checks. Use exactly one form. No wildcard, implicit region-wide dismissal or overlapping batch is accepted.

OCR conflict/correction: retain the original candidate, use `corrected_literal` for what the raw image actually shows, and add `correction_reason`. Do not change an expected number to match the built model. A corrected quantitative feature annotation must still be bound to its source dimensions.

## Check views and newly noticed features

Every feature supported by more than one declared view needs a `cross_view_checks` entry with `id`, all relevant `view_ids`, `feature_ids`, `state:Reviewed`, and actual correspondence `evidence`. Compare positions, repeated counts, axes, cut directions and section/hidden-line semantics. `Conflict` or `Unresolved` blocks. A declaration is not an automatic geometric proof.

Use `additional_findings` for visual features missed by all detectors: `id`, `page_number`, normalized `bounds`, `description`, `state`, and `feature_ids`. A newly found but unmodeled feature must remain unresolved. A legitimate construction-only feature can cite a localized visual finding explaining its derived role.

Call `cad_review_drawing_coverage` again with the full `drawingContext`. Fix localized issues, inspecting additional context when needed. `declared_review_complete` means candidate accounting and declared review are complete. It is not detector recall, actual visual-attention verification, complete drawing equivalence, topology, or GD&T certification. Two readings by the same model are correlated; do not turn agreement into a confidence guarantee.

Only then compile the typed draft. Complete drawing plans fail if the review is absent. Both compile and build recheck source, inventory, observation and raw-image hashes. Recovery retains the same source requirements; changed review/requirements can require an earlier compatible checkpoint or a rebuild. Never edit hashes to bypass a rejection.

## Implementation references and scope

The implementation reuses the plugin's local PdfPig, raster processing and Tesseract providers. It adds no neural model download or cloud service.

- [pdfplumber](https://github.com/jsvine/pdfplumber): native PDF object inventory and visual debugging informed the separate source-candidate list; this release uses existing PdfPig rather than bundling Python/pdfplumber.
- [SAHI](https://github.com/obss/sahi): overlapping tiled processing informed raw detail coverage; these crops do not claim SAHI model inference or improved detector recall.
- [eDOCr2 examples](https://github.com/javvi51/edocr2/blob/main/docs/examples.md): region-first visual interpretation and per-drawing tests informed the workflow; eDOCr2/TensorFlow are not bundled.
- [PaddleOCR text detection](https://huggingface.co/PaddlePaddle/PP-OCRv5_server_det): detector locations and retained candidate outputs are a useful comparison interface; PaddleOCR is not installed or substituted in this release.

Remaining limitations: raster primitives and residual ink are heuristic; arbitrary rotated split callouts may remain separate; detection recall is unmeasured on independent real drawings. Automatic semantic segmentation, automatic source-view reprojection comparison and geometric proof of cross-view relations are not provided by this feature. All such gaps remain visible in the documented scope.
