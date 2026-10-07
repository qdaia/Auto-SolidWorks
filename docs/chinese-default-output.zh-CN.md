# Auto SolidWorks 默认中文输出

本地安装版本：`0.6.0+zh.20261001.3`，默认语言为简体中文。

[详细行为与验证范围](../plugins/auto-solidworks/skills/auto-solidworks/references/chinese-default-output.md)

新生成的模型、43 种原生特征、辅助特征、装配零部件、工程图页名与视图、标题、参数表、单位和备注使用中文。工具说明、诊断和验证解释也已中文化。工程图默认字体为微软雅黑。

显式用户名称、旧文件、文件路径、源图纸原文和协议标识保持原值。旧英文模型可以导出中文工程图，原生参数引用仍保留在结构化结果中。

## 本地证据

- 最终源码离线回归报告 (`../artifacts/chinese-default-20261001/regressions-release-source/results.json`)
- 已安装版本原生验证报告 (`../artifacts/chinese-default-20261001/installed-native-release/report.json`)
- 中文零件工程图 PDF (`../artifacts/chinese-default-20261001/installed-native-release/中文工程图.pdf`)
- 中文装配体工程图 PDF (`../artifacts/chinese-default-20261001/installed-native-release/中文测试装配体.pdf`)

验证内容包含原生特征保存与读回、工程图保存重开、尺寸引用、中文参数表、PDF 中文文本与版面、英文源文件生成中文零部件名称，以及源文件哈希保留。旧复杂曲面能力证据仍按原版本独立保留。

## 使用新版本

重新加载插件或新建聊天，再调用 Auto SolidWorks。已有聊天可能仍使用启动时加载的旧版本；可以通过 `cad_get_capabilities` 的 `server_version` 和 `default_language` 确认版本及默认语言。

本轮修改与安装均限于本地，保留源码和已安装插件的备份，不包含远程提交或发布。


本页 artifacts/ 路径指向开发者本机保留的历史验收记录，不随公开源码或安装包分发。
