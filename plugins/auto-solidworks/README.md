> 本地尺寸绑定修订：dimension-evidence-1。新读图任务要求对象、基准和端点证据，检查量义、原文、跨视图冲突及派生依赖。详见 [识图与尺寸绑定](skills/auto-solidworks/references/dimension-evidence.md)。盲测识图准确率与本轮原生建模尚未验证。

> 当前识图方式：ai-vision-only-1。本地OCR已停用，AI直接识别原图文字、尺寸与视图关系；无需配置或安装OCR。原图、来源绑定和最终建模验收仍必须完成。

# Auto SolidWorks 26.10.11mini

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
本地优化版本：`26.10.17`；修订：`review-iterate-deliver-1`。提供带单位的源尺寸表达式、几何端点绑定、显式阶段范围、串行后台执行与验证检查点续建、固定毫米坐标的源投影差异定位。受控孔板验证不代表任意图纸首次成功率或整体提速；完整来源覆盖及最终保存重开门禁保留。详见[随包能力清单](skills/auto-solidworks/references/capability-manifest.json)。
<!-- Capability manifest SHA256: ff92dc2459f941264add1388925e1b3f1de9bc01dd189a69d2939e761b91d3fb -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

本地使用的精简建模流程版。先完整识图与绑定尺寸，真实阻塞问题集中询问；编译回执聚合当前全部来源缺口与修复项。原生保存重开与几何验收继续保留。

安装前保留原版本，本地包不自动切换插件。详见[流程合同](skills/auto-solidworks/references/batch-clarification-workflow.md)。
