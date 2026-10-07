# 方程、全局变量与配置（离线源码实现）

当前源码 `0.6.0+engineering.20261002.11.offline` 的证据范围为编译、核心计算、模拟端口和公开 MCP 预演。SolidWorks 原生创作、方程求解、配置继承、保存重开及几何验收均未执行；源码适配器和保存门槛不是原生成功证据。此前 `.4` 原生结果按原版本保留。

`cad_create_model_plan` 的 typed draft 增加 `design_intent`，编译到同名 Modeling IR 字段。几何操作完成后执行设计意图；已有源模型可以使用空 operations 和非空 design_intent，在既有 source_model_path 副本上编辑。仍需真实存在的绝对 `.SLDPRT` 输入及独立输出路径。新零件不能使用空 operations。

- `global_variables`：准确 name、expression 和默认 false 的 replace_existing。变量按依赖拓扑顺序创建，重复、大小写歧义、缺失引用和循环拒绝。
- `expression.kind` 为 Literal、Variable、Dimension、Add、Subtract、Multiply、Divide、Function、Negate。Literal 包含 `{value, unit}`，单位仅 Millimeter、Degree、Unitless；Variable 引用声明的全局变量；Dimension 用 `dimension_name` 引用受控目标或独立输入。算术节点使用 left/right，Function 使用 function/arguments，Negate 只有一个 arguments 子项。加减要求相同单位，乘法需至少一个无单位量，除法需无单位除数或同单位比值。不接受任意原生方程字符串或复合量纲。
- 固定函数：Abs、Sqrt、Sin、Cos、Tan、Asin、Acos、Atan、Exp、Log、Int、Sign、Min、Max、Power。Log 是自然对数，核心 Int 为向下取整，Sign(0)=0；这些边界的原生求解语义尚未校准。三角函数显式使用 Degree，反三角函数返回 Degree；执行前和保存重开后要求方程角度模式为 Degrees。Min/Max 生成固定 IIF，Power 生成括号包围的幂运算。每表达式最多256节点、深度32、依赖深度256，渲染文本最多16384字符；定义域错误、溢出和正切奇点拒绝。
- `equations`：dimension_name 必须是准确的 `参数名@特征名`，声明 expression、独立 expected_value 及 replace_existing。基准方程作用于所有配置。每配置 `global_variables` 可覆盖声明变量的 expression，并提供独立 expected_value；`equation_values` 提供配置内尺寸方程的独立预期。变量与尺寸使用联合依赖图，混合循环和跨配置量纲变化拒绝。
- 根 `dimension_inputs` 声明不由方程控制的独立尺寸及读回预期；配置 `dimension_input_values` 可声明配置的读回预期。这些字段不写尺寸。参考尺寸可作为输入，但 dimensions 写目标必须可驱动。新配置在创建前核对明确来源配置的输入；先验值与最终数值覆盖分别检查。尺寸依赖或配置变量要求 AutomaticSolveOrder 已开启，端口不会擅自改变现有求解设置。
- `configurations`：每项 name，显式 reuse_existing，或为新配置提供 create_from_configuration（必须已存在）。dimensions 使用 dimension_name 和 value；geometry 声明实体数、曲面体数、体积／质心等验收要求。原生 Check3 不得关闭。
- `active_configuration`：最终活动配置，必须是本计划声明的配置。`require_fully_defined_sketches` 按准确名称列草图，在每个配置中验证未抑制且完全定义。

执行前对全部现有配置快照；拒绝外部方程或设计表控制。逐个尺寸核对原生类型、可驱动状态和 SI 值。已有方程未明确 replace_existing 时拒绝，多配置原有方程若不是 Add3 兼容行，则修改失败，不改用更弱 API 重试。全局变量更新影响的未声明原有方程若求解值变化，当前保守保护会拒绝；应明确声明受控尺寸链。

`EvaluateAll()` 文档规定成功和失败都返回 -1，执行器不以此返回值作为成功。读取 Value[index] 后立即核对 Status == index，并要求请求方程已启用、全配置作用域、目标类型及定义一致。IEquationMgr.Value 原始单位在本版尚未原生核实，仅记录有限值、求解状态及重开稳定性；实际受控尺寸从 IDimension.GetSystemValue3 以 SI 单独核对。

修改后遍历配置，拒绝数值覆盖影响未指定配置；检查每个声明配置的原生实体／几何合同和所有指定草图。保存读回重复遍历，核对配置清单、活动配置、方程和受控尺寸。失败或取消停止后续提交，并恢复原活动配置；已经发生的修改不会被伪称为已回滚，遵守原有未知结果不自动重放原则。

基准示例见 `references/examples/design-intent-plate.json`，扩展示例见 `references/examples/design-expression-plate.json`。它们是离线编译夹具，尚未生成原生模型。扩展示例以高度尺寸为独立输入，标准宽80mm、加厚配置宽100mm，并声明对应最终体积。设计表、全局变量原生 Value 单位与真实函数等价仍是开放边界。

研究参考：[SolidWorks 参数编辑器源代码](https://github.com/lci-ang/solidworks-param-editor/blob/main/scripts/sw_modify.py)、[SolidWorks Agent Skill](https://github.com/LTGOz/solidworks-agent-skill)。本轮未导入这些项目代码：模糊名称匹配、统一乘 1000 和吞掉异常不满足当前准确身份／单位／失败拒绝要求。本机 SolidWorks 2025 官方 CHM 的 Add3、SetEquationAndConfigurationOption、EvaluateAll、Value、Status、AddConfiguration2、GetSystemValue3 文档用于适配器合同核对；解包和程序集反射都不启动 CAD。

## 配置特征抑制

`configurations[].feature_suppression` 是按顺序执行的 `{feature_name, suppressed}` 列表，支持抑制及解除抑制。目标名必须准确且唯一；不能抑制受控尺寸、独立输入或必须完全定义草图的来源特征。未知特征在原生预检中拒绝，compile/dryRun只验证静态合同，不证明目标在原生模型内存在。

执行前读取所有现有配置的完整特征清单及持久身份。新配置与显式来源快照比较；未声明特征的状态、未指定配置的状态必须保留。隐式连带抑制必须全部明确声明，否则失败。保存重开后重复完整清单和身份核对，原生使用不透明持久身份等价判断，而非只比较字节。

原生适配器通过GetFeatures(false)、IsSuppressed2和SetSuppression2的当前配置作用域实现；尚未原生运行。完整清单不可读、身份缺失、重复名称、设计表控制均拒绝。复杂依赖导致尺寸不可用或几何合同不符会失败；不会把部分修改声称为回滚。
