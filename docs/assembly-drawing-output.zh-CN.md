# 装配体同步输出工程图与 PDF

本页保留 2026-09-25 的初版验收记录。2026-09-28 已新增装配体视图尺寸导入及保存重开验证，详见[工程图尺寸标注更新](drawing-dimensions.zh-CN.md)；下述不导入组件尺寸的描述属于初版历史行为。

验证日期：2026-09-25。仅本地更新，未提交或发布到 GitHub。

本地安装版本：`0.6.0+codex.20260925035317`。

## 行为

`cad_build_assembly` 成功时默认生成同目录同名的三个文件：

```text
assembly.SLDASM
assembly.SLDDRW
assembly.pdf
```

无须新增请求参数，也无须再次调用工程图工具。原有 STEP/STP/STL 可选导出保留。

工程图采用 A3、第一角法，包含主视、俯视、左视和等轴测视图，另有原生参数表页。装配体图的参数表仅包含装配体级参数；不向装配视图批量导入零件详图尺寸。`cad_export_drawing` 同时支持已有 SLDPRT 和 SLDASM。

装配体保存并重新打开验证后，执行工程图和 PDF 导出，再重新打开工程图核查图纸列表、投影、页面大小、四视图及引用模型路径。返回结果新增 `drawing` 字段，包括工程图/PDF 路径、视图、参数覆盖及 `reopened`。

已有同名工程图/PDF 文件或目录会在装配体创建前被拒绝，即使 `overwrite_allowed=true`。工程图失败时，已保存并验证的装配体保留，顶层 `success=false`，并返回 `drawing` 的失败阶段和目标路径；不能把部分成功解释为三文件完整交付。失败文件保留供诊断，可指定新的工程图路径重试。

## 验证

- 最终源代码编译通过：构建日志 (`../artifacts/assembly-drawing-build-02.log`)。
- 204 项核心回归通过：日志 (`../artifacts/assembly-drawing-core.log`)。
- 包内 MCP/实际 SolidWorks 回归 14/14：报告 (`../artifacts/assembly-drawing-20260925-02/report.json`)。
- 安装缓存 MCP/实际 SolidWorks 回归 14/14：报告 (`../artifacts/assembly-drawing-installed-20260925/report.json`)。覆盖装配体、嵌套装配体、同名文件/目录预检、组件哈希保护、零件工程图、已有装配体独立导出和错误模板。
- 安装缓存接口冒烟 9/9：报告 (`../artifacts/assembly-drawing-installed-smoke/report.json`)。
- 77 个安装源/缓存文件逐项哈希一致：安装记录 (`../artifacts/assembly-drawing-install-20260925-115317/installation.json`)。包内与安装缓存关键运行文件也一致：哈希 (`../artifacts/assembly-drawing-install-20260925-115317/package-cache-hashes.json`)。
- 四份安装版 PDF 均为两页 A3，含四视图标签和实际矢量几何：PDF 检查 (`../artifacts/assembly-drawing-installed-20260925/pdf-validation.json`)。已目视检查样例装配图、嵌套装配图、零件图和参数表，并再次查看安装版装配图预览。

首轮原生测试暴露出 SolidWorks 持有组件写句柄时普通文件哈希读取失败；最终版本改用允许共享读写句柄的只读流计算哈希，并保留前后比对。首轮失败记录保留于 `artifacts/assembly-drawing-20260925-01`，其中确认了装配体成功、工程图失败时整体不会误报成功。另修正了旧工程图导出器调用通用检查器时因源文档已打开而被拒绝的问题，改为无重建读取原生参数。

## 样例和生效方式

- SLDASM (`../artifacts/assembly-drawing-installed-20260925/assembly_bundle.SLDASM`)
- SLDDRW (`../artifacts/assembly-drawing-installed-20260925/assembly_bundle.SLDDRW`)
- PDF (`../artifacts/assembly-drawing-installed-20260925/assembly_bundle.pdf`)
- 预览 (`../artifacts/assembly-drawing-installed-20260925/assembly-preview.png`)

装配体依赖同目录 `block.SLDPRT`，移动样例时应保留引用文件。更新前的本地插件源完整备份保存在 `artifacts/assembly-drawing-install-20260925-115317/previous-plugin-source`。旧缓存保留。新建 Codex 任务加载新版插件。

## 当前范围

本次实现装配体模型派生工程图/PDF的同步输出，不生成各零件详图、BOM、序号球、剖视/爆炸视图或未提供的公差。继承原导出器的 A3、第一角法、最多 44 个原生参数边界；不声称输出为完整制造定义。原始工程图到三维模型的完全等价性不在本次验收范围。

按要求检索了 GitHub 和 Hugging Face。GitHub 的 [haunchen/solidworks-mcp](https://github.com/haunchen/solidworks-mcp/blob/main/README.md) 提供工程图、PDF、BOM 工具参考；[FastGenerator](https://github.com/saffist3r/FastGenerator) 提供 SLDDRW 批量格式导出参考。仅作公开能力参考，未引入或复现其代码。Hugging Face 检索未发现可直接用于本次原生装配体工程图导出的项目。本次实现复用本地已存在的 SolidWorks COM 导出器。


本页 artifacts/ 路径指向开发者本机保留的历史验收记录，不随公开源码或安装包分发。
