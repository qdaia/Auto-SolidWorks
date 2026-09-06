# Auto SolidWorks 0.5.1

首次公开发布：通过 Codex / MCP 在本机 SolidWorks 创建、检查和修改参数化零件及装配体，默认单位 mm，支持 SLDPRT、SLDASM、STEP/STP 和 STL。

## 下载与安装

下载本页附件 `auto-solidworks-0.5.1-windows-x64.zip` 并解压，在解压目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

需要 Windows x64、本机已激活的 SolidWorks、.NET 9 Windows Desktop Runtime x64，以及支持插件的 Codex CLI。安装后打开新的 Codex 任务。SHA-256 校验文件作为同名 `.zip.sha256` 附件提供。

SolidWorks interop DLL 由安装脚本从使用者本机安装目录复制。OCR 的 Tesseract 和 PDF 栅格化的 Poppler 按需另行安装。详细步骤见仓库 `docs/installation.md`。

GitHub 自动生成的 Source code 压缩包不含运行程序；直接使用请选择 Windows 插件 ZIP。

## 包含内容

- 8 个 MCP 工具，覆盖能力查询、图纸输入、计划编译、零件构建、模型检查、装配构建、型材发现和连接诊断。
- 原生特征、钣金、焊件、曲面、几何引用和已有模型副本修改工作流程。
- C# 源码、公开合成测试、中文说明、安装和构建脚本、MIT 许可证及第三方声明。

## 本次检查

- MCP、公共库和 SolidWorks 执行器构建成功。
- 8 项非 CAD 烟雾检查、7 项尺寸绑定检查通过。
- ZIP 完整性、隔离的 Codex 安装、安装缓存文件哈希和安装后烟雾检查通过。
- 36 个 C# 源文件与本机原开发版内容一致。

本次没有重跑完整 SolidWorks 原生建模矩阵。几何创建成功不代表自动证明任意图纸等价；当前不生成物理螺旋螺纹或原生工程图纸页，不完整解释任意 GD&T。测试范围详见仓库 `docs/validation.md`。
