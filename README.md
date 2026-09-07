# Auto SolidWorks

在本机 SolidWorks 中，通过自然语言或工程图创建和修改参数化零件、装配体，并保存原生模型与 STEP/STL。

Create and modify parametric parts and assemblies in your local SolidWorks installation from natural-language descriptions or engineering drawings, and save native models and STEP/STL exports.

**Windows · 本机 SolidWorks / Local SolidWorks · 默认 mm / mm by default · 8 个 MCP 工具 / 8 MCP tools**

[下载 Windows 插件 / Download](https://github.com/qdaia/Auto-SolidWorks/releases/latest) · [安装说明 / Installation](docs/installation.md) · [从源码构建 / Build from source](docs/development.md) · [功能参数 / Operation reference](plugins/auto-solidworks/skills/auto-solidworks/references/modeling-operations.md)

## 测试案例：空心轴 / Example: Hollow shaft

**PPL001M45.1-2 空心轴**：以多视图工程图为输入的复杂轴类零件建模案例，展示阶梯轴段、侧面长窗口、键槽、环槽及端面孔组等结构。

**PPL001M45.1-2 hollow shaft**: a complex shaft modeling example based on a multiview engineering drawing, showing stepped shaft sections, elongated side openings, keyways, annular grooves and end-face hole groups.

<table>
<tr>
<td align="center" width="33%"><a href="docs/examples/hollow-shaft/model-preview.png"><img src="docs/examples/hollow-shaft/model-preview.png" width="240" alt="空心轴等轴视图 / Hollow shaft, isometric view"></a><br><sub>等轴视图 / Isometric</sub></td>
<td align="center" width="33%"><a href="docs/examples/hollow-shaft/model-side.png"><img src="docs/examples/hollow-shaft/model-side.png" width="240" alt="空心轴侧向视图 / Hollow shaft, side view"></a><br><sub>侧向视图 / Side</sub></td>
<td align="center" width="33%"><a href="docs/examples/hollow-shaft/model-end.png"><img src="docs/examples/hollow-shaft/model-end.png" width="240" alt="空心轴端面视图 / Hollow shaft, end view"></a><br><sub>端面视图 / End</sub></td>
</tr>
</table>

点击缩略图查看原始大图。 / Click a thumbnail to view the original full-size image.

[查看案例说明 / Example details](docs/examples/hollow-shaft/README.md) · [查看原始 PDF 图纸 / Original PDF drawing](docs/examples/hollow-shaft/drawing.pdf)

<details>
<summary>展开查看输入工程图 / Expand to view the input drawing</summary>

![空心轴输入工程图，包含主视图、剖面、端视图及局部详图 / Hollow shaft input drawing with the main view, sections, end views and detail views](docs/examples/hollow-shaft/drawing-preview.png)

完整尺寸与技术要求请打开上方 PDF。

Open the PDF linked above for the complete dimensions and technical requirements.

</details>

本案例展示提供的图纸与模型效果；截图不作为全部尺寸、公差和图纸等价性已通过验证的证明。

This example presents the supplied drawing and model images. The screenshots do not establish that all dimensions, tolerances or equivalence to the drawing have been verified.

## 快速开始 / Quick start

1. 安装并激活本机 SolidWorks，安装 **.NET 9 Windows Desktop Runtime（x64）** 和支持插件的 Codex CLI。

   Install and activate SolidWorks locally, then install the **.NET 9 Windows Desktop Runtime (x64)** and a Codex CLI version with plugin support.

2. 从 Releases 下载 `auto-solidworks-0.5.1-windows-x64.zip`，解压到一个长期保留的目录。

   Download `auto-solidworks-0.5.1-windows-x64.zip` from Releases and extract it to a directory you will keep for ongoing use.

3. 在解压目录打开 PowerShell，运行：

   Open PowerShell in the extracted directory and run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

4. 在 Codex 新任务中选择 **Auto SolidWorks**，描述尺寸或提供图纸：

   Select **Auto SolidWorks** in a new Codex task, then describe the dimensions or provide a drawing:

   > 创建一个 80 × 50 × 10 mm 的矩形板，中心有一个直径 10 mm 的通孔，保存 SLDPRT 和 STEP。
   >
   > Create an 80 × 50 × 10 mm rectangular plate with a centered through-hole 10 mm in diameter, and save SLDPRT and STEP files.

安装脚本从你的本机 SolidWorks 安装目录读取 interop 组件，再注册本地插件源。插件包不分发 SolidWorks 程序或 interop DLL。安装路径无法自动发现时，使用 `-SolidWorksInteropDir`，见[安装说明](docs/installation.md)。

The installer reads the interop components from your local SolidWorks installation and then registers a local plugin marketplace. The plugin package does not distribute SolidWorks software or interop DLLs. If the installation directory cannot be detected automatically, use `-SolidWorksInteropDir`; see the [installation guide](docs/installation.md).

GitHub 自动提供的 **Source code (zip)** 是源码，未包含编译后的运行程序。源码用户先执行构建步骤。直接把本仓库添加为远程插件源也不会自动下载运行程序；普通用户请使用 Release ZIP。

GitHub's automatically generated **Source code (zip)** contains source files without the compiled runtime. Source users must build the project first. Adding this repository directly as a remote plugin marketplace does not automatically download the runtime either; use the Release ZIP for installation.

## 能做什么 / Capabilities

| 范围 / Area | 功能 / Features |
|---|---|
| 草图 / Sketches | 矩形、圆、圆弧、多边形、槽、椭圆、样条、约束与驱动尺寸<br>Rectangles, circles, arcs, polygons, slots, ellipses, splines, constraints and driving dimensions |
| 零件 / Parts | 拉伸、切除、旋转、孔、圆角、倒角、抽壳、拔模、肋、放样与扫描<br>Extrusions, cuts, revolves, holes, fillets, chamfers, shells, drafts, ribs, lofts and sweeps |
| 重复与多实体 / Repetition and multibody | 阵列、镜像、布尔运算、分割、移动与复制<br>Patterns, mirrors, Boolean operations, splits, moves and copies |
| 钣金、焊件、曲面 / Sheet metal, weldments and surfaces | 基体与法兰、展开、型材构件与裁剪、曲面与加厚<br>Base features and flanges, flattening, structural members and trimming, surfaces and thickening |
| 装配 / Assemblies | 插入零部件、配置与位姿、几何配合、干涉检查<br>Component insertion, configurations and placement, geometric mates and interference checks |
| 检查与修改 / Inspection and editing | 特征、尺寸、配置、实体几何、原生零件副本修改、STEP 导入检查<br>Features, dimensions, configurations, body geometry, editing copies of native parts and STEP import inspection |
| 图纸输入 / Drawing input | 本地图片与 PDF、OCR 候选、分区/旋转提示、尺寸绑定检查<br>Local images and PDFs, OCR candidates, region/rotation hints and dimension-binding checks |
| 文件输出 / File output | SLDPRT、SLDASM、STEP/STP、STL<br>SLDPRT, SLDASM, STEP/STP and STL |

工具流程：`cad_get_capabilities → cad_read_drawing（有图时）→ cad_create_model_plan → cad_build_model → cad_inspect_model`。

Tool workflow: `cad_get_capabilities → cad_read_drawing (if a drawing is provided) → cad_create_model_plan → cad_build_model → cad_inspect_model`.

装配、型材与连接诊断分别使用 `cad_build_assembly`、`cad_list_weldment_profiles`、`cad_executor_health`。计划直接传递 typed draft / IR；`dryRun` 可选，默认建模执行。不会通过任意 shell 或脚本执行接口操作 CAD。

Assembly building, profile discovery and connection diagnostics use `cad_build_assembly`, `cad_list_weldment_profiles` and `cad_executor_health`, respectively. Plans pass typed drafts / IR directly; `dryRun` is optional, and modeling executes by default. CAD is not controlled through an arbitrary shell or script execution interface.

## 运行方式与范围 / Runtime and limitations

- CAD 执行、图纸预处理与文件保存在本机进行。Codex 或其他 AI 客户端如何处理提示、图纸和模型提供商请求，取决于该客户端及其设置；本项目不把云端 AI 会话描述为完全离线。

  CAD execution, drawing preprocessing and file storage run locally. How Codex or another AI client handles prompts, drawings and requests to model providers depends on that client and its settings; this project does not describe cloud AI sessions as fully offline.

- 未声明单位时使用 mm。关键尺寸缺失或冲突时需要澄清；OCR 数字需要结合原图判断。

  Dimensions default to mm when no unit is specified. Missing or conflicting critical dimensions require clarification; OCR numbers must be checked against the original drawing.

- `cad_executor_health` 和实际建模可能启动 SolidWorks。默认创建新文件，不覆盖已有模型。

  `cad_executor_health` and actual modeling may start SolidWorks. New files are created by default, without overwriting existing models.

- 当前版本未生成物理螺旋螺纹、原生工程图纸页，也不完整解释任意 GD&T。Tapped 孔使用底孔和原生装饰螺纹。

  The current version does not generate physical helical threads or native drawing sheets, and does not fully interpret arbitrary GD&T. Tapped holes use tap-drill holes and native cosmetic threads.

- 功能族不代表支持其中每一种 SolidWorks 选项。几何创建、重建和测量成功不能自动证明与任意工程图等价。

  Support for a feature family does not imply support for every SolidWorks option within it. Successful geometry creation, rebuilding and measurement do not automatically establish equivalence to an arbitrary engineering drawing.

- 本机开发验证使用 SolidWorks 2025；其他版本需要自行验证。此次公开发布的检查范围见[发布验证](docs/validation.md)。

  Local development validation used SolidWorks 2025; other versions require separate verification. See [release validation](docs/validation.md) for the checks performed for this public release.

## 开发与反馈 / Development and feedback

源码位于 `src/`，插件内容位于 `plugins/auto-solidworks/`。构建、非 CAD 烟雾检查和原生测试见[开发说明](docs/development.md)。报告问题时请附上插件版本、SolidWorks 版本、最小尺寸描述和错误信息。

Source code is in `src/`, and plugin files are in `plugins/auto-solidworks/`. See the [development guide](docs/development.md) for builds, non-CAD smoke checks and native tests. When reporting an issue, include the plugin version, SolidWorks version, a minimal dimensioned description and the error message.

## 许可证 / License

项目源码采用 [MIT License](LICENSE)。第三方组件适用各自许可证，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。SolidWorks 是外部专有依赖，需要使用者自己的安装与许可。本项目独立开发，不代表 Dassault Systèmes 或 SOLIDWORKS 官方。

The project source code is licensed under the [MIT License](LICENSE). Third-party components retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). SolidWorks is an external proprietary dependency and requires the user's own installation and license. This is an independent project and does not represent Dassault Systèmes or the official SOLIDWORKS organization.
