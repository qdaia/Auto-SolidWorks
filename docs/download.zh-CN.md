# 下载 Auto SolidWorks Windows 插件

**简体中文** | [English](download.en.md) · [返回 README](../README.md)

当前版本：**26.10.07**。通过 Codex / MCP 在本机 SolidWorks 创建、检查和修改参数化零件与装配体，保存原生模型、STEP / STL 和工程图。

[Windows x64 安装包](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.07/auto-solidworks-26.10.07-windows-x64.zip) · [SHA-256 校验文件](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.07/auto-solidworks-26.10.07-windows-x64.zip.sha256) · [GitHub Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v26.10.07) · [新增内容](release-26.10.07.zh-CN.md)

## 安装

1. 准备 Windows x64、已激活的本机 SolidWorks、.NET 9 Windows Desktop Runtime x64，以及支持插件的 Codex。
2. 下载 `auto-solidworks-26.10.07-windows-x64.zip`，解压到长期保留的目录。
3. 在解压目录运行：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

4. 打开新的 Codex 聊天并启用 Auto SolidWorks。

安装器优先使用 Codex 桌面版内置 CLI。SolidWorks 位于自定义目录时，追加 `-SolidWorksInteropDir '你的 SolidWorks 目录'`。安装器从本机准备 Interop；详细说明见[安装指南](installation.md)。

## 校验与包内容

```powershell
Get-FileHash .\auto-solidworks-26.10.07-windows-x64.zip -Algorithm SHA256
Get-Content .\auto-solidworks-26.10.07-windows-x64.zip.sha256
```

核对两处摘要一致。ZIP 内另有逐文件 `SHA256SUMS.json`，并包含运行程序、18 个 MCP 工具、使用技能、安装脚本、文档和许可声明。

Gordon 工具另需本地 FreeCAD；OCR 可选 Tesseract，PDF 栅格化可选 Poppler。源代码与测试另见仓库或 Release 的 `source-tests.zip`；直接安装请选择 Windows ZIP。
