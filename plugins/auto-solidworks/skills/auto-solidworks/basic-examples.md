# 基础示例

## 尺寸建模

调用 `cad_create_model_plan`，传 `text: "80 x 50 x 10 mm plate"` 和最终 SLDPRT 的绝对路径。收到 `plan_id` 后用 `cad_build_model(planId=...)` 保存零件。编译复杂零件时，用 `cad_get_contract(contract="draft", section="/properties/operations/items")` 查看草图和特征字段。

## 图纸尺寸绑定

- 按原页宽高归一化坐标，引用实际操作和参数路径。图纸中的值可用尺寸 ID 引用，计算值用表达式。
- 图纸中的有限厚度拉伸需填写 `placement_intent`，以原图的厚度轴、基准和起止位置定义区间。
- 角度驱动草图坐标轴时填 `model_orientation`。主视图右、上向量定义方向基准，绑定实际用于草图正 X 轴的始射线或终射线。
- 编译的 `binding_review` 给出可定位的局部图。用 `cad_read_result` 查阅完整诊断。

## 装配

组件路径可以是 SLDPRT 或 SLDASM。每个实例都需要唯一 ID。面、边及基准查询使用组件局部坐标。设置一个固定基座，再用配合约束其他实例。装配默认保存 SLDASM，支持通过 `export_paths` 另存 STEP 或 STL。
