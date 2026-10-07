# 工程图尺寸标注更新

2026-09-28，本地安装版本 `0.6.0+codex.20260928051124`。仅本地更新，未提交或发布。

装配体自动出图和单独的 `cad_export_drawing` 都调用 SolidWorks 原生 Model Items 导入模型尺寸，包含零件、装配体及组件的可导入原生尺寸。尺寸直接标注在工程图视图中，并随原生工程图导出到 PDF。

返回结果新增 `view_dimensions`（所属视图、尺寸名称、类型、单位和数值）及 `dimensions_reopened`。组件尺寸名称保留模型文件后缀，避免不同零件的同名参数混淆。保存后重新打开工程图，按多重集合核对尺寸名称、数值、类型、单位和视图，保留重复尺寸的数量。没有实际尺寸标注时明确失败，不把只有参数表或裸视图当成带尺寸工程图。

修正了重新打开工程图后的尺寸读取：部分工程图尺寸对象使用 `GetSystemValue2("")` 返回零，`GetSystemValue3` 返回空，而 `SystemValue` 可读取真实工程图尺寸值。最终实现读取后者并使用视图尺寸数组进行枚举。复测未降低保存重开检查要求，临时诊断日志已移除。

## 验证记录

- 最终构建日志 (`../artifacts/drawing-dimensions-build-final.log`)。
- 核心回归 (`../artifacts/drawing-dimensions-core-20260928.log`)：204 项通过。
- 完整原生流程 (`../artifacts/drawing-dimensions-20260928-full-final/report.json`)：15 项通过，覆盖零件、普通/嵌套装配体工程图、两份圆柱零件建模、装配体自动同步出图、尺寸及源文件哈希。
- 安装版工程图复测 (`../artifacts/drawing-dimensions-installed-20260928/report.json`)：保存重开后 10 条视图尺寸通过验证，包括直径 20/30 mm 和长度 10/15 mm。
- 安装版尺寸/PDF/哈希检查 (`../artifacts/drawing-dimensions-installed-20260928/verification.json`)：PDF 实际含尺寸文字；77 个安装文件核对一致，关键运行文件与通过测试的本地包一致。
- 安装版接口冒烟 (`../artifacts/drawing-dimensions-installed-smoke/report.json`)：9 项通过。
- 安装记录与旧版备份位置 (`../artifacts/assembly-drawing-install-20260928-131124/installation.json`)。

## 样例

- 自动输出的装配体 (`../artifacts/drawing-dimensions-20260928-full-final/dimensioned_assembly.SLDASM`)
- 自动输出的带尺寸 SLDDRW (`../artifacts/drawing-dimensions-20260928-full-final/dimensioned_assembly.SLDDRW`)
- 自动输出的带尺寸 PDF (`../artifacts/drawing-dimensions-20260928-full-final/dimensioned_assembly.pdf`)
- 安装版导出的 PDF (`../artifacts/drawing-dimensions-installed-20260928/existing.pdf`)

## 范围

Model Items 导入模型中已有的原生尺寸。模型没有存储的定位尺寸、制造公差或技术要求不会被凭空补上；仅有初始草图坐标不等于已有驱动尺寸。装配体参数表仍只列装配体级参数，组件的已放置尺寸记录于 `view_dimensions`。密集模型的尺寸布局仍须目视检查。通过这些样例不等于完整制造定义、任意图纸盲重建或长期稳定性认证。

参考 SolidWorks 官方[模型项目说明](https://help.solidworks.com/2024/English/SolidWorks/sldworks/HIDD_DVE_INSERT_MODEL_ITEMS.htm)及[原生导入示例](https://help.solidworks.com/2017/english/api/sldworksapi/insert_model_annotations_example_vb.htm)。未引入第三方实现代码。


本页 artifacts/ 路径指向开发者本机保留的历史验收记录，不随公开源码或安装包分发。
