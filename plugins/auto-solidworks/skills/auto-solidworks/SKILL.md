---
name: auto-solidworks
description: 根据工程图或尺寸创建本地SolidWorks参数化零件与装配体；集中澄清关键尺寸，专注识图绑定与建模，保留原生保存重开验收。
---

# Auto SolidWorks

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Local version: `26.10.17`; revision: `review-iterate-deliver-1`. Unit-checked source expressions, geometry anchors, explicit stage scope, serial background execution, verified checkpoint resume and fixed-frame source projection differences. Controlled plate validation does not certify general first-attempt accuracy or speedup. Complete source coverage and final native reopen gates remain. See [capability manifest](references/capability-manifest.json).
<!-- Capability manifest SHA256: ff92dc2459f941264add1388925e1b3f1de9bc01dd189a69d2939e761b91d3fb -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

完成用户已授权的本地建模，默认简体中文。新名称用中文机械术语，接口ID/枚举/原生参数路径及用户给定名称保持原值。默认mm、角度Degree、数量Unitless；返回最终文件和简短状态。

## 复核、修改迭代与最终交付

首轮模型作为待审候选，不能因为cad_build_model返回success/completed就自动作为最终交付。建模后实际对照源图、独立尺寸检查和必要视图；明确区分原生执行、几何检查、参数化与视觉复核状态。详见[迭代交付流程](references/iterative-delivery.md)。

发现有源图依据的偏差时，先回查源图与尺寸端点，再修正草案、重新编译并执行；保存后再次完成受影响的检查和视图复核。有原生失败、成功模型的视觉反证或未满足声明检查时，不以第一份成功文件结束任务。已通过且身份不变的证据可复用，不为迭代重复无关操作。不通过放宽检查、移动标称尺寸、忽略反证或改写失败回执来制造成功。

每次迭代保留失败回执、候选模型/视图、修正理由与计时。仅复用当前任务中来源、文件哈希及操作前缀一致且已验证的检查点；pending/outcome_unknown先查询request_id，不重放修改。关键尺寸/拓扑经复查仍无法确定时集中澄清；能力不支持或仍有未通过项时如实报告部分完成。几何通过但parametric_status=review_required时必须在交付中保留该限制。AI判断和迭代由Codex提供，插件不内置独立视觉推理器，也不保证任意图纸自动收敛。

## 连接与最短工作流

渐进建模修订 `progressive-source-1`：多特征图纸优先读[源尺寸表达式、几何锚点与阶段续建](references/progressive-source.md)。先完整复核全图投影、单位、拓扑及基准，已复核且依赖闭合的特征前缀可先编译，用 `cad_build_model(background=true)` 执行，同时继续分析后续特征。阶段必须显式登记待建候选、保留所有来源检查和成功检查点；`stage_completed` 不是最终交付。最后累计完整草案通过覆盖、保存重开和必要视图验收。使用尺寸引用/表达式减少重复赋值，不把原图未明确的值自动猜成事实。

当前识图修订 `ai-vision-only-1`：不加载或调用Tesseract/PaddleOCR等本地OCR，不要求安装OCR依赖。`cad_read_drawing`只准备原图、页面/区域、几何和来源库存；AI直接读取图像后填写文字与尺寸事实。没有OCR候选不表示图上没有标注，未审核的上下文不能启动建模。AI由当前聊天提供，插件不内置新的视觉模型。

尺寸绑定修订 `dimension-evidence-1`：新读图上下文默认要求 `require_dimension_evidence=true`。按返回的 `recognition_checklist` 先完整列出标注，再沿尺寸线/界线/引线记录对象、基准和端点；关键尺寸填写 `binding_evidence`，派生量明确前提，跨视图核对同一物理测量。字段、复核顺序和几何基准例外见[识图与尺寸绑定](references/dimension-evidence.md)。先回查原图解决冲突，再一次提交完整草案；不得把检查通过说成全图读对。

多步骤图片建模、尺寸反复绑定或局部修正，优先读[可组合执行与局部修正](references/composable-workflow.md)，使用本地 `scripts/composable-workflow.py`。已有读图回执直接 prepare；没有回执可 ingest 一次取得来源库存和待审包。以稳定步骤 ID 组合操作、来源和验收，尺寸用带单位引用复用；局部修改自动使依赖步骤待复核。审核后仍调用原 cad_create_model_plan → cad_build_model；不得绕过源绑定、参数化或最终验收。此适配层不自动解释图片，也不是全 MCP 强制门禁。简短一次性任务继续直接 draft/text。

文字法兰、孔板与孔族可先查[优化配方与参数化验收](references/optimization-workflow.md)，采用明确的机械参数。自动草图区分基准固定和可变孔心冻结；参数编辑要求需实际几何试算与恢复。精简预览/检查点必须声明覆盖理由，最终验收保留。

直接使用cad_*。工具未暴露或用户要求coding-tools时，先读[连接桥](../coding-tools-auto-solidworks-bridge/SKILL.md)。默认compact；需要专用投影/剖切/孔族验收时建立full会话，不漏检。health可能启动SolidWorks，仅排查连接故障时用；用户禁止原生操作时只编译/dry-run。

1. 查capabilities摘要，确认 source_requirements.ocr=disabled 与 recognition_workflow=ai-vision-only-1。旧聊天连接若仍返回OCR辅助模式，使用已安装 scripts/composable-workflow.py ingest 启动新运行时准备图纸，不调用旧连接的读图入口；其余建模合同照常。按任务读取能力与合同；同一合同不重复取。工程图遵循[集中澄清与必要审核](references/batch-clarification-workflow.md)。文字简单模板可直接编译；复杂几何用typed draft。
2. cad_read_drawing默认生成待审drawing_context_input_id和worklist_artifact_id。必须由AI直接查看所有源页、原图及非空细节；本地OCR流程已取消，文字识别、尺寸端点判断、视图理解与尺寸绑定全程交给AI。分页读取原始区域与相关候选，候选覆盖完整但不自动当噪声或Reviewed。填写合同前按需查[遗漏复查](references/drawing-omission-review.md)的当前字段要求。
3. 一次整理特征/尺寸/基准表，跨视图解释先于操作数值；记录定位支持、备选解释和反证。源标注明确直接绑定，几何可推导值记录依据。首次全图核对后，当前真正阻塞尺寸或拓扑的问题去重集中问；已有答案持续复用，无阻塞直接继续。未知关键值未回答不执行依赖它的操作。新反证才补问，不把接口错误交给用户。无标记且无冲突三视图用第一角：左上主视、左下俯视、右上左视；源标签、符号和用户修正优先。
4. 来源库存、所有关键字段与独立验收完整绑定。require_complete_bindings=true、require_interpretation_review=true；已选解释有反证必须重推，不猜尺寸/库存、不从生成模型回填来源预期。写上下文时查[来源解释](references/report-issue-hardening.md)和[来源验收](references/source-requirements-and-recovery.md)相关合同。待审上下文增量填入，几何草稿另存；simple一次性任务可直接draft避免存储调用。每批1..200项JSON Patch同时更新操作、源绑定与检查。candidate_ids只批量合并已检查且判定依据相同的候选。
5. cad_create_model_plan内部检查来源、尺寸、遗漏和操作。失败时readiness聚合来源缺口/修复项；先复查原图，再把仍需用户决定的事项集中询问。摘要截断按next_offset读完/readiness/source_gaps、/readiness/error_groups或完整diagnostics；一次修复当前全部可修项再编译。编译通过直接用plan_id建模，不额外审批、注册、health、读取全IR或无必要dry-run。原生执行前核对孔/切除/旋转方向、引用范围、零厚度与所有未注圆角；驱动尺寸与约束明确，圆自动直径不再叠加手工圆径。非等距倒角不得猜原生相邻面次序。未经授权不偏移标称尺寸；真实授权偏差保留原标称、执行值、理由、授权与推导。
6. 最终文件保存重开、重建、实体有效性和声明的独立尺寸/几何检查必须通过。默认采用[必要收尾验收](references/essential-final-review.md)：成功回执一次提取必要摘要，等轴测与源图对应的必要视图合成一张图实际复查。完整回执保留在本地，不逐页读回所有成功记录；摘要缺失、失败或截断时才取相关完整证据。同一文件/配置/源修订/检查均未变且回执完整时复用，不重复inspect。孔数量/轴向、通盲及必要局部墙/法向在首次计划集中声明；只有新的源疑点、证据缺失或身份变化才追加检查，正常完成后不另加密集采样、重做库存或编辑试算。有限点/包围盒不证明整件等价。只导出任务要求的文件；STEP另验几何，工程图重开并看全PDF页，BMP写出不证明没裁边。

## 收尾时间控制

常规单零件收尾以30至60秒为目标（含必要摘要读取与实际视觉复查，非保证值）；不得为凑时间将未通过项标为通过。一次摘要、一次合成图、一次结论后交付；没有新问题不再补充调用。超过60秒仍需要检查时，先定位具体阻塞项，只检查该项。原生保存重开或必要检查尚在执行时等待其回执，不重放修改。用户要求的完整审图、复杂剖切、装配或导出验收按任务范围执行，不套用单零件时间目标。

## 输出与失败恢复

用户目录优先，否则Documents/AutoSolidWorks/Models下新建描述性目录。绝对路径、默认不覆盖。恢复目录和source_revision_id属于当前任务；仅复用成功验证、哈希/来源/操作前缀一致的checkpoint。失败局部修正并保留未通过项，pending/outcome_unknown先按request_id查持久回执，不能重放修改。不能交付失败输出或把未验证范围说成通过。

## 按实际特征查参考

- [几何操作](references/modeling-operations.md)、[输入复用](references/input-reuse.md)、[多视图执行](references/drawing-modeling-playbook.md)：仅取本任务需要的字段与步骤。
- [局部几何](references/local-geometry-verification.md)：相交/裁剪孔、盲孔、螺纹、球面等；TrimmedAxisAndDiameter不代表整壁或通孔，重复孔绑定真实hole_center_count与expected_count。
- [装配](references/drawing-and-assembly.md)、[可靠性](references/engineering-reliability.md)：装配/焊件/已有模型副本编辑；已有模型先inspect，保留干涉与约束要求。
- [工程图导出](references/reference-drawing-export.md)：从保存模型导出；参数表不是完整制造定义。
- [复杂建模](references/complex-modeling.md)、[高级特征](references/advanced-modeling.md)、[曲面](references/surface-modeling.md)、[Gordon](references/gordon-surface.md)：只在对应特征使用；历史证据不能认证本版或任意G2。
- [完整专题](references/detailed-workflow.md)、[截止/整件库存](references/root-hardening.md)：其他细节与限制。

两次内部审核汇总减少重复复盘，工具中的必要检查继续执行。独立全过程对照从各自首次看图计时，不共用识图答案；用户等待单列且保留总墙钟。字符或调用减少不等于实测token或原生提速。
