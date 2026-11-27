# Auto SolidWorks 1127 精简版

Windows x64 统一包覆盖 SolidWorks 2018–2026 九个年度，提供 11 个执行器构建。安装器根据本机 Interop 选用匹配的执行器。

## 本次更新

- MCP 保留 8 个工具，覆盖读图、查建模字段、编译、创建零件、查看模型、配合装配、查询执行状态和读取结果。
- 装配默认保存 SLDASM。通过装配计划的 `export_drawing: true` 仍可同时生成工程图。
- 独立工程图导出、Gordon 曲面、图像几何候选和专项验收工具从插件目录移除。Windows 安装包不再携带 OpenCV 或 FreeCAD 曲面工作器。
- 延续 1120 的版本匹配方案，包含 2018 早期版、SP2 和 SP5 构建，以及 2019–2026 各年度构建。使用随本机 SolidWorks 安装的 Interop。

## 下载与安装

解压 `auto-solidworks-1127-windows-x64.zip`，在解压根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

需要已安装并激活的 SolidWorks、.NET 9 Windows Desktop Runtime x64，以及支持插件的 Codex。[安装文档](installation.md)

## 验证范围

九个年度的 Interop 编译、模拟执行器和安装选择属于离线验证。Windows 本机实机验证范围以发布回执为准。2018 同心配合的旋转锁定读回、2018 SP2 之前的高精度射线接口，以及跨版本高级曲面仍受 [1120 已记录的接口范围](release-1120.zh-CN.md) 约束。
