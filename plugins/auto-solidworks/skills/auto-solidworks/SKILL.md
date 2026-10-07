---
name: auto-solidworks
description: 根据工程图片、PDF 或尺寸描述创建和编辑本地 SolidWorks 参数化零件与装配体。默认使用简体中文输出，支持原生特征、多实体、钣金、焊件、曲面、装配、STEP/STL 和第一角投影工程图。
---

# Auto SolidWorks

## 默认中文输出

除非用户明确要求其他语言，所有面向用户的新增文本均使用简体中文：回复、模型名称、设计树特征和辅助草图／基准面名称、装配零部件和配合名称、工程图页名和视图名、标题、注释、参数表、验证结果与错误说明。

生成 typed draft 时，`name` 使用中文机械术语（例如“安装板”“定位孔”“外边圆角”“边界曲面”）。模型和操作的 `name` 可省略以使用编译器中文默认名；装配零部件的 `name` 可省略以使用“零部件_序号”。操作 `id`、`depends_on`、枚举、字段、错误码、原生参数路径和 STEP/STL/PDF 等格式标识属于接口标识，保持原值。用户明确提供的名称、文件路径、型号、螺纹规格以及从输入文件读取的原文按原样保留；不得翻译后再用于特征选择或尺寸编辑。

导出工程图默认采用中文页名、中文标准视图名称、中文标题／参数表说明及中文字体。源模型中已有英文参数路径保留在结构化返回值中，图面使用中文显示标签；不得为了改显示文字而修改源模型。旧模型和已导出的文件不会被升级安装自动改写，重新生成或重新导出时应用中文默认值。

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Current local integration version: `26.10.07`; revision: `local-integration-20261007`. Incorporates the implemented `.34.validation` optimizations and preserves existing modeling/drawing capabilities. This integration is checked by a fresh build, offline regressions and public MCP compile/dry-run without starting SOLIDWORKS. B01-B06 engineering remains paused and incomplete. Native successes and failures remain bound to their original revisions and bounded fixtures; this version is not natively recertified. Complex Boundary, Curvature Fill/G2, general changed topology, standard thread fits and mechanism limits/couplings remain open. Read the [bundled capability manifest](references/capability-manifest.json) and `cad_get_capabilities`.
<!-- Capability manifest SHA256: 04e0dc6afa7d8b14dc5ebde410f73c1ae0b050afaacbde725a504d9aec910ac1 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

Complete the user's modeling request through the bundled MCP tools. Keep the plan and tool details internal; normally return only the final model, requested exports and a brief creation status.

## coding-tools bridge prerequisite

If coding-tools is also selected or requested, or this skill is active but the Auto SolidWorks `cad_*` tools are not directly exposed in the current tool list, first read [coding-tools → Auto SolidWorks bridge](../coding-tools-auto-solidworks-bridge/SKILL.md) and use it to establish the MCP connection. Do not declare the local plugin unavailable before checking that bridge path. The bridge is connection-only; after the MCP tools are reachable, continue with this skill.

## Modeling flow

1. Read `cad_get_capabilities` to see the executable geometry operations.
2. For an image or PDF, call `cad_read_drawing` and inspect its source/page images and observations. Read [drawing omission review](references/drawing-omission-review.md). Retain the returned immutable `omission_inventory_path` and hash, inspect the raw overview and every nonblank detail, and account for annotations, geometry and residual ink through `cad_review_drawing_coverage`. Interpret geometry from the actual source; candidate detection never establishes complete understanding. A text-only request skips this step.
3. Read [typed geometry](references/modeling-operations.md). For drawing-based modeling, also read [source requirements and recovery](references/source-requirements-and-recovery.md): retain the source feature inventory, bind critical dimensions and declare independent measured checks. Include `drawing_context.omission_review`, resolved candidate explanations, crop findings and cross-view checks. Query remaining gaps before compiling. Then call `cad_create_model_plan` with a typed `draft` and an absolute `nativeOutputPath`. Simple plate/cylinder descriptions can use `text` instead. Supply exactly one of `draft` and `text`. Optional `exportPaths` select STEP/STP/STL outputs.
4. Pass the returned `ir_json` to `cad_build_model`. Its default `dryRun=false` builds immediately. There is no approval, run registration, fingerprint authorization or required dry-run step. Use `dryRun=true` only when checking a draft without creating a model is useful.
5. The builder checks declared `verification` requirements before saving and repeats them on the reopened native file. Read back with `cad_inspect_model`, passing the same `verification` for additional inspection. Inspect requested STEP/STP exports separately with the geometric subset of checks (native parameter names do not survive STEP), and compare bodies, dimensions and volume. Inspect the generated views, then return the final files and a brief status. Successful declared checks do not certify complete drawing equivalence.

For multi-view parts with repeated holes or several feature families, read [drawing-to-model execution notes](references/drawing-modeling-playbook.md). It covers bounded image inspection, coordinate/feature tables, saved base/detail stages, explicit thread representation, targeted recovery and final read-back. Keep full OCR payloads and intermediate plans internal; avoid repeating unchanged work after one feature fails.

For intentional half/intersecting holes, stepped bores, countersinks or drill tips, read [local geometry verification](references/local-geometry-verification.md). Declare source-derived trimmed-surface samples with outward normals and diameter/cone angle; add boundary-clearance samples where a wall must be absent. These are finite local checks, not whole-feature or thread certification. Retain complete-cylinder checks wherever the source requires a full wall; never weaken a failed expectation to obtain a pass.

For assemblies, read [drawing and assembly workflows](references/drawing-and-assembly.md), inspect/create component files, then call `cad_build_assembly`. Return its `.SLDASM`, automatically generated same-name `.SLDDRW` and `.pdf`, and requested exports. Check the nested `drawing` result and inspect the PDF; drawing failure makes the overall build unsuccessful while retaining the saved assembly. For existing models, use `cad_inspect_model` before referring to dimensions/features; `source_model_path` edits a separate part copy. For weldments, `cad_list_weldment_profiles` finds local SLDLFP files and inspection reads their configurations.

For engineering drawings from an existing saved part or assembly, call `cad_export_drawing` with absolute `inputPath`, unused `.SLDDRW` `nativePath` and `.pdf` `pdfPath`. Read [reference drawing export](references/reference-drawing-export.md). It creates first-angle views with native model/component dimension annotations and a parameter schedule, preserves the source and verifies the saved drawing by reopening it. Check `view_dimensions` and `dimensions_reopened`, then inspect the actual PDF pages before delivery; complex annotations may need layout refinement. Exports with no placed dimensions fail. Model Items can only import stored native dimensions: retain explicit driving sketch/feature dimensions when constructing parts; initial primitive coordinates alone do not produce width/position annotations.

`cad_executor_health` is for diagnosing a connection problem; it may start SolidWorks. It is not a prerequisite for every build.

## Dimensions and geometry

阅读 [建模可靠性修复](references/engineering-reliability.md)：`SetDimension` 按原生类型处理数量、长度和角度；不要给阵列数量标 mm。每个最终零件都强制重开与实体故障检查，不能用空 `verification` 跳过。普通板／圆柱模板要求驱动尺寸和完全定义草图。攻丝孔用 `thread_depth_mm` 独立声明牙深。装配默认拒绝未允许干涉；有意过盈必须列准确组件 ID 和体积上限；用户要求装配完全约束时设 `require_fully_constrained_components:true`。

For new complex part chains, read [complex modeling controls and evidence](references/complex-modeling.md): native centerline lofts, guide influence, retained tangent lengths, reliable trim interior points and trim-to-thicken body membership. Only five new complex fixtures were run in this revision. The new spatial Boundary trial failed; SurfaceLoft complex controls fail validation rather than becoming unverified geometry.

For complex open curve-network patches, read [local Gordon surfaces](references/gordon-surface.md) and use `cad_build_gordon_surface` when neutral STEP geometry is suitable. This path actually reuses pinned CurvesWB/TiGL locally through FreeCAD/OCC; it does not launch SolidWorks. Native SolidWorks feature history still uses the typed surface workflow below. Keep Gordon output/STEP acceptance separate from native SLDPRT or G1/G2 acceptance.

For advanced part modeling, read [advanced controls and current native evidence](references/advanced-modeling.md). Selected variable fillet, body pattern, spatial curve/helix, sweep, loft, edge Fill and seam fixtures were run natively in this version. Curvature Fill requests failed retained-control acceptance and must not be delivered as G2. Existing Boundary evidence remains separate. When the user prohibits SolidWorks, use compile/dry-run only; that restriction takes precedence over native workflows.

- Omitted units default to mm. Text normalizes explicit mm/cm/m/in and Chinese aliases; a trailing tuple unit qualifies the tuple. Typed draft fields remain mm. Unconsumed text requirements fail; plain extrusion all-edge fillets require an explicit radius, while ambiguous or richer selections need typed drafts.
- Model inspection returns unique native parameters with `system_value`, `parameter_type`, `unit` and converted `value`. Use the returned unit: angular parameters are Degree, pattern counts are Unitless, and lengths are Millimeter. Never multiply every parameter by 1000.
- For an unlabeled, conflict-free three-view layout, use first-angle: upper-left front, lower-left top, upper-right left. Explicit source labels, symbols or the user's directions take precedence.
- Use the supplied dimensions and explain derived coordinates internally. Ask only for a missing or conflicting critical value that prevents modeling. Do not ask for approval of already supplied dimensions or a complete plan.
- You are the primary visual interpreter: inspect the actual drawing and reason about geometry, view relationships and dimension attachment. OCR is auxiliary candidate evidence. Use optional `viewHints` for existing view/OCR support.
- For drawings, supply `drawing_context` with `features`, critical parameter bindings, `omission_review` and source-linked `verification`. Keep `require_complete_bindings=true`; do not disable coverage, invent an ingestion inventory, bulk-label unread candidates as noise, or remove a failed expectation to make a build pass. Only record a crop as Reviewed after actually inspecting it. Text-only modeling may omit the drawing context. New unitless counts use `Unitless`; omitted length units remain mm. Critical values must be stated or geometrically derived, never guessed.
- For a repeated hole callout, bind its count to `feature.hole_center_count` (computed from the executed hole-center list) and the independent cylinder check's `expected_count`. A decorative `feature.count` is not a hole-count binding. OCR corrections retain the original candidate and record the actual source reading and reason. Candidate accounting is not full-drawing, topology or GD&T certification.
- When adding sketch constraints, use native driving dimensions for sizes that must remain fixed. Initial primitive coordinates can move during constraint solving.
- Build from typed geometry. Preserve arcs, holes, topology, through/blind intent and feature positions. Distinguish effective thread depth, cylindrical drill depth and drill-point geometry. Record nominal thread representation explicitly; a failed cosmetic thread is not a created annotation. If an operation is unsupported, explain the missing capability instead of silently substituting a different part.
- Fix invalid dimensions, open contours or broken feature references and recompile the affected draft. The runtime checks these as part of creating valid geometry.

## Outputs

Use the user's requested output directory. Otherwise choose a new descriptive directory under `Documents/AutoSolidWorks/Models`, with a timestamp when needed. Use absolute file paths. `overwriteAllowed` defaults to false; ordinary modeling creates a new file and leaves existing models intact.

Set `recovery.directory` to the task working directory. Multifeature builds automatically checkpoint the first supported solid-producing operation; use `after_operation_ids` to choose additional stable boundaries after major geometry. When a feature fails, preserve the returned `recovery` data. Recompile the full corrected draft with `recovery.resume_manifest_path` to reuse its unchanged prefix; only the remaining suffix executes. If source requirements or prefix operations change, use an earlier compatible checkpoint or rebuild. Do not repeat an identical failed plan, weaken expectations, or keep retrying an unresolved reference. Do not deliver a copied source, checkpoint or failed output. Use a unique native filename when another document with that filename is open. The runtime hides construction geometry in previews without suppressing it and saves an isometric opening view.

Checkpoint hash readback permits native write-capable file handles while retaining before/after identity guards. Each checkpoint attempt saves `readback.json` with before/after geometry, including rejected attempts. Only a successfully verified `checkpoint.json` is reusable; a saved SLDPRT alone is insufficient. A boolean-intersection fixture currently shows small numerical mass-property drift and fails the unchanged strict gate; choose a later verified checkpoint rather than raising tolerance.

Recompilation creates a fresh plan request ID. Native prefix identity ignores that request ID and output paths while retaining source identity, executed operations and parameters. The installed mouse fixture verified corrected-suffix recovery from its R4 checkpoint. A changed prefix or source revision still invalidates reuse.

Sphere inspection supports source-bound radius/center checks and finite trimmed-face probes, including inward normals on spherical recesses. Use the actual native sphere parameters, not just points that resemble a sphere. See [local geometry verification](references/local-geometry-verification.md).

This edition uses typed drafts and returned IR directly. Older run-id/plan-fingerprint build requests belong to the preserved development source, not this tool interface.

See [native features](references/native-features.md) for parameters and selection marks. Helix paths and solid helical sweeps are available; automatic standards-compliant threads, runouts and general GD&T interpretation are outside this version. Native drawing export supports configurable paper geometry and first/third-angle standard views, with paginated source parameters and saved schedule readback. A parameter schedule is not a complete manufacturing definition. [Whole-model inventory and execution deadlines](references/root-hardening.md) supplement finite local probes and require independent source expectations. On deadline/outcome_unknown, retain the request ID, query `cad_execution_status`, and do not replay a mutation while pending. Existing geometric variants still need valid profiles and intersections; an API family does not imply every SolidWorks option is exposed.
