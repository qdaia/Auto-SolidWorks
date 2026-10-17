# 26.10.17 发布验证 / Release validation

2026-10-17. Version: `26.10.17`; workflow: `review-iterate-deliver-1`.

## 中文

本次重新构建MCP服务与SolidWorks执行器，核对183项源码/合同身份及61项运行库文件身份。版本元数据、技能合同和中英文发布入口同步为26.10.17。

- 21个托管离线回归套件，1388项检查通过，涵盖尺寸证据、表达式、后台串行执行/恢复、来源遗漏、计划输入、受控几何/特征与集成工作流。
- 必要收尾回执回归17项通过，包括后台execution包装、pending/unknown拒绝、文件身份、保存重开与参数化限制保留。
- 公开MCP接口检查130项通过；本版尺寸表达式、锚点合同、存储输入与来源阻断等补充接口检查14项通过。
- 仓库结构、10份能力合同文档及源码/运行库身份检查通过。GitHub Actions还会在发布提交上独立执行托管构建与仓库检查。
- Windows包在发布前解压检查ZIP安全路径、逐文件SHA-256、依赖准备和解压运行库MCP接口。不会替换当前已安装插件或修改当前Codex配置。
- 整理测试时修正了旧的21工具数量断言（完整配置现含22个工具）和一次集成回归调用缺少输入参数的问题，原始失败日志保留在本地发布证据目录。没有改写产品失败回执或跳过原验收门禁。

本次26.10.17发布验证没有新执行SolidWorks原生建模。随包及公告中保留的受控原生样例和三维练习51视图属于它们原先的运行版本、哈希及验收范围；不能因改版本号而提升为本版原生认证。版本/运行库不匹配的历史证据由能力接口标为stale_or_unverifiable。

公告案例的5次原生尝试、12分16秒、17项检查及review_required状态均来自发布整理前的实际记录。未做通用盲测、完整图纸/BREP/GD&T等价或改参稳定性认证。

## English

The MCP server and SolidWorks executor were rebuilt. Identity checks cover 183 source/contract files and 61 runtime files. Package metadata, workflow contracts and Chinese/English release entry points use 26.10.17.

- 21 managed offline suites passed 1388 checks, covering dimension evidence, expressions, serial background execution/recovery, source omissions, plan inputs, bounded geometry/features and the integrated workflow.
- 17 final-review receipt tests passed, including background execution envelopes, pending/unknown rejection, file identity, save/reopen and preservation of parametric limitations.
- 130 public MCP checks passed, plus 14 release-feature MCP checks for expressions, anchor contracts, stored inputs and source rejection gates.
- Repository layout, 10 generated capability-contract documents and source/runtime identity checks passed. GitHub Actions independently builds and checks the published commit.
- Before publication, the Windows archive is extracted and checked for safe ZIP paths, every recorded SHA-256, local dependency preparation and public MCP behavior from the extracted runtime. The active installation and Codex configuration are not replaced.
- Validation preparation corrected an old 21-tool assertion (the full profile now contains 22 tools) and one integrated-regression invocation missing its input arguments. Original failed logs are retained locally. Product failure receipts and acceptance gates were not rewritten or bypassed.

No new native SolidWorks modeling was executed for this 26.10.17 release validation. Retained controlled native examples and Exercise 51 images remain bound to their original versions, runtime hashes and acceptance scopes. Renaming a version does not recertify them. Historical evidence with mismatched version/runtime identity is reported as stale_or_unverifiable by the capability API.

The example's five native attempts, 12-minute 16-second timing, 17 checks and review_required status come from the actual pre-release records. General blind accuracy, whole-drawing/BREP/GD&T equivalence and parametric edit stability are not certified.
