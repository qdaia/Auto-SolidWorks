# 下载 Windows 插件

**简体中文** | [English](download.en.md) · [返回 README](../README.md)

## Auto SolidWorks 0.5.4

通过 Codex / MCP 在本机 SolidWorks 创建、检查和修改参数化零件及装配体,以实现自然语言或者工程图输入，SolidWorks零件图输出。
该插件依赖LLM识图能力，推荐使用GPT-6 Astra

## 下载与安装

[下载 Windows 插件 ZIP](https://github.com/qdaia/Auto-SolidWorks/releases/download/v0.5.4/auto-solidworks-0.5.4-windows-x64.zip) · [SHA-256 校验文件](https://github.com/qdaia/Auto-SolidWorks/releases/download/v0.5.4/auto-solidworks-0.5.4-windows-x64.zip.sha256) · [GitHub Release](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v0.5.4)

发送此消息至agent一键安装：帮我安装https://github.com/qdaia/Auto-SolidWorks

手动安装：下载上方链接中的 `auto-solidworks-0.5.4-windows-x64.zip` 并解压，在解压目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

需要 Windows x64、本机已激活的 SolidWorks、.NET 9 Windows Desktop Runtime x64，以及支持插件的 Codex CLI。安装后打开新的 Codex 任务。SHA-256 校验文件通过上方同名 `.zip.sha256` 链接提供。

SolidWorks interop DLL 由安装脚本从使用者本机安装目录复制。OCR 的 Tesseract 和 PDF 栅格化的 Poppler 按需另行安装。详细步骤见[安装说明](installation.md)。

GitHub 自动生成的 Source code 压缩包不含运行程序；直接使用请选择 Windows 插件 ZIP。

## 包含内容

- 9 个 MCP 工具，覆盖能力查询、图纸输入、计划编译、零件构建、模型检查、装配构建、型材发现、连接诊断和 SLDDRW/PDF 工程图导出。
- 原生特征、钣金、焊件、曲面、几何引用和已有模型副本修改工作流程。
- C# 源码、公开合成测试、中文说明、安装和构建脚本、MIT 许可证及第三方声明。
