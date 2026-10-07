# 架构

Auto SolidWorks 将模型解释、参数计划、原生执行和结果检查分开，便于在运行 CAD 前审查需求，并在运行后核对实际结果。

| 层 | 职责 |
| --- | --- |
| 插件技能与 MCP | 收集图纸或尺寸、查询能力、编译计划、发起建模与检查 |
| Modeling IR | 表达操作依赖、单位、配置、几何引用、输出和验收要求 |
| CadModeling.Core | 验证输入、编译特征、检查几何合同、管理恢复及回执 |
| SolidWorks 执行器 | 在独立 Windows 进程中调用本机 SolidWorks，创建原生特征并保存、重开、读回 |
| 本地 Gordon 工具 | 使用固定版本曲线网格算法与 FreeCAD / OpenCascade 生成 NURBS 输出 |

构建身份将生产源文件、能力清单与运行文件的哈希关联。原生执行租约限制并发访问；状态回执区分完成、失败和结果未知。检查点与持久引用用于判断恢复前段是否仍有效。

当前操作范围及对应证据见[能力清单](../plugins/auto-solidworks/skills/auto-solidworks/references/capability-manifest.json)。
