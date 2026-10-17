# 可组合执行与局部修正：composable-source-1

本地适配层借鉴 solidworks-automation-skill 的小步骤组合、显式依赖和局部修正方式。实际几何执行仍使用 Auto SolidWorks 的原生执行器，不导入第二套 COM 写模型后端。二进制版本仍为 26.10.13-opt.1。本文件的辅助脚本是技能调用路径，不是所有 MCP 请求强制经过的新门禁。

## 图片到建模的工作路径

1. 已有 `cad_read_drawing` 回执时直接复用，用 `prepare` 从已验证库存生成审图包。没有回执时用 `ingest` 一次调用原生读图入口并生成包；两条路径都不运行 OCR，避免重复准备来源。
2. 阅读原图、全部原始区域和候选。包只对 SHA-256 完全相同的区域图共享一次图像阅读，保留所有 region_id、页码/区域库存及候选。不同字节的图不合并。相同像素不同编码暂不去重。原图必读。AI直接从图像读取文字和尺寸，并记录原图定位。ING-AI-VISION-REQUIRED 表示等待AI识图；空文字候选不能解释成没有文字。
3. 审核后按原合同填写视图、尺寸端点/基准、来源观察、备选解释及反证、关键参数绑定、遗漏审核和独立几何期望。在一个 bundle 中维护，不从生成模型回填来源。所有 critical 字段、设计尺寸、约束和验收检查仍由原编译器检查。
4. 按机械功能把操作和相关来源/检查放入 steps。每步一个稳定 ID；依赖明确放入 depends_on。几何引用与尺寸引用也会保守补充跨步骤依赖。将相互绑定的步骤合并成一个步骤，避免循环。建模顺序具有语义：应显式声明实体合并、布尔、圆角/倒角、引用与库存等顺序依赖，不依赖步骤字典排列。
5. 检查通过后 `run --execute` 直接编译并用 plan_id 建模，不增加审批或额外 dry-run。仅优化/开发任务用默认编译或 `--dry-run`，不触发原生。只有用户已授权建模的任务才使用 `--execute`。
6. 必须读取执行回执及实际预览，按 essential-final-review.md 完成保存重开、重建、有效实体、声明的参数/几何验收和实际视觉审核。脚本完成或 compiled=true 不代表模型交付完成。

脚本路径是插件根目录下 `scripts/composable-workflow.py`。调用脚本的 Python 只需标准库；图像/PDF准备由已安装插件提供，本地OCR已停用。路径示例由当前任务改为绝对路径，输出目录必须全新。

```powershell
# 无读图回执时：只提取和准备，不自动解释
python -X utf8 '<插件根>\scripts\composable-workflow.py' ingest '<原图绝对路径>' --output '<新读图目录>'
# 已有库存回执时：直接复用来源
python -X utf8 '<插件根>\scripts\composable-workflow.py' prepare '<库存绝对路径>' --sha256 '<回执哈希>' --output '<新审图目录>'
# 将已有完整 typed draft 导入为待审核 main 步骤；不自动标为已审核
python -X utf8 '<插件根>\scripts\composable-workflow.py' init '<draft.json>' --output '<bundle.json>'
# 检查当前来源后，由代理记录本次实际审核。review 不是要求用户另行批准。
python -X utf8 '<插件根>\scripts\composable-workflow.py' review '<bundle.json>' --revision '<当前revision>' --steps main --note '<实际审核证据>' --output '<reviewed.json>'
# 离线验证；不连接 SolidWorks
python -X utf8 '<插件根>\scripts\composable-workflow.py' run '<reviewed.json>' --native-output '<新SLDPRT绝对路径>' --output '<新回执目录>' --dry-run
# 用户已授权的建模任务：同一编译/执行链
python -X utf8 '<插件根>\scripts\composable-workflow.py' run '<reviewed.json>' --native-output '<新SLDPRT绝对路径>' --output '<新回执目录>' --execute
```

会话已暴露 cad_* 时可用 `emit` 产生精确 `cad_create_model_plan` 参数，再直接调用现有工具，避免启动辅助 MCP 进程。不要把大 JSON 在聊天中重新手抄一遍。服务端已有 draft/context 句柄的任务继续使用 `cad_store_model_input` 的 JSON Patch；两种入口都经过同一编译器。

## bundle 与单一尺寸记录

`schema="composable-source-1"`；`draft` 保存名称、source_text、恢复策略、参数化意图、验收全局选项等原字段，不能重复放 operations/drawing_context/recipe。`context` 保存原来源上下文的 source_path、views、omission_review、投影与两个 true 门禁。`sources` 钉住原图和库存的绝对路径/哈希。`steps` 每项允许：

- id、depends_on、reviewed、review_note。
- operations：原 typed operation，可用全部当前原生操作类型；不会把第三方任意 Python 函数自动变成受支持操作。
- dimensions、features、interpretation_reviews：原来源合同中的相应数组，ID 必须唯一。
- verification：bounds、cylinder_groups、native_dimensions、surface_samples、boundary_clearances、bindings；特殊全局选项仍放 draft.verification。

同一来源尺寸只维护一个数值。例如操作的 width_mm 与独立 bounds.size_mm.x 都可引用 `{"$dimension":"plate_width","unit":"Millimeter"}`。引用从来源 dimensions 取值，不从操作取值；单位必须完全匹配，不猜 mm/inch 转换。如果绑定字段的原生单位不同，显式派生另一来源尺寸并记录推导关系。未使用引用的原数值仍由原编译器检查是否匹配。

`fact_dependencies={"derived_id":["premise_id",...]}` 记录直接推导关系；必须完整声明跨步骤的派生依赖。脚本检查循环和不存在的 ID，并传播已声明/可解析依赖的失效；不会解析自然语言推导或自动重新计算派生值。遗漏依赖不能得到自动影响范围保证。审核员仍须核对派生值、跨视图一致性和源标注端点。

现有 draft 可先以一个 main 步骤使用，逐渐按建模意图拆分。`composable-example.json` 是可编译的文字板件两步骤示例，仅作结构演示，不是图片识别结果。

## 局部修正

`revise bundle correction --output new-bundle` 支持整个步骤替换，或下面的按 ID 字段修改。字段路径只允许已有字段，expected 是并发/误定位保护；一次修改有任何失败则整批不产生新 bundle。旧 bundle 不覆盖。

```json
{
  "expected_revision": "<当前bundle的规范JSON SHA-256>",
  "changes": [
    {"step_id":"profile","collection":"operations","id":"sk",
     "path":"/primitives/0/height_mm","expected":50,"value":60}
  ]
}
```

collection 还支持 dimensions、features、interpretation_reviews，以及 verification.bounds 等带 id 的检查。修正图纸尺寸优先修改 dimensions 中经过源图确认的值，所有 `$dimension` 消费者随后自动取新值；原始 literal、推导、来源和独立预期必须与真实需求一致，原编译器仍会拒绝不一致。

所有修改步骤及其传递依赖会清除 reviewed 标记；context 或 fact_dependencies 改变则全部失效。只有实际复查后才能 review 当前 revision。独立无依赖步骤的原审核记录保留。对来源文件改动必须重新读图、建立新源身份；不能把旧审核套到新图。

修正原生失败后，保留完整最终操作/要求，按原 recovery 合同使用兼容 checkpoint。原执行器验证文件、来源和操作前缀，重放后缀；本适配层不承诺任意中间特征树原位修改。来源/验收变化可能使旧 checkpoint 不再兼容，按拒绝回执选更早检查点或重建。

## 回执、性能与限制

所有 MCP 请求先落盘，再发送；响应、编译输入、plan_id、执行派发记录和阶段时间保留在新的输出目录。失败编译不派发建模；超时/断连禁止自动重试，先查持久执行状态。复用一个输出目录会失败，避免覆盖旧回执。

图片理解由代理实际完成，source_interpretation_automatic=false。此次只验证离线回归、真实读图提取、编译和 dry-run，没有新图盲测、原生建模或端到端 A/B。去重和少写字段属于明确的数据流改进，不应据此宣称 OCR 准确率或原生耗时提升。

设计参考：[solidworks-automation-skill](https://github.com/wzyn20051216/solidworks-automation-skill)。已检索 Hugging Face 的工程图 OCR 项目与数据集；历史检索不构成当前OCR依赖；当前流程已取消本地OCR，不新增视觉模型服务。
