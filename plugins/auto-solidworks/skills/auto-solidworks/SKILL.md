---
name: auto-solidworks
description: 根据工程图或尺寸创建本地SolidWorks参数化零件与装配体；集中澄清关键尺寸，专注识图绑定与建模，保留原生保存重开验收。
---

# Auto SolidWorks

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
Local optimization version: `26.10.13-opt.1`; revision: `parametric-recipes-evidence-20261013`. Editable hole positions, native sketch quality and geometry-changing probes, bounded mechanical recipes, direction/depth contracts and declared preview/checkpoint scopes. Native evidence is validated per task, SOLIDWORKS revision and runtime hash; untested tasks remain uncertified. Source and final save/reopen gates remain required. Read the [bundled capability manifest](references/capability-manifest.json) and cad_get_capabilities.
<!-- Capability manifest SHA256: b227e0bec254da978b871b777ea1a9ae03b37a4c73948deb082bcc0c0c7795d5 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

完成用户已授权的本地建模，默认简体中文。新名称用中文机械术语，接口ID/枚举/原生参数路径及用户给定名称保持原值。默认mm、角度Degree、数量Unitless；返回最终文件和简短状态。

## 连接与最短工作流

文字法兰、孔板与孔族可先查[优化配方与参数化验收](references/optimization-workflow.md)，采用明确的机械参数。自动草图区分基准固定和可变孔心冻结；参数编辑要求需实际几何试算与恢复。精简预览/检查点必须声明覆盖理由，最终验收保留。

直接使用cad_*。工具未暴露或用户要求coding-tools时，先读[连接桥](../coding-tools-auto-solidworks-bridge/SKILL.md)。默认compact；需要专用投影/剖切/孔族验收时建立full会话，不漏检。health可能启动SolidWorks，仅排查连接故障时用；用户禁止原生操作时只编译/dry-run。

1. 查capabilities摘要，按任务读取能力与合同；同一合同不重复取。工程图遵循[集中澄清与必要审核](references/batch-clarification-workflow.md)。文字简单模板可直接编译；复杂几何用typed draft。
2. cad_read_drawing默认生成待审drawing_context_input_id和worklist_artifact_id。必须查看所有源页、原图及非空细节；OCR只辅助。分页读取原始区域与相关候选，候选覆盖完整但不自动当噪声或Reviewed。填写合同前按需查[遗漏复查](references/drawing-omission-review.md)的当前字段要求。
3. 一次整理特征/尺寸/基准表，跨视图解释先于操作数值；记录定位支持、备选解释和反证。源标注明确直接绑定，几何可推导值记录依据。首次全图核对后，当前真正阻塞尺寸或拓扑的问题去重集中问；已有答案持续复用，无阻塞直接继续。未知关键值未回答不执行依赖它的操作。新反证才补问，不把OCR/接口错误交给用户。无标记且无冲突三视图用第一角：左上主视、左下俯视、右上左视；源标签、符号和用户修正优先。
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
