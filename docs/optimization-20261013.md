# Auto SolidWorks 26.10.13-opt.1 优化交付与验证

六项优化已经落实到隔离候选版。原来的 26.10.11mini 缓存 327 个文件和 Codex 配置逐项 SHA-256 复核保持不变；备份、源码和验证产物仅存本机。当前交付是可解压、可复跑的候选包，尚未替换已安装插件。

参考项目为 [solidworks-automation-skill](https://github.com/wzyn20051216/solidworks-automation-skill/tree/a3ddc5694161299b50b9e9f688fcd6f0bf815da2)，使用固定提交。吸收草图质量回读、会话边界和机械参数配方的设计方法，生产实现继续使用现有 C# / PIA / MCP，没有复制上游 Python 实现。此前比较报告和方案保留在本机 `solidworks-real-comparison-20261013-183436`。

## 已实现的六项

| 项目 | 实际行为 | 主要证据 |
|---|---|---|
| P0-A 孔位参数化 | 自动圆草图用驱动 X/Y 与点关系定位，区分原点基准与可变孔心；新增 Origin 引用。法兰用原生孔圆周阵列，保留孔数与轴向起始位置的分度圆半径驱动。 | 六孔独立位置 45→46；配方法兰 PCD90→92、6→7孔，实际孔轴检查及恢复通过。 |
| P0-B 参数化验收 | 返回完整关系证据、驱动尺寸数、Fixed实体角色、消费特征与独立的几何/参数化/视觉状态。严格可编辑要求检查最终保存重开的文件。编辑探针可声明实际几何合同与恢复合同，失败影响总体success。 | 孔径9→10保持孔心；厚度12→14改变所有孔壁长度；错误几何探针失败但恢复成功；旧固定孔心模型几何通过、参数化待复核。 |
| P1-C 方向与深度合同 | Front/Top/Right、显式frame提供方向诊断；附着/引用面无法离线确定时明确未解析。ThroughAll/UpToNext/UpToSurface不要求虚构深度，Blind/MidPlane仍需正深度。 | Front正反、Top、Right、MidPlane、自定义frame；不填depth的UpToNext、UpToSurface切除均原生通过。 |
| P1-D 机械配方 | Flange、HolePlate、HoleFamily(Through/Blind/Counterbore)展开为既有IR，并从明确源参数推导体积、面积、孔轴和完整库存。中文法兰入口集中返回缺项，拒绝未消费要求和零净距/重叠。 | 四种配方保存重开、完整几何/拓扑库存、实体有效性和草图参数化通过。 |
| P2-E 流程性能与缓存 | 预览集、覆盖理由、Automatic/Explicit/EveryNFeatures检查点可配置；保留最终保存重开和Check3。原有trace增加请求、操作、尝试身份及预览阶段；编辑验收缓存需精确探针范围和验证器身份。 | 5轮交替实测；全部10次构建机器验收通过；编辑缓存缺合同不能命中。 |
| P2-F 能力证据绑定 | 保留单一静态capability-manifest，增加当前任务证据索引；完整与精简能力查询均验证版本、6个主要运行库、模型和回执哈希、原生重开与完整库存。启动管道同时绑定executor/Core/IR身份。 | 13个当前原生范围；7项索引验证，包括版本、运行库、模型改变、缺库存、越界、坏JSON均正确拒绝。 |

原点基准可保留Fix；可变设计孔心不能靠Fix通过严格可编辑验收。当前法兰在轴向起始角时提供 `分度圆半径@安装孔定位`，45mm代表PCD90。任意非轴向起始角使用X/Y种子定位及配方重编译，没有宣称所有角度都有独立原生PCD/角度驱动。

## 最终验收

实测 SolidWorks 2025，revision **33.0.0**。最终运行库下 **13个原生场景**通过：四种机械配方、独立六孔位置模型、六种方向/框架场景以及UpToNext/UpToSurface。全部检查保存重开、重建、原生实体有效性、独立体积/面积/孔壁/轴线及完整面边库存。5项实际几何编辑试算通过：孔位X、PCD、孔数、孔径、厚度；每次检查实际变化、恢复后的几何和源文件字节保持。

两项原生负例通过：故意错误的目标孔轴使编辑探针和总体success失败，恢复仍通过；旧版6个固定孔心模型在严格模式下被判定为参数化待复核，源字节保持。几何成功不会覆盖声明的参数化失败。

10个离线套件共 **777项**通过，其中新增优化回归15项；公开MCP完整工具配置的 **129项**离线接口/合同/编译/dry-run检查通过。它们与原生结果分开计数。两个旧FocusedCore断言只识别英文，而基线已输出中文；复现后将断言调整为同时识别两种语言，没有放松生产来源门禁。测试启动器的参数/目标框架错误保留在中间日志，最终可复跑启动器独立构建后按项目声明框架执行。

人工审查74幅原生预览，包含四种配方的孔口侧图、独立六孔位置模型、六方向模型、两种终止方式和首轮性能样本。最终性能批次35幅图全部与已审查图逐字节SHA-256相同；13个原生交付场景的67幅预览与35幅最终性能预览合计102幅获得实际审查或相同字节的审查覆盖。它们不是完整工程图或BREP等价认证。

视觉检查发现固定BMP输出曾裁掉轮廓，已在原生适配后保留取景余量，并实际重做验证。盲孔/沉孔配方自动补充背视图，不能以仅正视图认证孔口。机器inspection中的visual_status保留not_observed；独立人工审查回执单独保存，未伪造机器视觉通过。API语义参考 [SaveBMP](https://help.solidworks.com/2017/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.imodeldoc2~savebmp.html) 与 [ViewZoomtofit2](https://help.solidworks.com/2023/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IModelDoc2~ViewZoomtofit2.html)。

## 最终性能数据与边界

同一最终运行时，同一明确法兰源草案，交替顺序各5次。计时包含计划编译与原生build调用，包含建模、实际检查、保存重开和预览；开始前已有本机SolidWorks会话和执行器。它不包含识图、代理推理、编写/修复源码、连接准备和另外执行的编辑试算。

| 流程 | 次数 | 中位数 | 范围 |
|---|---:|---:|---:|
| 默认5视图、一个中间检查点 | 5 | 11.49秒 | 11.03–12.48秒 |
| 明确front/isometric覆盖、无中间检查点 | 5 | 7.93秒 | 7.71–8.06秒 |

本组中位数减少 **30.95%**。这是两种流程设置在同一优化版上的比较，不能声称是旧mini到新版的通用提速，也不是完整识图建模总耗时。省略中间检查点会减少失败恢复粒度。前两轮数据和全部开发失败/修复记录保留；没有跨运行时、窗口状态或验收范围拼接更快数字。

trace为包含子阶段的墙钟span，不应相加当全过程。原生outcome_unknown仍按request_id查持久回执，未引入未知结果自动重放。

## 证据与复跑

- 当前索引：`plugins/auto-solidworks/skills/auto-solidworks/references/current-native-evidence.json`。只提升13个列出的任务范围与SW33.0.0，其他能力保留各自历史边界。
- 当前原生模型、源IR、构建/检查回执、实际编辑合同与人工审查在索引的包内相对路径；最终运行库身份在`runtime/build-identity.json`。
- `tests/run-optimization-offline.ps1`：10个离线套件，输出目录需全新。
- `tests/optimization_native.py`、`optimization_recipes_native.py`、`optimization_matrix_native.py`、`optimization_delivery_native.py`、`optimization_extended_probes.py`：本机原生验证，使用隔离包启动器，不修改已安装插件。
- `tests/optimization_benchmark.py`：5轮交替流程比较；`optimization_capability_evidence.py`：证据失效负例，finally恢复所有测试字节。
- `scripts/record-native-evidence.py`：仅接收已通过的原生回执与实际人工预览审查，记录运行库/模型/回执哈希。
- `scripts/package-release.py`：本地ZIP，拒绝覆盖旧包；`scripts/verify-package-local.py`：解压、SHA-256、只准备本机Interop和公开MCP离线验证，使用SkipCodex、不替换安装。

这次没有重新认证复杂识图、复杂曲面、装配运动、工程图全页等价或仿真。默认所有项目只在本地使用；没有上传、推送或发布。
