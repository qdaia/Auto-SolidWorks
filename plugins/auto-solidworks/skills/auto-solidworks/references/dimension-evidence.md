# 识图与尺寸绑定：dimension-evidence-4

目标是提高首次完整读图和绑定的正确率。文字、端点、引线、跨视图关系由当前 AI 实际看原图判断；本地没有新增 OCR 或视觉模型。校验通过仅说明已提交证据结构及部分语义一致，不证明图纸上每个标注都被读对。

## 一次完整读图

1. 总览所有页面，定位主视、俯视、侧视、剖视、局部放大、单位、标题栏及技术要求。无标识且不冲突时采用第一角假设；实际符号、标签优先，冲突先复查。
2. 在建模前维护一份全尺寸清单。逐区扫描所有标注，记录原文、区域、R/直径符号、孔数、螺距、深度、角度、倒角、公差和配合。旋转、拥挤、断裂和相邻字形至少结合全图及局部放大复读一次。未读清写未知，不能从模型需要倒推读数。
3. 沿尺寸线、界线、引线追到真实对象。线性尺寸明确从哪条界线/中心线/面到哪里；角度明确两条射线。圆形标注明确内孔/外圆及轴线。禁止以文字最近、同值、像素比例代替绑定。
4. 跨视图核对整体与分段、孔径与外径、中心距与边距、通孔与盲孔、隐藏线和实体连通性。相同物理测量共用 measurement_id，不同对象即使同值也独立记录。
5. 提交前从原图反向逐个核销标注，复核所有非空区域和无文字候选；保留已有 omission_review、interpretation_reviews 及独立验收。可确定的接口问题集中修正，真实矛盾回查原图后集中询问。

## 新任务的绑定证据

cad_read_drawing 默认创建 require_dimension_evidence=true 的上下文。通过 cad_get_contract(contract="drawing_context") 读取完整合同。每个关键尺寸填写：

```json
{
  "binding_evidence": {
    "feature_id": "bore",
    "measurement_id": "bore-diameter",
    "view_id": "front",
    "quantity_kind": "Diameter",
    "text_bounds": {"left": 0.1, "top": 0.1, "right": 0.2, "bottom": 0.2},
    "target_bounds": {"left": 0.3, "top": 0.3, "right": 0.4, "bottom": 0.4},
    "datum": "孔轴线",
    "attachment_explanation": "引线终点位于内孔轮廓，侧视虚线支持通孔解释"
  }
}
```

区域坐标都是原始整页的 0..1 归一化坐标，裁剪图必须映射回原图。示例坐标只是字段示意，不能复制为实际证据。

quantity_kind 支持 Linear、Radius、Diameter、Angle、Count、ThreadPitch。Linear/Angle 额外要求 start_reference、end_reference，分别命名不同的界线、中心线、面或射线。quantity_kind 表示实际绑定的物理量；半径不能直接绑定直径字段，反之亦然。数量须为正整数且 Unitless。一个孔组标注如 4×Ø10，应分别保留孔数与孔径事实，同一文字区域可以支持不同物理量，不共用 measurement_id。

派生值设置 status=Derived，填写 derived_from_dimension_ids 和 derivation。例如半径由直径除以 2，则引用直径事实 ID；派生量使用自己的 measurement_id 和对应 QuantityKind。依赖不得缺失、重复、循环或依赖 Unknown/Assumed。新任务的算术派生必须提供可计算 expression；插件统一计算公式、检查量纲、依赖、循环和尺寸链。自然语言推导说明不替代公式，公式正确仍不能证明几何解释正确。原始标注事实和执行偏差继续分开记录，已有授权偏差合同保留。

纯几何基准（例如由源图对称中心定义零坐标）可用 `derivation_kind=Geometry`，同时保留 `status=Derived`、`derivation`、`binding_evidence`，并在 `observation_ids` 引用当前原图库存中的真实 Geometry 观测。这样无需虚构一个数值前提。库存哈希、来源身份和现有遗漏检查仍必须通过；不能用几何模式按像素比例生成长度。

旧上下文仍可读取；不要为绕过新任务检查把 require_dimension_evidence 改为 false。旧计划可能因新增字段影响绑定摘要而需要重新编译。

## 验证边界

自动检查覆盖对象/视图引用、区域边界、端点描述、量与单位、明确简单原文的数值、半径直径误绑、派生依赖和已声明同一测量的一致性。复杂公差、螺纹、基准体系和漏读的未声明标注仍需视觉核对；不能声称 100% 首次正确或完整制造图等价。没有训练模型，没有测得盲测识图准确率提升。

## 复合标注与圆对象一致性（26.10.22-local.1）

`4×Ø10 ±0.1` 分成数量 4（Count/Unitless）与标称直径 10（Diameter），两条事实可保留同一 source_literal 和 text_bounds，使用不同 measurement_id。支持明确的 R/直径符号、数量前缀（×/x/-）、小数、mm/in/m/角度单位、配合后缀及 ±、+/-、+a/-b 公差形式。检查标称值和显式单位，不能把 10.1 或 H7 的 7 当作直径；不自动应用公差或认证配合语义。

复杂孔深、复合沉孔、螺纹、上下排列极限尺寸、逗号小数和一般技术要求不在此有限文字语法内。必须实际看原图拆分各量与作用对象，保留原文和未确定项，不能把未解析等同通过识别。

同一圆/弧跨视图用相同 physical_entity_id；半径与直径即使 measurement_id 不同也核对 `d=2r`，先换算为毫米。内孔、外圆、沉孔、底孔是不同对象。几何锚点的 bounds 和尺寸 target_bounds 必须相交；不要扩大框到全页来绕过检查。相交只是必要条件，不是引线确实指向该圆的证明。授权执行偏差按原标称值进行来源一致性核对，原有偏差授权检查继续执行。

报错 DIMENSION_LITERAL_VALUE/UNIT/KIND 时回读原文；ANCHOR_CIRCULAR_CONFLICT 时核对物理对象、R/Ø 与单位；ANCHOR_TARGET_REGION 时回查引线落点及裁剪图到整页的坐标变换。不要改 ID、标称值或证据框以迁就报错。

## 覆盖与路径门禁

新工作流按 [source-review-v4.md](source-review-v4.md) 提供每条标注的自动覆盖与显式视觉复核。未知语法不再静默算作通过；关键明示线性/角度/R/直径必须有源笔画支持的实际附着路径。历史段落中的区域相交仅为基础必要条件。
