# 下载 Auto SolidWorks 1025

**简体中文** | [English](download.en.md) · [返回 README](../README.md)

当前版本：**1025**，发布日期：2026年10月25日。

[Windows x64 安装包](https://github.com/qdaia/Auto-SolidWorks/releases/download/v1025/auto-solidworks-1025-windows-x64.zip) · [SHA-256](https://github.com/qdaia/Auto-SolidWorks/releases/download/v1025/auto-solidworks-1025-windows-x64.zip.sha256) · [源码包](https://github.com/qdaia/Auto-SolidWorks/releases/download/v1025/auto-solidworks-1025-source.zip) · [Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v1025) · [更新说明](release-1025.zh-CN.md)

准备 Windows x64、已激活的本机 SolidWorks、.NET 9 Windows Desktop Runtime x64 和支持插件的 Codex。核对SHA-256，解压到长期保留的目录后运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

打开新的 Codex 聊天并启用 Auto SolidWorks。自定义SolidWorks路径使用 -SolidWorksInteropDir 参数，详见[安装说明](installation.md)。

ZIP包含运行时、技能、文档、许可与逐文件SHA256SUMS.json；Interop从本机SolidWorks准备。AI实际读图，不运行OCR。PDF栅格化及可选曲面工具依其各自本机依赖。默认四步工作流，不自动追加独立交付审核。
