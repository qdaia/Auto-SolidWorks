# Auto SolidWorks 1025

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
本地版本：`1025`；修订：`source-four-stage-2`。默认仅识图、尺寸绑定、编译、建模四步；保留真实参数/单位/源图端点检查和建模保存，移除强制复核台账、分区探针、预览与独立交付审核。历史草案仍按原修订校验。不认证完整图纸/BREP/GD&T等价。详见[能力清单](skills/auto-solidworks/references/capability-manifest.json)。
<!-- Capability manifest SHA256: 203b2763562d8e401f8fccc85fec2c8f76c172b10604f28edb64105bb18c7cb1 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

本地插件，默认流程只有：**识图 → 尺寸绑定 → 编译 → 建模**。

读图准备原图和绑定证据；AI确定形状、尺寸、基准和端点；尺寸对应真实建模参数；编译通过后直接执行并保存原生零件，返回文件路径和简短状态。

默认不要求备选解释报告、候选核销、双遍扫描、形状分区/材料探针、独立验收表、预览图片、源投影比较、交付审核或阶段计时报告。建模内部仍检查重建、保存读回和实体有效性，避免把执行失败当成功。

新输入使用source-four-stage-2；显式旧版草案按原规则兼容。详细入口见[四步工作流](skills/auto-solidworks/SKILL.md)，批量尺寸可按需使用[绑定表](skills/auto-solidworks/references/binding-sheet.md)。
