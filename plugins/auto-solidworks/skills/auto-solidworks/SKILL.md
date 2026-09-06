---
name: auto-solidworks
description: Create and edit local parametric SolidWorks parts and assemblies from engineering images, PDFs or dimensioned text. Supports sketches, holes, revolve, fillet, chamfer, shell, rib, patterns, multibody, loft, sweep, sheet metal, weldments, surfaces, assembly mates and STEP/STL export.
---

# Auto SolidWorks

Complete the user's modeling request through the bundled MCP tools. Keep the plan and tool details internal; normally return only the final model, requested exports and a brief creation status.

## Modeling flow

1. Read `cad_get_capabilities` to see the executable geometry operations.
2. For an image or PDF, call `cad_read_drawing` and inspect its source/page images and observations. Interpret the dimensions and geometry from the actual drawing. A text-only request skips this step.
3. Read [typed geometry](references/modeling-operations.md), then call `cad_create_model_plan` with a typed `draft` and an absolute `nativeOutputPath`. Simple plate/cylinder descriptions can use `text` instead. Supply exactly one of `draft` and `text`. Optional `exportPaths` select STEP/STP/STL outputs.
4. Pass the returned `ir_json` to `cad_build_model`. Its default `dryRun=false` builds immediately. There is no approval, run registration, fingerprint authorization or required dry-run step. Use `dryRun=true` only when checking a draft without creating a model is useful.
5. Read back the saved model with `cad_inspect_model` to check critical dimensions, hole groups and geometry. Inspect requested STEP/STP exports separately and compare bodies, dimensions and volume. Inspect the generated views, then return the final files and a brief status. Successful modeling checks do not certify drawing equivalence.

For multi-view parts with repeated holes or several feature families, read [drawing-to-model execution notes](references/drawing-modeling-playbook.md). It covers bounded image inspection, coordinate/feature tables, saved base/detail stages, explicit thread representation, targeted recovery and final read-back. Keep full OCR payloads and intermediate plans internal; avoid repeating unchanged work after one feature fails.

For assemblies, read [drawing and assembly workflows](references/drawing-and-assembly.md), inspect/create component files, then call `cad_build_assembly`. Return its `.SLDASM` and requested exports. For existing models, use `cad_inspect_model` before referring to dimensions/features; `source_model_path` edits a separate part copy. For weldments, `cad_list_weldment_profiles` finds local SLDLFP files and inspection reads their configurations.

`cad_executor_health` is for diagnosing a connection problem; it may start SolidWorks. It is not a prerequisite for every build.

## Dimensions and geometry

- Omitted units default to mm. Convert explicitly declared other units into mm before preparing a draft.
- For an unlabeled, conflict-free three-view layout, use first-angle: upper-left front, lower-left top, upper-right left. Explicit source labels, symbols or the user's directions take precedence.
- Use the supplied dimensions and explain derived coordinates internally. Ask only for a missing or conflicting critical value that prevents modeling. Do not ask for approval of already supplied dimensions or a complete plan.
- Use optional `viewHints` to isolate views or rotate sideways annotation OCR. Treat OCR numbers as candidates, especially on low-resolution drawings. Optional `drawing_context` records view labels, projection, stated/derived/assumed values and their target operation fields; the compiler checks unit-converted values against the draft.
- When adding sketch constraints, use native driving dimensions for sizes that must remain fixed. Initial primitive coordinates can move during constraint solving.
- Build from typed geometry. Preserve arcs, holes, topology, through/blind intent and feature positions. Distinguish effective thread depth, cylindrical drill depth and drill-point geometry. Record nominal thread representation explicitly; a failed cosmetic thread is not a created annotation. If an operation is unsupported, explain the missing capability instead of silently substituting a different part.
- Fix invalid dimensions, open contours or broken feature references and recompile the affected draft. The runtime checks these as part of creating valid geometry.

## Outputs

Use the user's requested output directory. Otherwise choose a new descriptive directory under `Documents/AutoSolidWorks/Models`, with a timestamp when needed. Use absolute file paths. `overwriteAllowed` defaults to false; ordinary modeling creates a new file and leaves existing models intact.

Store checkpoints and failed attempts in the task working directory. Do not deliver an existing copied source file when its edit stage failed. The runtime hides construction geometry in previews without suppressing it and saves an isometric opening view.

This edition uses typed drafts and returned IR directly. Older run-id/plan-fingerprint build requests belong to the preserved development source, not this tool interface.

See [native features](references/native-features.md) for parameters and selection marks. Physical helical threads, native drawing-sheet generation and general GD&T interpretation are outside this version. Existing geometric variants still need valid profiles and intersections; do not imply that an API feature family supports every possible SolidWorks option.
