---
name: auto-solidworks
description: 本地 SolidWorks 建模，仅按识图、尺寸绑定、编译、建模四步执行。
---

# Auto SolidWorks

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Local version: `1025`; revision: `source-four-stage-2`. Four default stages: recognition, dimension binding, compile, model. Retains real parameter/unit/source endpoint checks and native save. Removes mandatory review ledgers, material probes, previews and separate delivery review. Explicit legacy revisions retain their original rules. No full drawing/BREP/GDT equivalence claim. See [capability manifest](references/capability-manifest.json).
<!-- Capability manifest SHA256: 203b2763562d8e401f8fccc85fec2c8f76c172b10604f28edb64105bb18c7cb1 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

默认仅四步：**识图 → 尺寸绑定 → 编译 → 建模**。用户当前指令优先。新输入采用 `source-four-stage-2`；已有显式旧修订不自动迁移。默认简体中文、毫米、角度Degree、数量Unitless。

1. **识图**：调用 `cad_read_drawing` 准备原图、原像素网格工作区与绑定用证据；可用bindingCrops同时请求局部图，实际查看原图和需要放大的细节，确定视图、单位、实体/孔槽、方向和基准。无标识且无冲突时默认第一角；图示标签和用户修正优先。不调用OCR，不按像素比例猜尺寸。当前接口与合同已知时不再查询能力或完整合同。直接使用返回的authoring_guide和binding_workspace。需查字段优先小合同sketch_frame/primitive/dimension_fact/placement_intent，或section指定JSON Pointer片段；不重复读取整个draft。
2. **尺寸绑定**：填写视图、源特征、尺寸原文、真实 `operation_id/parameter_path`、基准及端点/引线证据。数值用源尺寸引用，派生值用表达式。角度经表达式驱动草图frame.x_direction时，须填写model_orientation：已复核的主视图front_right/front_up单位正交基，以及每个受驱动草图的dimension_id/operation_id方向绑定。use_end_ray=true表示原图角度终射线沿草图正X轴，false表示始射线；不能为了通过检查更改源射线。主视图+X向右/+Y向上时，俯视图向上为-Z；第一角/第三角只影响视图排布，不改变此基向量关系。详见[方向绑定](references/model-orientation.md)。斜线、引线或轮廓坐标难定位时，可用cad_find_source_geometry(workspaceId,frameId)提议直线坐标；查看原图与编号叠图，确定真实对象和端点后再填绑定。候选可能包含中心线、剖面线或字形，不自动作为已审核事实。复合孔和倒角逐分项绑定正确的参数槽；不得用同值代替对象归属。图纸Blind/MidPlane拉伸仍须placement_intent，独立填写源图厚度轴、基准、起止区间和已登记的尺寸观察ID，不能从当前拉伸参数回填。可直接写完整draft；批量装配按需使用 `cad_assemble_source_bindings`，不把工具调用当额外流程。
3. **编译**：调用 `cad_create_model_plan`，可传识图返回的workspaceId复用坐标工作区。编译失败自动返回binding_review，含原图局部、叠线图及裁剪偏移；局部像素回原页归一化用(left+x)/page_width、(top+y)/page_height。成功直接取 `plan_id`；失败只修正当前诊断需要的图纸解释、绑定或建模参数，再编译。只有读图后仍缺少必要尺寸/拓扑时才集中询问用户，不为接口问题反复询问。
4. **建模**：调用 `cad_build_model(planId=...)`，等待真实完成并返回原生文件路径和简短结果。长任务可用background，pending/outcome_unknown只查询同一request_id，不重放执行。建模内部保留重建、保存、原生文件读回和实体有效性检查；不要另外再调用一套审核流程。

**已取消默认步骤**：独立备选解释报告、候选逐项核销、双遍标注扫描、形状分区表和材料探针、独立验收表、预览合成图、源投影比较、建模后独立交付审核、阶段检查点规划、计时报告。不要默认补做或用其它名称加回来。用户明确要求的导出、审查或计时才在对应任务中执行。

保留必要的尺寸归属、单位、编译完整性和原生失败检查。源事实仍由AI读图得到；绑定通过与建模成功不代表全图遗漏审查或BREP/GD&T等价。旧版要求的检查仍只用于显式旧版草案。

按需查字段：[建模操作](references/modeling-operations.md)、[批量绑定](references/binding-sheet.md)。未遇到对应问题不加载其它流程文档。新文件默认保存到Documents/AutoSolidWorks/Models的任务目录，使用绝对路径且不覆盖已有文件。用户给定名称与接口枚举保持原值。
