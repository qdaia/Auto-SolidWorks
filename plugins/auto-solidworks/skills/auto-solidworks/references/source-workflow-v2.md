> 兼容文档：以下流程仅用于显式旧版草案或用户明确要求的专项复核。当前新任务默认只按SKILL.md中的识图、尺寸绑定、编译、建模四步，不执行本文附加步骤。

# 第二轮识图、尺寸绑定与渐进建模

新MCP图纸输入现采用 `source-workflow-3`：先完成[逐视图形状分解](shape-decomposition.md)，提交轮廓、材料/空缺、操作对应及必要的轴向周向分层，再执行本文的尺寸绑定和渐进建模流程。旧 `source-workflow-2` 与已编译历史计划不自动改写。这些门禁能提前拦截已知错误，不能保证视觉理解或任意图纸一次成功。全部项目默认本地。

1. 原图只准备一次，先追踪轮廓并完成形状分解，再完整看尺寸线端点和跨视图关系。`drawing_context` 的原四个 require_* 及 `require_shape_decomposition` 必须为 true。几何锚点引用真实观察，具体端点不能用通用“起始界线/终止界线”占位。算术派生填写 expression；几何推导记录证据与反证。AI仍负责识图，插件不捆绑新视觉模型。
2. 直接调用 `cad_create_model_plan(draft=...)` 或 `cad_store_model_input` 均采用相同策略。已明确登记 operation_id/parameter_path 和 verification.bindings 的相同数值会变成带单位引用。不会根据相同数值猜绑定，也不会覆盖矛盾。输入修订后源表达式和消费者重算，原始修订保留。摘要 source_workflow 显示采用数量、锚点、阶段标记及未解决消费者；存储成功不代表校验通过。
3. 图纸中的 Blind/MidPlane 拉伸提供 `placement_intent`：AxisDirection、DatumPointMm、MinimumMm、MaximumMm、ToleranceMm、SourceExplanation、ObservationIds（JSON使用snake_case）。例如源中面 z=0、厚8，对应沿 +Z 的 -4..4 mm；这是独立来源期望，不能照抄当前拉伸结果。检查方向、offset、MidPlane后再执行。已知包络分离会阻止合并，棱/点接触提示零厚度风险；包络相交并不证明真实连接。
4. 编译一次聚合来源、轮廓、自动尺寸限制、验收规格、输出和恢复诊断。完整列表保存在回执中；看完当前全部问题再集中修复，不将接口格式问题交给用户。
5. 圆头耳板用 ProfileSketch.driven_profile=`{kind:RoundEar,radius_mm:R,web_height_mm:H}`，局部原点为底边中点，圆心为(0,H)。HalfAnnulus/QuarterAnnulus提供 radius_mm/inner_radius_mm，圆心为局部原点，分别位于上半平面/第一象限。用 frame 表达实际方向，不从图形名字推方向。配方不接受额外原始曲线/尺寸/约束；展开为可编辑且必须全约束的原生草图。孔的原生内部草图默认有圆径、孔心定位驱动。尺寸数值的单位仍为mm。
6. 全图拓扑/基准确认后，显式声明已审阶段和待建特征，`background=true`运行当前前缀，同时分析后续部分。原生任务串行，pending/outcome_unknown只查询同一request_id。recovery.directory使用当前任务专属目录；新源输入默认auto_select_checkpoint=true，选择实际存在、已重开、哈希及完整前缀兼容的最长快照。显式resume_manifest_path优先，错误不会被悄悄替换；编译回执保留候选拒绝原因，原生入口仍复核执行器/SolidWorks身份。
7. 源局部投影通过 source_projection 合同声明固定毫米坐标、原图区域、覆盖的feature ID和可选 region_mm。局部范围比较包络与区间相交的完整图元；圆弧使用整圆包络，可能多包含图元，不裁剪/缩放/对齐来凑通过。每个参与图元都需要来源位置。Missing/Extra/位置偏差用于修正相应特征后缀，保留失败回执。
8. `cad_review_model_delivery(planId,executionArtifactId,projections,visualReviews)`复用保存重开证据并批量投影。visualReviews必须来自实际看图，登记原生文件哈希、预览路径与哈希、视图和观察。预览必须属于同一次原生回执。摘要分别给出 native、declared_geometry、parametric、source_projection、visual、delivery 状态。未提供或未通过的项为 review_required；中间阶段最多 stage_accepted；最终仅 declared_scope_accepted，始终不等于完整制造图/BREP/GD&T等价。

受控配方、源夹具阶段续建与原图实测失败输入重放分别验收。阶段原生耗时不等于从首次识图到最终交付的墙钟时间；后台分析重叠区间不能重复相加。新版本安装后用新MCP会话核对版本、工作流修订及运行库哈希。
