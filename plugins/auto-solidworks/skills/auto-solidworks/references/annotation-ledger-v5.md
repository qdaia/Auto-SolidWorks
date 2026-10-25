> 兼容文档：以下流程仅用于显式旧版草案或用户明确要求的专项复核。当前新任务默认只按SKILL.md中的识图、尺寸绑定、编译、建模四步，不执行本文附加步骤。

# Source annotation ledger, source-workflow-5

AI直接看原图；没有OCR或新的视觉模型。先完成全页形状分解和逐区域文字普查，再绑定参数。所有明示尺寸必须回指账本；旧 source-workflow-2/3/4 保留历史身份，新任务不得降级。

1. `annotation_ledger.source_sha256` 使用当前图纸哈希。每条尺寸或孔标注保留唯一 `id`、`view_id`、归一化 `text_bounds`、完整 `source_literal`、`length_unit`、所属 `feature_id`、`operation_id`、复核状态及具体证据。相同数字在不同位置是不同条目。
2. 对库存中每个 overview/detail 区域填 `sweeps`，`pass=1` 的 `annotation_ids` 为该区域所见条目。后续绑定前保留第一遍记录。完成绑定后逆向逐区查看，填写 `pass=2`。无尺寸区域也明确空列表与发现。交叉区域列出相交条目。遗漏条目被第二遍发现时更新账本与受影响第一遍记录，保留实际增补说明；两遍记录不等于独立识别证明。
3. `cad_parse_source_callout` 对AI抄录文字执行有限语法拆分，返回键、角色、数值/单位、原文UTF-16起点/长度与未知尾部。不要自行改变数值以满足解析。`complete=false` 不允许直接编译当前标注；复查图纸或补充支持，不能只标记已审。
4. 数值分项用 `components[{component_key,dimension_id}]` 唯一对应尺寸事实；事实填 `source_annotation_id`、`source_component_key`，原文、文本范围、视图、特征、量纲必须一致。孔径、孔深、沉孔孔径/深度、锪孔孔径/角度、螺纹公称径/深度分别绑定真实参数槽。同值不同义不能共用事实。
5. 公差、配合、螺纹等级、通孔、锪平和孔类型符号逐项用 `retained_literal` 与 `evidence` 保留。保留不等于制造/GD&T验收。螺距没有独立可执行孔标量时保留原文并对照实际 `thread_designation`，其仅为装饰螺纹标记；不认证实体牙型。PCD原文对照真实 `hole_centers` 的直径及等角分布，不创建不参与零件的辅助尺寸。
6. 复杂孔的公共引线放在标注 `leader_trace` 和 `leader_target_anchor_id`；每个径向分项仍指向自己的源圆圈锚点，不能把内孔与沉孔当作同一物理圆。孔深/锪孔角绑定具体孔圈及实际孔轴（`leader_trace.model_direction`），保留入口面、孔底或锥面射线的基准解释；原图只有公共引线时不伪造二维界线。普通线性/角度尺寸继续原有端点、界线、角弧门禁。
7. 阶段计划中尚未执行的标注可 `deferred=true`，但必须属于 `stage_scope.pending_feature_ids`，保留原文、位置和证据并纳入两遍区域普查。最终计划不允许Deferred。

支持的有限形式包括逗号/点小数、数量×、R/直径、显式mm/in/m、±及双向公差、配合、深度、THRU、counterbore、countersink、spotface、M公制螺纹及明确螺距/等级、EQUALLY SPACED ON直径PCD。未知左旋、多头、多级重复孔段、复杂标准注释等仍须待审。不得把有限语法覆盖称为全部标准支持。

`readiness.annotation_components` 返回完整解析记录，摘要最多12条，沿 `/readiness/annotation_components` 分页读完。源文字或哈希改变后重新核对。

此账本检查已登记标注的完整绑定和声明复核，不能发现AI两次均未看到的文字；墨迹/引线仍是几何支持，不能独立证明语义目标。A/B必须分开报告文字召回、语法覆盖、错误拦截、正确样本接受和原生验收。
