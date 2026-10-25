# 源笔画、隐含基准与倒角（26.10.22-local.5）

工作流仍为 source-workflow-5，source-trace-3 与 hole-callout-2 是可追溯的能力修订。没有关闭原绑定、遗漏、分解、参数化、保存重开门禁。仍由AI看图，不恢复OCR。

原图笔画库存使用 source-local-contrast-1：31像素局部窗口、k=0.12、至少12级局部对比，结合受约束的Otsu暗笔画通道。按白纸合成透明度，不膨胀、不连接白色间隙。输入源图和旧库存不改写；新读图生成带哈希和算法身份的新库存。60%路径支持率、3像素附着容差不变。均匀灰底、接近白色的噪声和未画出的轴不因此成为笔画。该方法不保证任意扫描背景上的笔画召回。

## 隐含基准

`DrawingGeometryAnchor.construction` 可声明 `kind`、恰好两个不同的 `support_anchor_ids` 和实际看图 `evidence`。支持锚点必须是同视图、同特征、已复核的真实直线，具有源路径、有效原图库存引用和笔画支持，且自身不能再声明 construction。构造锚点继续保留物理身份、区域、库存引用；`source_path` 此时表示几何推导位置，而非声称该处有墨迹。

- `ParallelMidline`：锚点 kind 为 Axis。两个有限平行孔壁中线，至少6像素重叠、可分辨间距、有限延伸；投影中轴不可偏离实际两壁。用于原图没有画中心线的孔位尺寸。
- `LineIntersection`：锚点 kind 为 Point。两条非近似平行边的有限延长交点；source_path 使用两个同一交点坐标。交点距离每条有限支持段不得超过 min(64px, 0.5×较短支持段长度+3px)。用于圆角/倒角前的理论尖角。

所有坐标仍是原页归一化坐标，计算在原页像素空间，不用图像比例猜毫米数值。尺寸线及实际界线仍须真实笔画支持，并连接相应构造位置和箭头端点。错误中轴、无关视图/特征、递归支持、白区伪轮廓或无限延伸保持阻断。构造证明有限二维几何关系，不证明AI选择的实体语义一定正确；跨视图身份与原生独立尺寸检查仍必需。

## C倒角与非孔数量

有限语法新增 `C0.8`、`4× C 0,8`，保留原文字跨度与显式单位、公差原文。C表示等距45度倒角，距离分项键为 `chamfer_distance`，只绑定 `feature.distance_mm`，数量单独绑定 `feature.selection_count`。原生 Chamfer 必须为 EqualDistance，或45度 DistanceAngle，或相等的 TwoDistances；未知/冲突尾部仍阻断。公差原文保留不等于制造语义认证。

倒角距离使用账本 `leader_trace` 与 `leader_target_anchor_id`，指向同特征真实 Line/Contour 倒角边；事实 target_anchor_id 也指向该边，target_bounds与它相交。无需伪造图纸没有画出的距离界线。普通线性尺寸不能借用此例外。

`3×R1.5` 的圆角数量也支持真实选边计数。带数量的 Chamfer/Fillet 只允许显式单边选择，tangent_propagation=false、all_matches=false，不允许重复选择或可变圆角替代。执行前另检查 SolidWorks 实际选中的不同边数，别名查询选中同一条边仍失败。feature.selection_count 是只读列表计数；相等的通用 count 不可替代它。圆角半径继续使用圆/弧锚点和真实引线。

本次已见的 part_0003 仅是失败复现及修复回归样本，不能重称独立盲测。阶段草案中的 Deferred 圆角/倒角不构成最终零件交付。
