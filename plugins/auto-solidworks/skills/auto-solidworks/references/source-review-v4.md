> 兼容文档：以下流程仅用于显式旧版草案或用户明确要求的专项复核。当前新任务默认只按SKILL.md中的识图、尺寸绑定、编译、建模四步，不执行本文附加步骤。

# Source review workflow 4

新图纸默认 `source-workflow-4`，尺寸绑定 `dimension-evidence-4`。既有 workflow 2/3 保留原修订；不要降低新任务修订来规避未完成的源审查。读取 `cad_get_contract(drawing_context)` 获取准确字段。

## 标注覆盖

编译回执 `readiness.callout_coverage` 为每个源标注报告自动状态 `Verified`、`Partial`、`Unsupported`、`Conflict` 或 `NotApplicable`。摘要只显示前12条，完整结果通过返回句柄读取。Verified 只表示有限标称值/单位语法核对，不表示公差、配合、螺纹或制造语义已认证。

关键 Partial/Unsupported 必须填写 `callout_review`：`state=Reviewed`、与 fact 完全相同的 `source_literal`、具体 `selected_component`、`quantity_kind`、`value`、`unit`、实际看图的 `evidence`，并清空已解决的 `unresolved_tokens`。未解决项继续阻断。原文变更使旧审查失效；视觉复核完成也不会把自动覆盖改为 Verified。将复合标注拆成各量，仍须逐项入源库存，不能只复核其中一个量就忽略其余项。

## 引线与测量方向

关键明示 Linear/Angle/Radius/Diameter 的 `binding_evidence.trace` 包含原页归一化坐标 `path`，`geometry_anchors[].source_path` 描述真实源轮廓。必须用新 `cad_read_drawing` 原页库存的哈希绑定笔画掩码。掩码来自普通二值化，不是 OCR；它只检测路径有无像素支持，不会理解哪条相邻线才是正确对象。3像素接触容差、2像素邻域及60%路径笔画支持允许普通制图间隙；密集交叉线、粗糙图片仍须 AI 逐项看图。

- Leader：从标注区域开始，到目标圆/弧轮廓结束。半径和直径保留各自物理量。
- LinearDimension：path 从首箭头到尾箭头；start_extension/end_extension 从实体轮廓到对应箭头。明确 Horizontal、Vertical、Aligned 或 Projected；model_direction 为实际模型中测量方向。Aligned 需尺寸线与被测端点连线平行。
- AngularDimension：路径至少3点，angular_vertex 位于两条基准源射线；明确 MinorAngle、ReflexAngle 或 DirectedAngle；model_direction 为有向模型平面法向。DirectedAngle 还需 clockwise_in_page（页面Y向下）。圆弧半径/转向和类型一致性受检，不从像素换算模型毫米。

## 材料区间

源 shape_decomposition 的材料/空缺声明生成 `verification.material_regions`。每条要求保留 feature_id、region_id 和 source_decomposition_digest。编译、执行保持源要求一致；手工删除、更改期望或删去源分区来迁就错误模型都不允许。

AxialAngular 每个源层/扇区生成9条径向内部线段（轴向、角向各1/4、1/2、3/4位置）；径向两端内缩0.002mm。General/Prismatic 每区须提供至少3条不同的 `probe_segments`，每条有 id、start_mm、end_mm、source_explanation。各区最多4096条，全计划最多16384条，端点在所述区域内部。线段由原图/尺寸与基准独立定义，不能从执行结果提取。

原生检查重新取得当前零件所有实体体，在全模型包围盒之外发射射线，读取实际 BREP 进入/离开交点；对各体材料区间取并集，检查完整目标线段。Material 不得有超公差缺口，Void 不得有超公差材料。缺失交点、相切歧义、无实体、非有限数据均为 unverifiable，不能计作通过。检查随保存重开重复，详细回执包括源区域、失败线段和交点区间。

**覆盖界限：有限线段上的完整区间，不是整个区域体积或完整 BREP 等价。** 未采样轴向/周向位置的小缺陷可能漏过；不认证 GD&T、标准螺纹、公差极限或制造可行性。源分区本身仍由 AI 解释，解释和复核可能共享同一错误。
