# 开发与验证

## 构建

需要 Git、.NET 9 SDK、Windows，以及本机 SolidWorks interop 组件。Python 3.10+ 用于测试和 ZIP 打包。

```powershell
git clone https://github.com/qdaia/Auto-SolidWorks.git
Set-Location Auto-SolidWorks
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

非标准安装位置可传入 `-SolidWorksInteropDir`，也可以设置 `SOLIDWORKS_INTEROP_DIR`。构建结果写入 `plugins/auto-solidworks/runtime/`；默认框架依赖部署，不包含 .NET 运行时。NuGet 恢复需要网络，CAD 执行使用本机 SolidWorks。

也可直接构建项目，并通过 MSBuild 属性覆盖位置：

```powershell
dotnet build .\src\CadModeling.Executor.SolidWorks\CadModeling.Executor.SolidWorks.csproj -c Release '-p:SolidWorksInteropDir=C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS'
```

## 非 CAD 烟雾检查

```powershell
python .\tests\smoke.py "$((Resolve-Path .\plugins\auto-solidworks).Path)" --report "$((Get-Location).Path)\artifacts\smoke\report.json"
```

检查 9 个公开工具、能力查询、文字和 typed draft 编译、默认执行参数、可选 dry-run、错误几何拒绝、合成图片页面与线条读取，以及 dry-run 不产生原生模型。该检查不证明实际 SolidWorks 建模或准确 OCR。

## 原生测试

下面的命令会实际启动或连接 SolidWorks，并创建测试模型：

```powershell
python .\tests\run_native.py --case box_fillet
```

运行前查看 `tests/cases.py` 获取实际样例名称。可以重复传入 `--case` 指定多个样例；不传筛选参数时运行现有零件样例。`--assembly`、`--references` 使用先前在 `artifacts/` 中生成的组件，请先生成依赖零件，见对应 Python 文件。焊件样例引用本机型材库，需要根据自己的 SolidWorks 安装调整。

公开仓库保留合成几何测试。私人图纸、真实任务 CAD 文件、机器安装历史和依赖旧备份的发布脚本未分发，因此不应把本仓库的测试集称为原开发目录的完整历史验收集。

## 发布包

```powershell
python .\scripts\package-release.py
```

输出 `dist/auto-solidworks-0.5.4-windows-x64.zip` 和 SHA-256 文件。ZIP 包含插件、运行程序、安装脚本、目录清单哈希和许可证。SolidWorks interop DLL 与 PDB 不进入 ZIP，interop 在使用者机器上通过 `install.ps1` 准备。

打包器拒绝覆盖已有 ZIP。源码更新后应先更新版本、构建和验证，再选择新的输出目录或版本发布。`bin/`、`obj/`、运行程序、模型产物和备份均被 Git 忽略。

在已有 SolidWorks、Codex CLI 和 .NET Runtime 的 Windows 机器上，可验证 ZIP 的独立安装流程：

```powershell
python .\scripts\verify-release.py .\dist\auto-solidworks-0.5.4-windows-x64.zip --workdir .\artifacts\release-check
```

工作目录必须尚不存在。该脚本使用隔离的 Codex 配置目录，核对 ZIP 和安装缓存哈希，再运行安装后烟雾检查；不会修改日常 Codex 的插件配置。

依赖升级后可使用 `python scripts/third-party-notices.py` 更新第三方清单。该维护脚本需要已登录的 GitHub CLI、恢复后的本地 NuGet 缓存和网络；普通安装不需要执行它。

## 0.5.4 聚焦回归

```powershell
dotnet run --project tests/FocusedCore/FocusedCore.csproj -c Release
dotnet run --project tests/DrawingRegression/DrawingRegression.csproj -c Release
python tests/run_native.py --contracts
```

分别运行 52 项核心检查、5 项不依赖私有数据的图纸检查和 7 项绑定契约检查。DrawingRegression 可选第一个参数为开发参考 PDF 目录，此时扩展至 23 项；参考文件不分发。`tests/focused_three.py`、`tests/local_geometry.py`、`tests/chamfer_regression.py` 提供来源验证、局部面和倒角回归，原生模式会连接本机 SolidWorks。
