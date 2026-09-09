---
name: auto-solidworks
description: Create and edit local parametric SolidWorks parts and assemblies from engineering images, PDFs or dimensioned text. Supports native features, multibody, sheet metal, weldments, surfaces, assemblies, STEP/STL export and first-angle SLDDRW/PDF engineering drawings from saved parts.
---

# Auto SolidWorks

Complete the user's modeling request through the bundled MCP tools. Keep the plan and tool details internal; normally return only the final model, requested exports and a brief creation status.

## Modeling flow

1. Read `cad_get_capabilities` to see the executable geometry operations.
2. For an image or PDF, call `cad_read_drawing` and inspect its source/page images and observations. Interpret the dimensions and geometry from the actual drawing. A text-only request skips this step.
3. Read [typed geometry](references/modeling-operations.md). For drawing-based modeling, also read [source requirements and recovery](references/source-requirements-and-recovery.md): retain the source feature inventory, bind critical dimensions and declare independent measured checks. Then call `cad_create_model_plan` with a typed `draft` and an absolute `nativeOutputPath`. Simple plate/cylinder descriptions can use `text` instead. Supply exactly one of `draft` and `text`. Optional `exportPaths` select STEP/STP/STL outputs.
4. Pass the returned `ir_json` to `cad_build_model`. Its default `dryRun=false` builds immediately. There is no approval, run registration, fingerprint authorization or required dry-run step. Use `dryRun=true` only when checking a draft without creating a model is useful.
5. The builder checks declared `verification` requirements before saving and repeats them on the reopened native file. Read back with `cad_inspect_model`, passing the same `verification` for additional inspection. Inspect requested STEP/STP exports separately with the geometric subset of checks (native parameter names do not survive STEP), and compare bodies, dimensions and volume. Inspect the generated views, then return the final files and a brief status. Successful declared checks do not certify complete drawing equivalence.

For multi-view parts with repeated holes or several feature families, read [drawing-to-model execution notes](references/drawing-modeling-playbook.md). It covers bounded image inspection, coordinate/feature tables, saved base/detail stages, explicit thread representation, targeted recovery and final read-back. Keep full OCR payloads and intermediate plans internal; avoid repeating unchanged work after one feature fails.

For intentional half/intersecting holes, stepped bores, countersinks or drill tips, read [local geometry verification](references/local-geometry-verification.md). Declare source-derived trimmed-surface samples with outward normals and diameter/cone angle; add boundary-clearance samples where a wall must be absent. These are finite local checks, not whole-feature or thread certification. Retain complete-cylinder checks wherever the source requires a full wall; never weaken a failed expectation to obtain a pass.

For assemblies, read [drawing and assembly workflows](references/drawing-and-assembly.md), inspect/create component files, then call `cad_build_assembly`. Return its `.SLDASM` and requested exports. For existing models, use `cad_inspect_model` before referring to dimensions/features; `source_model_path` edits a separate part copy. For weldments, `cad_list_weldment_profiles` finds local SLDLFP files and inspection reads their configurations.

For engineering drawings from an existing saved part, call `cad_export_drawing` with absolute `inputPath`, unused `.SLDDRW` `nativePath` and `.pdf` `pdfPath`. Read [reference drawing export](references/reference-drawing-export.md). It creates first-angle views and a parameter schedule, preserves the source and verifies the saved drawing by reopening it. Inspect the actual pages before delivery; complex annotations may need layout refinement.

`cad_executor_health` is for diagnosing a connection problem; it may start SolidWorks. It is not a prerequisite for every build.

## Dimensions and geometry

- Omitted units default to mm. Convert explicitly declared other units into mm before preparing a draft.
- Model inspection returns unique native parameters with `system_value`, `parameter_type`, `unit` and converted `value`. Use the returned unit: angular parameters are Degree, pattern counts are Unitless, and lengths are Millimeter. Never multiply every parameter by 1000.
- For an unlabeled, conflict-free three-view layout, use first-angle: upper-left front, lower-left top, upper-right left. Explicit source labels, symbols or the user's directions take precedence.
- Use the supplied dimensions and explain derived coordinates internally. Ask only for a missing or conflicting critical value that prevents modeling. Do not ask for approval of already supplied dimensions or a complete plan.
- You are the primary visual interpreter: inspect the actual drawing and reason about geometry, view relationships and dimension attachment. OCR is auxiliary candidate evidence. Use optional `viewHints` for existing view/OCR support.
- For drawings, supply `drawing_context` with `features`, critical parameter bindings and source-linked `verification`. Keep `require_complete_bindings=true`; do not disable coverage or remove a failed expectation to make a build pass. Text-only modeling may omit the drawing context. New unitless counts use `Unitless`; omitted length units remain mm. Critical values must be stated or geometrically derived, never guessed.
- When adding sketch constraints, use native driving dimensions for sizes that must remain fixed. Initial primitive coordinates can move during constraint solving.
- Build from typed geometry. Preserve arcs, holes, topology, through/blind intent and feature positions. Distinguish effective thread depth, cylindrical drill depth and drill-point geometry. Record nominal thread representation explicitly; a failed cosmetic thread is not a created annotation. If an operation is unsupported, explain the missing capability instead of silently substituting a different part.
- Fix invalid dimensions, open contours or broken feature references and recompile the affected draft. The runtime checks these as part of creating valid geometry.

## Outputs

Use the user's requested output directory. Otherwise choose a new descriptive directory under `Documents/AutoSolidWorks/Models`, with a timestamp when needed. Use absolute file paths. `overwriteAllowed` defaults to false; ordinary modeling creates a new file and leaves existing models intact.

Set `recovery.directory` to the task working directory. Multifeature builds automatically checkpoint the first supported solid-producing operation; use `after_operation_ids` to choose additional stable boundaries after major geometry. When a feature fails, preserve the returned `recovery` data. Recompile the full corrected draft with `recovery.resume_manifest_path` to reuse its unchanged prefix; only the remaining suffix executes. If source requirements or prefix operations change, use an earlier compatible checkpoint or rebuild. Do not repeat an identical failed plan, weaken expectations, or keep retrying an unresolved reference. Do not deliver a copied source, checkpoint or failed output. Use a unique native filename when another document with that filename is open. The runtime hides construction geometry in previews without suppressing it and saves an isometric opening view.

This edition uses typed drafts and returned IR directly. Older run-id/plan-fingerprint build requests belong to the preserved development source, not this tool interface.

See [native features](references/native-features.md) for parameters and selection marks. Physical helical threads and general GD&T interpretation are outside this version. Native drawing export currently supports saved parts, A3 first-angle views and up to 44 source parameters. A parameter schedule is not a complete manufacturing definition. Existing geometric variants still need valid profiles and intersections; do not imply that an API feature family supports every possible SolidWorks option.
