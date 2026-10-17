# 复核、修改迭代与交付 / Review, iterate and deliver

Revision: `review-iterate-deliver-1`; release: `26.10.17`.

## 中文

第一份模型是待审候选。原生执行返回成功，只证明执行及其声明检查的结果；源图解释、参数化和视觉复核分别保留自己的状态。Codex必须实际查看必要源视图与原生预览，不能以图片已写出代替看图。

发现偏差后，先回查原图与真实尺寸端点，再记录修正理由、修改草案、重新编译并执行。未变且身份一致的成功证据可复用；只追加与新反证相关的检查。失败回执、中间候选与阶段计时保留。可以复用当前任务中已验证且来源/文件/前缀一致的检查点；不能把阶段完成或检查点当作最终整件交付。

最终交付要求保存重开、重建、实体有效性、已声明检查与实际必要视图复核。仍有欠定义或参数化待审时明确报告；不能将geometry_status=passed改说成parametric_status=passed。来源反证不能通过删除检查、移动标称尺寸或改写回执消除。

关键尺寸或连接拓扑经复查仍未知时集中澄清；能力不足或修正未成功时如实报告部分结果。pending/outcome_unknown先查询request_id，不重放原生修改。该流程由Codex判断与编排，插件提供原生执行和证据机制；不保证通用自动收敛、全图/BREP/GD&T等价，也不证明通用成功率或提速。

## English

Treat the first model as a candidate for review. Native success reports execution and the declared checks; source interpretation, parametric quality and visual review retain their separate states. Codex must actually inspect the necessary source views and native previews. A written image file is not visual acceptance.

When a discrepancy appears, revisit the drawing and dimension endpoints, record the correction rationale, revise the draft, recompile and execute again. Reuse successful evidence only while its identity remains unchanged; add checks relevant to new counterevidence. Retain failed receipts, intermediate candidates and phase timing. Resume only from verified checkpoints belonging to the current task with matching source, file and operation-prefix identities. Stage completion is not final whole-part delivery.

Final delivery requires save/reopen, rebuild, body validity, declared checks and actual necessary-view review. Disclose underdefined sketches and review-required parametric status. Geometry passing does not imply parametric passing. Do not remove counterevidence by dropping checks, shifting nominal dimensions or rewriting receipts.

Batch clarification when critical dimensions or connection topology remain unknown after source review. Report partial results honestly if the capability is unavailable or corrections remain unsuccessful. Query request_id for pending/outcome_unknown instead of replaying native mutations. Codex supplies judgment and orchestration; the plugin supplies native execution and evidence. This is not a guarantee of general convergence, whole-drawing/BREP/GD&T equivalence, accuracy or speedup.
