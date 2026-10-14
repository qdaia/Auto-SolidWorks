# 参数化优化合同 26.10.13-opt.1

本版本沿用 C# / PIA / MCP，所有默认任务仅本地运行。当前原生验收限定包内证据列出的任务族、SolidWorks修订号及运行库哈希；其他能力保留各自历史证据范围。

## 机械配方

cad_create_model_plan 的 draft 支持 recipe；同时声明 source_text 和 operations:[]。不能与手写操作、图纸上下文、已有模型、设计方程或手写验收混用。配方展开为普通IR，并从明确输入计算独立验收；需要复杂修改时使用展开后的草案并保留独立来源要求。

- Flange：outer_diameter_mm、inner_diameter_mm、thickness_mm、hole_diameter_mm、pitch_circle_diameter_mm、hole_count (2..200)、start_angle_degrees (0..360，不含360)，全部必填。
- HolePlate：width_mm、height_mm、thickness_mm、hole_diameter_mm、hole_centers:[{xmm,ymm}]，1..200孔，全部定位明确。
- HoleFamily：同孔板基础字段，另声明 hole_kind=Through/Blind/Counterbore；盲孔需要 depth_mm；沉孔需要 counterbore_diameter_mm 和 counterbore_depth_mm。盲/沉孔深度严格小于板厚。

零净距、相切、孔口重叠和重复中心被拒绝，不自动偏移标称尺寸。不消费的配方参数被拒绝。

文字配方支持明确的中文法兰字段，例如：

法兰 外径120 中心孔60 厚度12 安装孔径9 孔数6 分度圆90 起始角0

可使用逗号、空格及 mm；未声明单位按mm。缺少孔数、分度圆或起始角集中返回 RECIPE_REQUIREMENT；额外要求不静默忽略。其他自由文本仍采用已有有限模板。

圆周法兰保留原生阵列数量参数 孔数@孔圆周阵列。轴向起始角时保留 分度圆半径@安装孔定位，其45mm表示PCD90；改为46mm表示PCD92。非轴向起始角以种子孔的X/Y位置驱动，配方重编译可修改任意起始角；没有声称所有起始角都有独立的原生PCD/角度参数。

## 草图与可编辑性

自动圆尺寸建立可驱动的圆心位置，非零坐标使用定位X_i / 定位Y_i；轴上点使用点专用水平/垂直关系。原点参考和局部基准可锚定，但不能固定可变孔心。明确的原点坐标作为参考角色保留，不能把“任何Fix都失败”当成质量规则。

Origin 是可用的 SketchEntityPart，表示当前草图的声明基准。原生方向提示中的标准平面坐标与草图定位尺寸的局部坐标分别读取；Right 的局部X对应深度，不能当作模型全局X。

自动圆/矩形或 require_editable=true 的草图在最终文件重开后必须有完整关系证据、驱动尺寸、被预期特征消费，且没有冻结的可变设计实体。辅助基准轴的构造草图标为reference_only，不将它误当成建模孔位。

cad_inspect_model 支持 requireEditableSketches=true，输出 geometry_status / parametric_status / visual_status 和 features[].sketch_quality。缺证据保留unverifiable；几何通过不会覆盖声明的参数化失败。

## 编辑试算

editabilityProbes 的每项可带 trial_verification 和 restore_verification，采用ModelVerificationSpec。它们核对实际几何变化和恢复，而非只读参数数字。结果包括试算/恢复几何状态及合同指纹。未声明试算几何合同的探针仍只证明参数读回、原生重建、恢复和文件保持，不认证孔位变化。

试算在隔离原生副本进行；恢复后重建、实体有效性、几何与源字节哈希分别检查。任何探针失败会反映到 success 和 parametric_status。原生 outcome_unknown 仍必须按request_id查询持久回执，不能自动重放修改。

## 方向、深度与策略

标准Front/Top/Right与显式frame在编译回执显示方向。引用平面或面附着无法离线解析时明确报告 unresolved，需原生/源要求核对。Blind/MidPlane需要正深度；ThroughAll/UpToNext无需制造深度，原生未使用槽位规范化为0；UpToSurface还需要目标引用。

通用默认预览仍是 isometric/front/top/right/left，另支持 back。盲孔/沉孔配方自动补充孔口侧 back，明确缩减预览也不能去掉这张必要视图。draft.preview_views 可声明视图子集，并提供 preview_scope_reason；保留等轴测，图纸任务仍要求全部五张标准视图，剖视/来源视觉门禁不因此取消。固定尺寸BMP导出在原生视口适配后增加取景余量，本次实测模型边缘不再裁切；任意屏幕布局的无裁切仍需实际图片审查。文件写出状态不等于视觉审查完成。代理默认按[必要收尾验收](essential-final-review.md)一次查看覆盖源特征的必要视图拼图，避免重复逐张读取全部预览；原生生成五视图的合同继续保留。

recovery.checkpoint_mode 支持 Automatic/Explicit/EveryNFeatures；默认Automatic兼容原版。显式模式的空 after_operation_ids 表示不写中间检查点，feature_interval在每N特征模式为1..200。每个实际检查点和最终文件仍必须保存重开验证；省略中间点会减少失败恢复粒度。

AUTO_SOLIDWORKS_TRACE_DIRECTORY 启用现有诊断。trace包含request_id、operation_id、同一计划的进程内attempt、preview及既有阶段。performance_trace_path指向完整日志。span是包含子阶段的墙钟，不得相加当全过程。写入失败不改变模型验收。

## 当前证据

capability-manifest.json仍是静态合同唯一来源。current-native-evidence.json是当前任务的原生证据索引，能力查询核对运行库、模型与检查回执SHA-256。缺失、版本不匹配、文件改变或未通过的任务不标为当前原生通过。它不是任意SolidWorks版本、复杂识图、曲面、装配或仿真的认证。
