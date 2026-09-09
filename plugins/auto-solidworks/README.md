# Auto SolidWorks 0.5.4

本地 SolidWorks 参数化零件与装配建模插件。默认单位 mm，使用本机 SolidWorks，直接返回模型和指定导出文件。

## 建模功能

- 草图：矩形、圆、圆弧轮廓、槽、椭圆、样条、点、约束、驱动尺寸、偏移和修剪；任意基准面与局部坐标。
- 零件：拉伸/切除、旋转/旋转切除、孔组、圆角/倒角、抽壳、拔模、肋板、薄壁、放样/扫描及切除、镜像、阵列、多实体布尔/分割/移动复制。
- 钣金：基体、折弯截面、边线法兰、展开和恢复折弯。
- 焊件：本机型材库、配置读取、结构构件和端部裁剪。
- 曲面：平面、拉伸、放样、裁剪、缝合和加厚。
- 装配：零部件/配置、位置旋转、重合/同心/距离/角度/平行/垂直/锁定配合、干涉体积和保存后复核。
- 已有模型：读取特征、尺寸、配置与几何，按持久引用及几何条件选择实体，复制源文件后修改尺寸或压缩/恢复特征。
- 读图：PDF/图片、OCR 和符号尺寸候选、分区/旋转识别、视图解释与尺寸到建模字段的绑定检查。

## 工具

cad_get_capabilities → cad_read_drawing（有图时）→ cad_create_model_plan → cad_build_model。

辅助建模工具：cad_inspect_model、cad_list_weldment_profiles、cad_build_assembly、cad_executor_health、cad_export_drawing。

运行依赖：Windows、本机已安装并可启动的 SolidWorks、.NET 9。OCR 使用本机 Tesseract，PDF 栅格化使用本机 Poppler。OCR 配置可放在 `%LOCALAPPDATA%/AutoSolidWorks/dependencies/ocr.json`，字段为 executable 与 tessdata；环境变量 CAD_TESSERACT_PATH、CAD_TESSDATA_DIR、CAD_TESSERACT_LANG 可覆盖。CAD 执行和文件处理在本地；主体识图由当前对话模型完成，其联网需求取决于模型服务。OCR 仅作辅助。

## 使用范围

0.5.2 增加三项运行时能力：图纸关键特征/尺寸的覆盖与绑定检查；实际圆柱面、轴线位置/长度/数量、原生尺寸和包络的独立测量；保存并读回主体检查点后的前段复用与后段恢复。图纸规划提供 `drawing_context.features` 和 `verification`，失败时按返回的 `recovery` 修正完整草案后继续。详细字段与边界见 [来源要求、测量检查与恢复](skills/auto-solidworks/references/source-requirements-and-recovery.md)。仍无重复人工审批；未通过检查的输出不作为交付模型返回。

0.5.1 将复杂图纸任务中的分阶段建模、孔深与螺纹表达、失败特征修复和保存后检查整理为随包工作流程。执行器关闭自动捕捉创建有类型草图，自动隐藏构造几何并保存等轴视图；`cad_inspect_model` 新增 STEP/STP 隔离导入检查，恢复应用设置并将临时副本移至回收站。

建模成功表示原生特征已创建、重建、测量和保存。低分辨率 OCR 可能错读数字，须结合原图解释；不宣称自动证明模型与任意图纸等价。Tapped 孔使用底孔和原生装饰螺纹；曲面孔口等不支持标注的情况会明确失败。如交付允许名义螺纹表达，需要显式重新规划为 Simple 孔并保留规格及说明。未生成物理螺旋牙型。当前不完整解释任意 GD&T，也不包含 SolidWorks 所有高级特征选项。

安装包只含运行程序、建模技能和简明参数说明。源代码、开发回归样件和报告保留在独立开发目录。更新后在新任务中调用插件，以加载新版工具和技能。

## 复杂局部几何检查

支持来源指定的平面、圆柱面、圆锥面局部采样，核对裁剪后实际面的位置、材料外法线、直径及圆锥角度；可核对匹配曲面的总面积，以及到实体边界的最小距离。适用于半孔、相交孔、沉孔台阶、埋头孔与钻尖的指定局部要求。采样结果不认证完整拓扑、通盲连通性或螺纹语义。详见 `skills/auto-solidworks/references/local-geometry-verification.md`。

## 0.5.4 工程图导出与读取

cad_export_drawing 从已保存零件导出第一角法 A3 SLDDRW 与 PDF，包含四视图及原生参数表；重新打开检查引用，保留源模型哈希。修复遗漏尺寸、角度及阵列计数分类，并保留旋转 PDF 文字与 OCR 冲突状态。最多 44 个源参数，复杂标注可能需整理。详见[导出说明](skills/auto-solidworks/references/reference-drawing-export.md)。
