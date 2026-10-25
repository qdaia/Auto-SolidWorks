# 四步流程的批量尺寸绑定

默认流程仅识图、尺寸绑定、编译、建模。直接draft即可；大量尺寸时可按需使用绑定工作区和sheet装配，不需要单独的复核阶段。

从cad_read_drawing的上下文保留source_evidence。草案填写name、source_text、operations、drawing_context。新上下文workflow_revision为source-four-stage-2，启用require_complete_bindings、require_dimension_evidence、require_geometry_anchors；无需interpretation_reviews、omission_review、shape_decomposition、sweeps或verification。

批量绑定时，cad_prepare_binding_workspace只准备裁剪坐标，cad_assemble_source_bindings接收draft与sheet。sheet.groups每组含frame_id/view_id/feature_id/datum/anchors/annotations。annotation保存source_literal、operation_id、text_bounds、state及evidence；dimensions每项明确id、component_key、parameter_path和attachment。checks可省略；提供时按原规则验证，不能伪造。坐标使用工作区原像素，数值来自已看图转录的原文。

简明字段可按需调用cad_get_binding_guide；完整类型按需查询binding_sheet/draft/drawing_context/geometry_anchor。同一合同不重复查询。真实线/圆锚点使用Geometry或ResidualInk的同页重叠候选，具体source_path仍需对应图纸。起止基准可属于同视图另一特征，目标锚点仍属于当前特征。

装配返回input_id后直接编译，不补第二遍扫描。复合标注的annotation_ledger只保存分项到参数的映射，sweeps为空，不做普查账本。简单单值尺寸可直接填写dimensions而不创建annotation_ledger。

显式旧source-workflow-5仍按旧版全检查处理；不要把旧草案改修订来掩盖错误。


### 按需追加细节图

整页缩小后看不清引线终点、相邻台阶或粗糙度对象时，调用 `cad_add_binding_crops(workspaceId,crops)`，一次追加 1–12 个局部图。`crops` 与准备工作区时格式相同：`[{"id":"leader-detail","page_number":1,"bounds":{"left":0.2,"top":0.3,"right":0.5,"bottom":0.6}}]`。边界是原页归一化坐标，不是上一幅裁剪的坐标。

返回新的 `workspace_id` 和新增 `frames`；已有图像和坐标框架复用，旧工作区不变。后续绑定使用新的工作区 ID。局部图保持源像素，不插值或自动读字。裁剪范围应同时包含标注文字、完整引线与目标轮廓；必要时批量加入对应视图。不要仅凭最近的尺寸文字推定表面归属，也不要混淆入口台阶和小孔等径段、截面半径和俯视路径半径。

这是识图/尺寸绑定阶段的可选辅助工具，不增加工作流阶段，也不自动完成语义审核。局部图生成加速不代表整体识图速度或准确率已提高。


### 圆与圆弧候选

`cad_find_source_circles(workspaceId,frameId,minimumRadiusPixels=8,maximumCircles=64)` 返回圆心、像素半径、墨迹支持比例与观测圆弧区间。使用 OpenCV EdgeDrawing/EDCircles 提议，并对源像素作圆拟合和支持校验。椭圆不作为圆返回。观测角度以图像右方为零度、顺时针增加，跨零度的区间可超过360度；区间表示像素容差内的支持，不证明真实设计端点或连续性。

用原图确认对象属于孔、凸台、轮廓还是文字，读取实际标注，再填写尺寸绑定。不能把像素半径当毫米，也不能用拟合半径替代原文尺寸。`source_radius_x`、`source_radius_y` 分别除以原页宽高，非正方形页面上数值不同。同心圆保留为不同候选，半圆不自动补成整圆。短弧、图外圆心、复杂交叠仍可能漏检或偏移。该工具属于尺寸绑定阶段的可选辅助，不增加默认工作流步骤。


`source-circles-2` 增加相交边缘链的短段候选，以及分离墨迹连通域的确定性圆假设，以辅助查找被中心线穿过的小圆和虚线圆。候选仍须通过源像素支持与径向误差检查；断续墨迹只输出观测到的各段，不能推断其一定是隐藏轮廓或孔。补充候选采用更严格的周向分布与误差门槛，仍可能漏检或误检。

返回 `fragment_circle_count`、`dash_circle_count` 为检查前的候选来源计数，不是已识别特征数。连通域超过 50,000、采样连通域超过 256、连通域圆假设超过 32 或短段窗口超过 6,000 等限额触发时会标记 `circles_truncated`。取消请求在原生图像调用之间、逐行扫描和候选循环中检查，不能中断正在执行的 OpenCV 原生调用。该补充检测增加计算量，不能据此宣称整体识图提速。


### 按候选编号绑定源几何

查看原图和几何叠图后，可选用 `cad_bind_source_geometry`，把 AI 已明确选中的 `cad_find_source_geometry` 或 `cad_find_source_circles` 候选直接装配成锚点，减少坐标和路径转录。该工具属于尺寸绑定步骤的辅助接口，不增加流程阶段。

参数为 `baseInputId`、`kind`（`draft` 或 `drawing_context`）、`workspaceId`、`proposalArtifactId`、`viewId`、`featureId`、`datum`、`selections`。每项选择包含 `proposal_id`、新锚点 `id`、`physical_entity_id` 和明确的 `evidence`；`state` 默认 `Unresolved`，必须由 AI 明确填写 `Reviewed` 才保留复核状态。视图和源特征必须已在基础输入声明。不能根据候选几何自动认定孔、轮廓或文字。

直线可显式指定 `kind` 为 `Line`、`Axis` 或 `Plane`。圆候选只保留原始观测区间；多个区间必须指定从 0 开始的 `arc_index`，不能把虚线或残缺弧闭合成圆。路径超过原合同 128 点上限时保留端点并减少一个内部采样点。工具检查工作区/候选/墨迹身份和路径的源像素支持，从同页 Geometry/ResidualInk 证据中选择至多 8 个覆盖至少 90% 路径采样点的候选，找不到则拒绝；此覆盖是几何证据检查，不证明语义归属。

返回新 `input_id`、完整文件与摘要、所选证据和支持率，基础输入保持不变。后续仍须绑定原始尺寸文字、引线、实际参数和完整编译。坐标录入减少不等于整体识图速度或准确率已提高。


### 跨视图圆柱与完整尺寸附着

`drawing_context.circular_projections` 可显式登记 `{id,circle_anchor_id,silhouette_anchor_ids:[left_id,right_id],axis_direction:{x,y,z},state:"Reviewed",evidence}`。圆必须是一个视图中闭合、源墨迹支持的真实圆；两条侧轮廓必须是同一特征在另一个视图中的真实平行直线、彼此分离且轴向区间重叠。三者保留原来的二维类型和路径。声明的实体对应与模型轴由 AI 看图确定，工具只校验声明和原图几何支持，不自动识别孔/凸台，不推断像素比例或毫米值。当前不支持用椭圆或断续圆替代该闭合圆证明。

声明对应后，简单直径/半径标注的引线可落在侧视图的实际轮廓线；目标尺寸按对应圆的物理身份检查跨视图数值。改动 measurement_id 不能隐藏同一圆的不同直径，或同一物理端点、同一模型测量方向的不同线性尺寸。同一实际参数若在不同视图绑定到了不同物理对象，即使数值相等也拒绝。复合孔的公共引线规则保持原状，不能直接把复杂孔深标注挂到普通侧轮廓。

对于空间很小、箭头朝外的线性尺寸，`trace.path` 保留完整可见直线，并成对提供 `start_arrow_point` / `end_arrow_point`（同一裁剪原像素坐标）。两点必须在该路径上并有源墨迹；起止界线分别连接真实轮廓和这两个箭头。不要把较长可见线段的端点当作尺寸值对应的箭头。

当界线接触的是实际平面的有限投影延伸，可用 `construction:{kind:"LineExtension",support_anchor_ids:[actual_line_id],evidence:...}` 显式构造 Line 或 Plane 锚点。支持线必须是同视图、同特征、已复核、真实墨迹支持的非构造 Line。构造路径必须共线，每端最多延伸 `max(12 px, 支持线长度的25%)`，不改变任何毫米尺寸。不要把未画出的延伸线冒充实际墨迹；没有证据的尺寸仍须报出未解决，不得降低墨迹支持门槛或把未知值当已知。


同一实际参数可有一个主尺寸及多个跨视图旁证。主尺寸不填 `corroborates_dimension_id`，每个旁证尺寸显式填写该主尺寸的 ID。主尺寸与旁证必须都是 Stated，来自不同视图，绑定同一 operation_id / parameter_path、同一物理测量与方向，且标称值一致（支持长度单位换算）。编译入口只用主尺寸驱动参数，所有旁证仍保留并接受校验；自引用、循环、不同实体、不同参数和数值冲突均拒绝。未显式登记关系的重复消费者仍报 SOURCE_CONSUMER_AMBIGUOUS，不能只凭数值相等自动选主尺寸。

### 拉伸图片中的角度与实际方向

当原图经过统一横纵拉伸，正常投影中的圆和角度弧在像素上会呈椭圆。确认两个不同视图、不同实体的轮廓本来是正视圆后，可声明：

```json
"page_metrics": [{
  "page_number": 1,
  "normal_circle_anchor_ids": ["front_hole_rim", "top_bore_rim"],
  "state": "Reviewed",
  "evidence": "已从原图及跨视图确认两处均为正视圆，页面发生统一横纵拉伸"
}]
```

每个锚点须有24至128点的完整闭合原图路径、独立物理身份、已复核状态和至少85%的真实笔画支持。两处比例相差不得超过4%；不支持透视、斜切或局部不一致变形。不能用待校验角度弧本身反推页面比例，也不能把斜视投影的真实椭圆当作校准圆。工具只为角度几何检查恢复同页比例，不修改图像、不生成毫米尺寸；角度原文仍是建模数值来源。恢复后的弧线范围须与原文相符，几何附着容差为2°。

源角度可用 `Sin` / `Cos` 一元表达式驱动真实草图方向：`{"kind":"Cos","arguments":[{"kind":"Reference","dimension_id":"ear_angle"}]}`。输入须为 `Degree`，输出须绑定为 `Unitless`；表达式事实为 `Derived` / `Arithmetic`。在实体轮廓与相应孔的 `frame.x_direction`、`frame.normal` 中共同使用这些派生值，检查法向、拉伸区间和跨视图对应。独立构造草图、仅有 `depends_on` 的执行顺序都不能冒充实体消费者。此关系在源草案重新编译时生效，不等同于 SolidWorks 内修改角度尺寸后的关联重建。
