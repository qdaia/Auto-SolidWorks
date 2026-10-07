# 26.10.07

- **默认中文输出**：新增设计树、草图、装配零部件、配合、工程图页名与视图、参数表、诊断和工具说明的简体中文默认文本。
- **本地 Gordon 曲面工具**：新增基于固定版本 CurvesWB / TiGL 与 FreeCAD / OpenCascade 的曲线网格建模，输出 NURBS 曲面 STEP、BREP、FCStd、预览和检验报告。
- **高级建模控制**：新增三维曲线、原生螺旋线、中心线放样、导向影响范围、端部切向长度、多实体归属及裁剪到加厚的连接检查。
- **曲面参数合同**：新增 Boundary 选择身份、标记、顺序和源曲线核对；新增 Fill 逐边支撑面、来源身份、连续性控制读回及保存定义回执。
- **参数与配置设计**：新增全局变量、配置表达式、15 个固定函数、变量与尺寸联合依赖、单位和角度检查，以及配置级特征抑制、解除抑制和保存读回。
- **尺寸描述编译**：新增中心平底盲孔、中心通孔和明确开口的向内抽壳描述，配套单位归一化、完整需求检查及独立几何合同。
- **持久几何引用**：新增源语义与根实体身份、受控尺寸修改和圆角历史、保存回执重验，以及限定范围样条控制网、节点、权重、裁剪与球面参数的完整修订检查。
- **名义实体螺纹与装配检查**：新增名义内外实体螺纹、左右旋输入及牙深检查；新增空间开放树同轴机构的瞬时相对运动基与自由度检查。
- **工程图输出**：新增装配体同名 SLDDRW / PDF 输出、模型与组件原生尺寸导入、保存后尺寸读回，以及可配置的视图布局和分页。
- **执行控制与恢复**：新增 `cad_execution_status`、`cad_pause_execution` 和 `cad_build_gordon_surface`，公开 MCP 工具扩展至 18 个；新增跨进程原生执行租约、执行截止、状态回执与恢复身份核对。
- **安装与交付**：新增 Codex 桌面版 CLI 优先选择、版本及构建身份核对、源码测试入口和逐文件 SHA-256 清单，提供 Windows x64 安装包与校验文件。

[中文更新说明](docs/release-26.10.07.zh-CN.md) · [English release notes](docs/release-26.10.07.en.md)

# 0.6.0+engineering.20261002.9.offline

- Boundary 选择身份／标记／顺序、曲面工厂与保留控制统一到离线可测端口；源曲线身份与保存回执核对，补齐弯曲／空间网络合同及失败路径。第二方向裁剪读回、影响枚举分歧和实际原生创建仍未验收；全 B01–B06 范围保留。

# 0.6.0+engineering.20261002.8.offline

- 填充曲面逐边支撑及来源身份合同、连续性设置提交后重读、保存定义回执核对。两个外部来源面的原生 G2 支撑观测仍不可用并拒绝。仅离线编译与核心／模拟端口验收；未运行 SolidWorks。完整 B01–B06 目标保留。

# Changelog

## 0.6.0+engineering.20261002.7.offline（本地源码）

- GeometryRef 增加独立源语义、根实体、来源特征持久身份和原始配置合同。完整历史覆盖未变、修改、生成、分裂、合并和删除关系；当前源几何经唯一后继及实际库存核对，不使用旧坐标或最近实体后备。
- 修复与边处理验收重放完整历史回执，拒绝身份、源修订、所有权、库存、配置或摘要被篡改的成功状态。
- 引用指纹改为类型化 JSON，消除字段／源事实分隔符碰撞并规范化数值负零；旧指纹回执需重新生成。
- 原生库存提取增加来源特征身份。原生历史端口目前只支持未变修订，仍缺跨重建／保存的内核变更历史；Tracking ID 不替代此证明。本版未启动或调用 SolidWorks，完整目标仍未完成。

## 0.6.0+engineering.20261002.6.offline（本地源码）

- 有限文本增加板件／圆柱中心平底盲孔、圆柱中心通孔及明确顶部开口／封闭向内抽壳；生成实际切除／抽壳操作，并声明独立体积、孔壁、底面及开口检查。
- 装配增加源设计意图绑定的一般瞬时相对自由度合同：平移／旋转／螺旋的最多六维完整运动基，装配坐标、轴线位置、mm/rad 导程和与选基无关的子空间距离。
- 保存前与重开后的装配自由度检查入口已接入源码；原生适配器仅能确认固定或完全约束组件对的零相对运动。活动机构缺完整运动基时明确失败，不解释未文档化 API 返回值或猜测零自由度。
- 本版只做源码编译与离线检查；不启动／调用 SolidWorks，未安装到个人插件缓存，工程图导出不在修复范围。B01–B06 完整目标仍在推进。

## 0.6.0+engineering.20261002.5.offline（本地源码）

- 类型化 `design_intent`：有界算术、全局变量依赖图、全配置尺寸方程、独立期望值以及指定配置的数值尺寸覆盖。
- 修改前验证准确目标、原生参数类型、驱动状态和现有方程替换权限；逐配置重建、读回，并拒绝尺寸更新泄漏。保存重开核对配置清单、活动配置、方程定义与受控驱动尺寸。
- 添加独立核心／模拟原生接口回归，不启动 SolidWorks。原生适配器仅编译；本版原生创作、保存重开和几何验收均未执行，历史原生证据不重认证。
- 外部方程、设计表、任意原生表达式、按配置改变全局变量及配置特征抑制仍不支持。工程图导出不涉及。源码版本尚未替换用户已安装的 `.4` 缓存。

## 0.6.0+engineering.20261002.4 - local modeling acceptance hardening

- Capture supported advanced native definitions after the final save and compare retained identities and controls after close/reopen. Reacquire live feature wrappers and compare opaque persistent references using SolidWorks IsSamePersistentID.
- Add explicit sampled G2 seam checks using native surface second derivatives, normal-aligned curvature forms and inverse-mm tolerances; unavailable or degenerate derivatives remain unverifiable.
- Native analytic fixtures distinguish coplanar G2 from a tangent plane/R2-cylinder seam with curvature mismatch. Boundary factory and Curvature Fill assignment failures remain open.
- Modeling only; local package and installation, no drawing-export certification or publication.

## 0.6.0+engineering.20261002.3（本地）

- 装配保存重开逐一验证配合的组件实例、实体持久引用、对齐方向和同心旋转锁定，返回 `mate_readbacks`。
- 提前拒绝重名配合、无效枚举、多个实体端点、被忽略的数值／旋转／对齐标志；完全约束要求继续检查轴向自由度。
- 3D 折线／样条与 2D 草图采用相同的 AddToDB 自动推断隔离，恢复创建前的草图显示与推断设置；两个视图尺度的原生坐标及保存重开验证通过。
- 原弯曲 2×2／空间 Boundary 和平面 Curvature Fill 再次复现失败，普通 Tangent Fill 通过；保留失败证据及剩余验收清单，不声明复杂 Boundary／G2 已修复。
- 仅本地建模升级；工程图导出不在验收范围内。详见 [建模可靠性修复](plugins/auto-solidworks/skills/auto-solidworks/references/engineering-reliability.md)。

## 0.6.0+engineering.20261002.2（本地）

- 修复 SetDimension 的整数／长度／角度单位转换，增加重建与最终保存参数读回。
- 修复全边圆角 `R2` 简写被误判为未消费 `r` 文本的问题，继续拒绝未实现的需求。
- 所有最终零件强制保存后重开、重建和原生 Check3 实体／曲面故障检查，空 verification 也不能跳过。
- 普通板件／中心通孔／圆柱模板补充关键驱动尺寸和完全定义验收，替换正方形的自动边等长冲突。
- 攻丝孔分离钻孔深度与有效牙深，保存后核对原生装饰螺纹；仍不生成实体螺旋牙。
- 装配默认拒绝未明确允许干涉，可选要求完全约束；重开核对组件路径／配置及配合类型／驱动值。
- 验收范围限于本轮建模回归样例；复杂曲面、G2 和工程图流程未重新认证。详见 [修复说明](plugins/auto-solidworks/skills/auto-solidworks/references/engineering-reliability.md)。

## 0.6.0+zh.20261001.3（本地）

- 新生成的模型、43 种原生特征、辅助草图、装配零部件和工程图文字默认简体中文。
- 工程图采用微软雅黑，页名、标准视图、标题、参数表、单位及覆盖备注均为中文；保留原生尺寸引用与投影规则。
- 工具说明、诊断、验证结果及通用工具异常提示使用中文；协议标识、用户明确指定的名称和源文件保留原值。
- 通过离线回归与中文零件／装配体／工程图保存、重开、PDF 验证。详见 [中文默认输出说明](docs/chinese-default-output.zh-CN.md)。

## 0.6.0+modeling.20261001.4 (local)

- Add typed native boss/cut loft centerlines, guide influence, tangent lengths and reversal controls with retained-definition validation.
- Extend explicit 2x2 Boundary endpoint checks to spatial curves; new native Boundary trial remains failed.
- Commit standard trims using proved original-target points and resolve trim-to-thicken bodies through native feature membership.
- Five new complex fixtures passed creation/rebuild/save/reopen; old modeling fixtures and suites were not rerun. SurfaceLoft complex controls fail validation; G2 remains uncertified.

## 0.6.0 — Optimized release and expanded validation evidence

- Promote the current rc4 modeling/verification implementation to version 0.6.0 without changing its algorithms.
- Update runtime identity, launcher namespace, current download pages and separate Chinese/English release notes.
- Publish sanitized 22-stage performance evidence: 30 measured samples, 93 n=5 cells and 570 passing assertions, with explicit scope and no speedup claim.
- Preserve the independent-drawing, long-duration and optional-feature limitations; do not count the interrupted holdout as passed.

[中文更新公告](docs/release-0.6.0.zh-CN.md) · [English release notes](docs/release-0.6.0.en.md)

## 0.5.5-rc4 — Drawing review, native verification and constrained recovery (prerelease)

- Expand public tools from 9 to 15 with drawing coverage review, native projection/section capture and revolved/hole/edge family gates.
- Add source facts and review coverage, identity-bound geometry references, directional measurements, bounded connectivity and constrained repair/difference checks.
- Strengthen native recovery and signed-zero plan identity handling; preserve existing modeling and export functionality.
- Add optional internal timing with unchanged operation semantics; publish measured scope without a speedup claim.
- Publish the validated rc4 runtime and updated regressions, with separate Chinese/English release notes. Independent drawing acceptance, complete performance coverage and optional OCR/viewer work remain pending.

[中文更新公告](docs/release-0.5.5-rc4.zh-CN.md) · [English release notes](docs/release-0.5.5-rc4.en.md)

## 0.5.4 — Native drawing export and measured verification

- Add the ninth MCP tool, `cad_export_drawing`, for first-angle A3 SLDDRW/PDF export, four views, a parameter schedule, source hash preservation and native reopen checks.
- Recover omitted feature dimensions, deduplicate parameters and distinguish millimeters, degrees and unitless pattern counts.
- Preserve rotated PDF dimension labels, OCR conflict evidence and coordinate-frame boundaries.
- Include the previously local 0.5.2/0.5.3 source binding, measured cylinder and local surface checks, checkpoint recovery and chamfer parameter fixes.
- Add focused core and drawing regressions; retain the existing public synthetic modeling cases.
- Ship separate Chinese/English documentation, complete example assets and a version-independent isolated installer verification script.

The 14-reference development corpus uses model-derived drawings; it does not establish blind drawing-to-model reconstruction accuracy. Native export currently supports saved parts, A3 sheets, recognized English/Chinese standard views and up to 44 source parameters.

## 0.5.1 — First public GitHub release

- Publish the local typed SolidWorks MCP server and executor source, with eight public modeling tools.
- Include native part and assembly workflows, model inspection, image/PDF input and STEP/STL output.
- Add Chinese installation and build instructions, MIT license and third-party notices.
- Build the runtime from source and distribute a Windows ZIP with a local Codex installer and SHA-256 checksums.
- Discover local SolidWorks interop components during installation; proprietary interop DLLs are not redistributed.
- Keep source, tests and plugin instructions in Git. Keep runtime binaries in Releases, and retain private drawings, CAD files, backups and historical machine reports outside the public repository.
- Correct the public smoke test to expect the current eight tools and decouple public tests from private fixtures.

Known limitations and the exact release validation scope are documented in [docs/validation.md](docs/validation.md).

## 0.6.0+modeling.20261001.1 (local)

Typed advanced feature controls, native spatial curves/helices, support-face edge Fill with retained-continuity checks, geometry selection filters and source-derived edge/seam verification. Selected native fixtures were created, rebuilt and saved; Curvature Fill requests in planar/cylindrical fixtures are rejected because requested controls are not retained. No complete G2 or standards-compliant thread claim. Local delivery only.

## 0.6.0+modeling.20261001.2 (local)

Reject nonfinite native selection measurements and bound advanced pattern/twist requests. Final modeling upgrade build.

## 0.6.0+modeling.20261001.3 (local)

Two missing persistent references cannot prove native entity identity; prevents false support-face adjacency and boundary ownership. Final identity regression added.
