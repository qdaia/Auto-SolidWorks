# 源尺寸绑定与渐进建模：progressive-source-1

先确认全图投影、单位、拓扑和基准，再把当前依赖闭合的特征编译并后台执行。原生操作始终串行；等待 SolidWorks 时可继续分析尚未建模的孔、槽、圆角等特征。不得把未读图页或相反证据登记为待建来绕过复核。

1. `cad_get_contract(drawing_context)` 获取阶段及端点合同；`dimension_expression` 获取尺寸表达式。`Derived` 事实可省略 value，由 `Reference/Constant/Add/Subtract/Multiply/Divide/Negate` 树计算。常数注明单位；仅允许长度、角度、无量纲的受支持组合。依赖不存在、循环、假定值、单位不合或独立 `dimension_equations` 不闭合会拒绝。
2. 草案数值使用 `{"$dimension":"width","unit":"Millimeter"}`，支持嵌套对象及数组。用 `cad_store_model_input` 的不可变修订与 JSON Patch 改源事实，服务器重算所有引用；禁止复制一套与原图脱离的数值。历史回执不可用于修改后的源。
3. `geometry_anchors` 记录原图中的 Point/Axis/Line/Plane/Circle/Arc/Contour、物理实体ID、特征、视图、区域以及真实几何 observation_ids/candidate_ids。线性/角度标注填 start_anchor_id/end_anchor_id，半径/直径填 target_anchor_id；按尺寸线和界线判断端点。相同数值不表示相同对象。跨视图同一 measurement_id 必须保持同一物理对象。几何锚点的语义仍需 AI 看图判断。
4. 阶段上下文要求完整读取源页/区域，四项 require_* 检查全部开启。`stage_scope` 包含32位GUID session_id、stage_id、revision、投影单位/拓扑基准/依赖闭合复核文字及 pending_feature_ids。当前 features/dimensions/interpretation_reviews 只包含已确认的累计前缀；未来候选明确标记 `Deferred` 和 deferred_feature_id，不能延后当前操作已消费的标注或锚点。
5. `scripts/composable-workflow.py readiness bundle.json` 给出可用前缀和阻塞原因；`emit-stage bundle.json stage-spec.json --native-output ... --output ...` 生成提交参数。stage-spec 必须明确提供 step_ids、经过复核的完整阶段 context、recovery；帮助程序不会自动把候选归为噪声或待建。就绪清单仍需服务器编译。
6. `recovery.enabled=true`，source_revision_id 为当前来源修订，after_operation_ids 包含阶段最后一操作。`cad_create_model_plan` 通过后调用 `cad_build_model(planId, background=true)`，保留 request_id，用 `cad_execution_status` 查询；pending/outcome_unknown 不重新提交。回执未完成前只做后续分析，不启动第二个原生任务。
7. 阶段必须实际保存重开并保存成功检查点，结果为 `stage_completed`、is_intermediate_stage=true。下一阶段提交累计操作前缀，将成功检查点 manifest 放入 recovery.resume_manifest_path。源、操作、文件哈希不兼容时停止复用。最终去除 stage_scope/Deferred，完整来源覆盖通过后才允许 `completed`；最终预览、参数化、几何和保存重开验收全部保留。
8. 需要原图几何对照时查 `source_projection` 合同，调用 `cad_compare_source_projection`。先从源图尺寸和基准建立独立线/圆/弧毫米坐标及 source_regions，声明 calibration_evidence，再捕获原生投影。返回 Missing/Extra/位置/尺寸/参数/镜像差异和对应原图区域。禁止由模型反填期望、自动缩放或自动对齐以使结果通过。该检查覆盖所声明投影几何，不认证完整 BREP、剖面或 GD&T。

机械配方与风险预检继续沿用 optimization-workflow.md：明确孔轴、切除方向、深度、引用范围、零厚度和小圆角顺序。本修订没有引入未经验证的新配方，也没有安装 OCR 或本地视觉模型。

计时分别记录读图、绑定/公式、编译、原生执行、重开与视觉验收；后台重叠时间按区间计算，不能相加后当墙钟，也不能拿最后一次 RPC 当完整建模时间。受控样例的首次通过与盲测识图成功率分别报告。
