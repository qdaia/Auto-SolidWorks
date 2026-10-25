# Auto SolidWorks

**版本 1025 · 2026-10-25 · Windows x64 · Codex / MCP**

把工程图与尺寸描述转成本机 SolidWorks 参数化模型。默认按 **识图 → 尺寸绑定 → 编译 → 建模** 四步执行，保存原生文件。

**简体中文** | [English](README.en.md)

[下载 Windows 插件](docs/download.zh-CN.md) · [本次更新](docs/release-1025.zh-CN.md) · [GitHub Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v1025) · [验证范围](docs/release-validation-1025.md)

1025同步本机vision.9，包含源图几何候选、批量尺寸绑定、角度表达式、页面校准及耳板镜像方向检查。源图语义仍由AI复核。发布验证与原生CAD验收分别记录；本次未重新运行原生建模。

## 从输入到交付

提供工程图片、PDF 或明确尺寸，插件将输入整理为可检查的建模计划，再通过本机 SolidWorks 执行。你可以继续修改驱动尺寸，检查几何与装配关系，并保存可编辑的原生文件。

| 工作内容 | 提供的能力与输出 |
| --- | --- |
| 参数化零件 | 草图与约束、拉伸、旋转、孔、圆角倒角、扫描、放样、阵列、多实体、钣金和焊件；SLDPRT / STEP / STL |
| 曲面与空间曲线 | Boundary / Fill / Sweep / Offset 的类型化控制、三维曲线与螺旋线；本地 Gordon 曲线网格与 NURBS 文件 |
| 设计参数 | 全局变量、尺寸方程、配置表达式、单位检查、特征抑制及保存后的读回 |
| 既有模型 | 副本修改、持久引用、局部及整件几何检查、限定范围的拓扑历史与修复回执 |
| 装配体 | 组件、配合、干涉检查、限定空间树机构自由度检查；SLDASM |
| 工程图 | 第一角投影视图、原生尺寸导入、视图布局与分页；SLDDRW / PDF |
| 执行管理 | 状态查询、暂停、截止时间、跨进程租约、检查点与恢复身份核对 |

## 测试案例

### 拱形支座：建模过程

根据三视图与尺寸创建拱形底座、立板、圆柱凸台、通孔、半圆槽和 R3 圆角。

<p>
<a href="docs/examples/arch-support/modeling-process.gif"><img src="docs/examples/arch-support/modeling-process.gif" width="800" alt="拱形支座的 SolidWorks 原生建模过程"></a>
</p>

### 空心轴

下图为历史示例。本次两版从零对照均未完成曲面 M20 螺纹，不能将历史截图作为本版完整验收证据。

<picture><img src="docs/examples/hollow-shaft/model-preview.png" width="480" alt="空心轴等轴视图"></picture>

<picture><source srcset="docs/examples/hollow-shaft/drawing-preview.svg" type="image/svg+xml"><img src="docs/examples/hollow-shaft/drawing-preview.png" width="1200" alt="空心轴工程图 PDF 矢量预览"></picture>

工程图 PDF：`docs/examples/hollow-shaft/drawing.pdf`

## 快速开始

1. 准备 Windows x64、已激活的本机 SolidWorks、[.NET 9 Windows Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)，以及支持插件的 Codex。
2. 下载并解压 [1025 Windows 安装包](https://github.com/qdaia/Auto-SolidWorks/releases/download/v1025/auto-solidworks-1025-windows-x64.zip)，在解压目录运行：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

3. 打开新的 Codex 聊天，启用 **Auto SolidWorks**，给出尺寸或工程图：

   > 创建一个 80 × 50 × 10 mm 的矩形安装板，中心有直径 10 mm 的通孔，四角做 R2 圆角，保存 SLDPRT 和 STEP。

安装器优先使用 Codex 桌面版自带 CLI，并从本机 SolidWorks 安装目录准备 Interop。详细步骤及自定义目录见[中文下载与安装说明](docs/download.zh-CN.md)。

## 本地运行与能力查询

CAD 执行、原生文件和几何检查在本机完成。SolidWorks 由使用者安装并授权；Gordon 另需本地 FreeCAD。图片 / PDF 的理解使用你选择的模型与服务，OCR 和 PDF 栅格化依赖可按需配置。

<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->
本地版本：`1025`；修订：`source-four-stage-2`。默认仅识图、尺寸绑定、编译、建模四步；保留真实参数/单位/源图端点检查和建模保存，移除强制复核台账、分区探针、预览与独立交付审核。历史草案仍按原修订校验。不认证完整图纸/BREP/GD&T等价。详见[能力清单](plugins/auto-solidworks/skills/auto-solidworks/references/capability-manifest.json)。
<!-- Capability manifest SHA256: 203b2763562d8e401f8fccc85fec2c8f76c172b10604f28edb64105bb18c7cb1 -->
<!-- AUTO-SOLIDWORKS-CONTRACT:END -->

[功能参数](plugins/auto-solidworks/skills/auto-solidworks/references/modeling-operations.md) · [Gordon 使用](plugins/auto-solidworks/skills/auto-solidworks/references/gordon-surface.md) · [曲面控制](plugins/auto-solidworks/skills/auto-solidworks/references/surface-modeling.md) · [几何验证](plugins/auto-solidworks/skills/auto-solidworks/references/local-geometry-verification.md)

## 开发与反馈

[源码构建与测试](docs/development.md) · [架构说明](docs/architecture.md) · [第三方声明](THIRD_PARTY_NOTICES.md) · [提交问题](https://github.com/qdaia/Auto-SolidWorks/issues)

项目使用 MIT 许可证，见 [LICENSE](LICENSE)。SolidWorks、FreeCAD 及其他第三方组件遵循各自的授权与许可。
