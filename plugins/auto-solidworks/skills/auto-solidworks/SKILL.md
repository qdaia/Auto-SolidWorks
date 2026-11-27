---
name: auto-solidworks
description: 按尺寸或工程图在本机 SolidWorks 创建零件和装配体，保存 SLDPRT、SLDASM，按需导出 STEP 或 STL。
---

# Auto SolidWorks

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Local version: `1127`; revision: `source-four-stage-2`. Four default stages: recognition, dimension binding, compile, model. Retains real parameter/unit/source endpoint checks and native save. Removes mandatory review ledgers, material probes, previews and separate delivery review. Explicit legacy revisions retain their original rules. No full drawing/BREP/GDT equivalence claim. See [capability manifest](references/capability-manifest.json).
<!-- Capability manifest SHA256: da5b1c411b6abd4f0eba30d326d137f777bbefb1e8dcdd33fd84db58d5010694 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

默认使用毫米和度。把新模型保存到独立目录，传绝对路径，避免覆盖现有文件。

- **按尺寸建零件**。简单尺寸可直接传 `cad_create_model_plan(text=..., nativeOutputPath=...)`。有多个特征时提交 `draft`，按需用 `cad_get_contract` 查对应字段。拿到 `plan_id` 后调用 `cad_build_model`。需要 STEP 或 STL 时在编译时传入 `exportPaths`。
- **按图建零件**。用 `cad_read_drawing` 准备原图和局部图。查看原图，确定视图、基准和特征，绑定尺寸原文、端点及实际参数。按原图填写 `drawing_context` 和 `draft`。调用 `cad_create_model_plan`，必要时传 `workspaceId`，按诊断修正草案，然后调用 `cad_build_model`。图示缺少必要尺寸时集中向用户确认。
- **装配**。按 `assembly` 合同声明现有零件或子装配体、初始位姿和配合。需要辨认配合面、边或基准时用 `cad_inspect_model`。用 `cad_build_assembly` 保存 SLDASM，检查配合及干涉结果。
- **查询**。返回 `artifact_id` 时可用 `cad_read_result` 分页读取。后台任务返回 `request_id` 时用 `cad_execution_status` 查询。同一请求仍在执行或结果未知时继续查询原请求。

[建模示例与图纸尺寸绑定](basic-examples.md)
