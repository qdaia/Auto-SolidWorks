# 完整输入复用与增量修改

版本 `26.10.11` 增加 `cad_store_model_input`，整合此前独立优化版的输入复用流程，插件名称统一为 `auto-solidworks`。

## 适用情况

大型图纸上下文、多轮遗漏复查、失败后的局部修正和反复编译。简单一次性零件直接使用原有 `draft`／`text` 路线，可避免额外存储调用。

## 流程

1. 实际检查源图及所有非空细节，按原合同撰写图纸上下文。用 `cad_store_model_input(kind="drawing_context", input=<完整上下文>)` 保存，保留 `input_id`。
2. `cad_review_drawing_coverage(inventoryPath=..., drawingContextId=<context句柄>)` 复查，继续处理全部未解决候选。句柄不免除图纸观察和遗漏检查。
3. 将几何操作、验证和恢复设置作为完整草案保存：`cad_store_model_input(kind="draft", input=<未内嵌drawing_context的草案>)`。
4. `cad_create_model_plan(nativeOutputPath=..., draftId=<draft句柄>, drawingContextId=<context句柄>)` 编译。服务端恢复完整输入并执行原编译器；全部源绑定和验证要求保留。
5. 编译通过后，使用返回的 `plan_id` 执行 `cad_build_model`。执行、最终保存重开和导出检查遵循原流程。

## 增量修改

`cad_store_model_input(kind="draft", baseInputId=<原句柄>, edits=[...])` 创建新不可变草案。上下文修改使用 `kind="drawing_context"`。每批 1..200 项，不改写原工件。支持 JSON Patch 子集：

```json
[
  {"op":"test","path":"/operations/3/feature/radius_mm","value":3},
  {"op":"replace","path":"/operations/3/feature/radius_mm","value":4}
]
```

支持 `add`、`replace`、`remove`、`test`；`remove` 不带 `value`，其他操作必须提供。路径使用 JSON Pointer，`~1` 表示 `/`，`~0` 表示 `~`；数组 `add` 支持 `-` 追加。禁止整个根对象替换，不支持 `move`／`copy`。非数组索引、未知字段、缺少必填字段、前置条件不匹配均拒绝，整批失败不保存新工件。按操作序号定位前先读取当前 `/operations`，不要猜序号。

修改尺寸时，必须同步修改其源绑定与独立验证，不能为了通过而删掉期望。图纸要求变化时，应提供对应的新来源和重新审阅记录。来源文件、遗漏清单、恢复文件或编译器身份发生变化，旧句柄拒绝复用；重新保存／编译。

## 输入与结果

`input` 与 `baseInputId` 二选一。首次保存不带 `edits`；增量必须同时提供基线句柄和 `edits`。`draft`、`text`、`draftId` 三选一。`drawingContextId` 只能用于没有内嵌图纸上下文的草案，不能同时提供两份上下文。

输入工件支持现有 `cad_read_result(artifactId=<input_id>, section=..., offset=..., limit=...)` 完整分页读取。严格字段校验不允许拼错尺寸字段被忽略。

保存返回 `status=stored_not_validated`、`compiled=false`。这仅证明完整输入已存储，不证明理解、覆盖、几何或制造要求通过。原生操作仍由计划执行器验证。工件哈希用于发现意外变化，不是本机恶意作者的权限边界。

## 验收证据复用

建模回执已包含最终保存重开及声明的验证结果时，通过 `cad_read_result` 读取全部必要证据，避免再为相同文件和相同检查调用原生检查。新增检查、缺失／截断证据、输出文件变化仍需检查。STEP 与原生模型分别验收；不把特征数或包围盒回执当成全部源图等价证明。

## 测量边界

该优化减少重复请求文本和AI生成完整JSON的工作。验收采用公开MCP的真实编译／遗漏复查／dry-run；发布重建未重新进行原生全量认证；此前两图对照的成功与失败见发布测试说明。本版未承诺AI实际token账单、识图总耗时或完整建模提速比例。
