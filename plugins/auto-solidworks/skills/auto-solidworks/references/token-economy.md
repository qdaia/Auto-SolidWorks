# 精简工具、计划句柄与结果读取

版本 `26.10.11` 默认使用 `compact` 工具配置和摘要返回。核心几何编译、原生操作、源图纸绑定、未知执行不重放和最终保存重开验收继续调用原实现；本轮不启动 SolidWorks，不认证原生提速。

1. `cad_create_model_plan` 接受原有 `draft` 或 `text`，默认返回 `plan_id`、操作数、输出路径、诊断以及完整编译结果的 `artifact_id`。计划存在本机 LocalAppData/AutoSolidWorks/mcp-artifacts；文件内容哈希成为句柄的一部分，完整计划包含输出和验收要求，同时绑定 MCP/Core/IR 编译器身份、源模型、源图纸、遗漏清单、恢复清单及恢复原生文件。内容、来源或编译器变化后必须重编译。哈希检测意外变化，不作为抵御本机恶意用户的权限边界。
2. `cad_build_model` 提供 `planId`（返回字段为 `plan_id`）或旧 `irJson`，必须择一。默认立即执行；`dryRun=true` 只执行原有计划检查。为了检查草案才使用 dry-run，普通建模不增加一轮无必要的调用。不能因为外部 timeout 或结果文件保存失败而重放原生操作；保留 request_id 并查询状态。
3. 默认编译结果不返回重复的 `plan`/`ir_json`。调试或已有自动化兼容需要时，指定 `responseDetail="full"`。读图、建模、检查、装配和工程图导出也支持该参数；状态查询始终返回小型结构，完整状态回执用 `cad_read_result` 读取。
4. `cad_read_result(artifactId, section, offset, limit)` 使用 JSON Pointer：`/evidence`、`/verification/checks`、`/features`、`/pages/0/observation/observations` 等。省略 section 列顶层字段，嵌套对象/数组给出更细 section。数组每页 1..200 项，内容字符预算 24000；超大单项必须细分到其字段。字符串分块以字符偏移续读，不能从 Unicode 代理对内部起读。必须使用返回的 next_offset；终页为 null。文件每次读取均核对内容哈希。
5. 摘要保留原始成功／失败、pending、outcome_unknown、deadline、capture_complete、model_reopened、rebuild_succeeded、遗漏状态、失败总数及前 12 条行动项，超出时明确 truncated。详细证据全部保存在本地。几何/单位/内部计算不做有损压缩。摘要不意味着图纸完整理解、GD&T 完整或超出既有验收范围的成功。
6. 工具 schema 保留轻量 JSON 参数，在服务端反序列化为原类型并验证。需要完整参数结构时调用 `cad_get_contract(contract)`，支持 draft、inspection、drawing_context、assembly、drawing_layout；已有技能参考仍适用。未知字段拒绝，避免错拼尺寸被静默丢弃。
7. 默认 16 个工具；完整配置 21 个工具，保留原有 18 个名称并增加合同和结果读取。完整配置增加 `cad_capture_projection`、`cad_capture_section`、`cad_verify_revolved_family`、`cad_verify_hole_group`、`cad_verify_edge_treatment`。启动 MCP 前设置 `AUTO_SOLIDWORKS_TOOL_PROFILE=full`，或由 coding-tools 桥接在新子进程环境设置；未指定时为 compact。不需要这些专用工具时不加载其大型 schema。

示例调用：

```json
{"text":"80 x 50 x 20 mm plate","nativeOutputPath":"C:/Users/csj17/Documents/AutoSolidWorks/Models/安装板.SLDPRT"}
```

编译成功后直接使用返回的句柄：

```json
{"planId":"plan_<工具返回的完整哈希>"}
```

失败诊断或详细验收结果按需读取：

```json
{"artifactId":"result_<工具返回的完整哈希>","section":"/evidence","offset":0,"limit":50}
```

结果保存在本地并持续保留，本版不自动清理。旧编译器签发的计划升级后重新编译；旧结果可继续读取。需要清理时遵循用户的回收站要求。

本版新增 [完整输入复用与增量修改](input-reuse.md)，保存图纸上下文／草案后，复查和编译使用句柄，修正仅提交修改项。原编译器、遗漏复查和原生验收继续执行。原生执行器源码未改动，接口传输量减少不代表原生或完整任务提速。
