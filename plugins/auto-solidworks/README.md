# Auto SolidWorks

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
本地版本：`1127`；修订：`source-four-stage-2`。默认仅识图、尺寸绑定、编译、建模四步；保留真实参数/单位/源图端点检查和建模保存，移除强制复核台账、分区探针、预览与独立交付审核。历史草案仍按原修订校验。不认证完整图纸/BREP/GD&T等价。详见[能力清单](skills/auto-solidworks/references/capability-manifest.json)。
<!-- Capability manifest SHA256: da5b1c411b6abd4f0eba30d326d137f777bbefb1e8dcdd33fd84db58d5010694 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

通过本机 SolidWorks 创建零件、检查模型、配合装配。输入支持尺寸、图片和 PDF。零件和装配体分别保存为 SLDPRT 和 SLDASM，可按需导出 STEP/STL。

[插件用法](skills/auto-solidworks/SKILL.md) · [基础示例](skills/auto-solidworks/basic-examples.md)
