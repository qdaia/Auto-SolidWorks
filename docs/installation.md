# 安装 Auto SolidWorks

## 运行要求

- Windows x64，已安装、激活并可交互启动的 SolidWorks。本机开发测试环境为 SolidWorks 2025。
- [.NET 9 Windows Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)。只安装普通 .NET Runtime 不足以运行 WPF 图像处理组件。
- 支持 `codex plugin` 的 Codex CLI，用于 Codex 插件注册。
- 可选：Tesseract 与语言数据用于 OCR；Poppler 的 `pdftoppm.exe` 用于 PDF 页面栅格化。它们不随本插件分发。

## 按 SolidWorks 年度版本构建和验证

从修订后的源码运行 `scripts\build.ps1 -SolidWorksInteropDir <目标版本的安装目录>`。目录须同时包含 `SolidWorks.Interop.sldworks.dll` 和 `SolidWorks.Interop.swconst.dll`。执行器按所引用的年度版本选择 COM 应用。安装器发现年度不匹配或本机引用低于构建版本时，会报告 `SW_INTEROP_MISMATCH`。1127 为统一 ZIP，在解压根目录运行 install.ps1 即可选择匹配的执行器。安装器会显示选中的构建和实际安装目录。

2018 和 2019 构建通过旧版质量属性接口读取实体体积和质心；截面交线检查使用临时实体副本和旧版配对交线接口。2018 构建无法验证同心配合旋转锁定，2018 SP2 之前的引用还缺少高精度射线检查接口。每个年度版本可用下面的脚本从 MCP 验证连接、矩形板建模、保存、重开和尺寸读回。请填写实际测试年度，并提供一个新的输出目录。

```powershell
python scripts/verify-native-compatibility.py 2018 C:\CADTests\sw2018-plate-01
```

### 离线验收

将两份 Interop DLL 放在按年度命名的目录下，例如 `C:\SWRefs\2018`。使用下面的命令编译各年度执行器，逐项比对输出程序集引用和 DLL 哈希，并通过模拟执行器检查超时、租约与回执。输出目录应为新目录。缺少年度引用或出现失败时，脚本仍写出完整的 `matrix.json` 并返回非零退出码。全程无需启动 SolidWorks。

```powershell
.\scripts\verify-interop-matrix.ps1 -ReferenceRoot C:\SWRefs -OutputDirectory C:\CADTests\offline-01
```

2027 的文档比对可用于发现接口变更；要完成年度二进制验收，仍需 35.x 的原始 Interop。边界曲面编辑器中使用原生界面命令的读回流程保留其 2025 SP0 限制。

## 恢复中断的健康检查

如服务仍在运行，可先用 `cad_execution_status` 和原请求 ID 查询结果。若原执行器已退出且缺少最终回执，保存工作，正常关闭 MCP 客户端与 SolidWorks，然后在安装器显示的实际安装目录运行以下命令。第一条只核对，第二条归档旧锁。自定义回执目录可用 `-ReceiptsDir` 指定。

```powershell
.\scripts\recover-health-lease.ps1 -RequestId <原请求ID>
.\scripts\recover-health-lease.ps1 -RequestId <原请求ID> -Apply
```

工具要求锁与回执都为 active、编号和 PID 一致、原 PID 已退出，且回执记录的是 health。它在持有文件句柄时将旧锁改名留档，保留原回执。建模请求、记录不完整和仍有进程持有锁的情况需要核对现场。

## Release ZIP 安装

下载 [Releases](https://github.com/qdaia/Auto-SolidWorks/releases) 中的 Windows ZIP，解压后保留完整目录结构。在该目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

自动发现 SolidWorks 目录失败时，指定自己的目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -SolidWorksInteropDir 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS'
```

脚本检查运行依赖，把本机 `SolidWorks.Interop.sldworks.dll` 和 `SolidWorks.Interop.swconst.dll` 复制到插件执行器目录，并运行：

```powershell
codex plugin marketplace add .
codex plugin add auto-solidworks@auto-solidworks-local
```

统一包传递实际安装目录的绝对路径，目录位于解压位置的 local 子目录中。此本地插件源名为 `auto-solidworks-local`。打开新的 Codex 任务，选择 Auto SolidWorks。安装脚本不启动 SolidWorks，也不创建或关闭 CAD 文档。

新版本请解压到新目录，运行新目录中的安装脚本。源码中的 `runtime/` 不由 Git 管理，克隆仓库后需要先构建，见[开发说明](development.md)。

## OCR 与 PDF

插件启动时可以读取 `%LOCALAPPDATA%\AutoSolidWorks\dependencies\ocr.json`：

```json
{
  "executable": "C:\\Program Files\\Tesseract-OCR\\tesseract.exe",
  "tessdata": "C:\\Program Files\\Tesseract-OCR\\tessdata"
}
```

上述目录只是示例，使用实际安装位置。可设置 `CAD_TESSERACT_PATH`、`CAD_TESSDATA_DIR`、`CAD_TESSERACT_LANG` 覆盖配置。默认配置流程使用 `eng+chi_sim`，需要相应语言文件；仅装英文数据时改为 `eng`。PDF 栅格化工具可以放在 PATH 中，或通过 `CAD_PDFTOPPM_PATH` 指定其绝对路径。环境变量改变后重启客户端。

## 其他 MCP 客户端

在解压目录运行 `install.ps1 -SkipCodex`，只准备本机依赖。然后将 stdio MCP 服务器指向 `plugins/auto-solidworks/scripts/run-auto-solidworks-mcp.ps1`。配置示例：

```json
{
  "mcpServers": {
    "auto-solidworks": {
      "command": "powershell.exe",
      "args": ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "C:\\Tools\\AutoSolidWorks\\plugins\\auto-solidworks\\scripts\\run-auto-solidworks-mcp.ps1"]
    }
  }
}
```

将路径改成实际解压目录。此配置只接入 MCP 工具；客户端若不自动加载 Codex 技能，还需要读取 `plugins/auto-solidworks/skills/auto-solidworks/SKILL.md` 中的建模说明。

## 故障定位

- `Compiled runtime is missing`：下载了源码压缩包；改用 Windows Release ZIP，或先构建源码。
- `WindowsDesktop.App 9` 缺失：安装 .NET 9 Windows Desktop Runtime x64。
- interop 组件未找到：通过 `-SolidWorksInteropDir` 指定包含两份 interop DLL 的本机目录。
- 看不到插件：核对 `codex plugin list`，然后打开新任务。
- 工具已加载但建模连接失败：先确认本机 SolidWorks 能正常打开，再使用 `cad_executor_health` 获取诊断；该工具可能启动 SolidWorks。
- 读图尺寸不可靠：提供更清晰的图纸、分区提示或关键尺寸。OCR 候选不应被当作图纸真值。
