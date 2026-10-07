# 开发与验证

[English](development.en.md)

## 从源码构建 26.10.07

需要 Windows、.NET 9 SDK、Python 3.10+，以及本机已授权的 SolidWorks Interop 组件。

```powershell
git clone https://github.com/qdaia/Auto-SolidWorks.git
Set-Location Auto-SolidWorks
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

自定义目录可传入 `-SolidWorksInteropDir` 或设置 `SOLIDWORKS_INTEROP_DIR`。构建生成 `plugins/auto-solidworks/runtime/` 及 `build-identity.json`，源码与运行文件通过 SHA-256 关联。NuGet 恢复需要网络，CAD 执行在本机完成。

## 完整离线检查

构建后，选择一个尚不存在的输出目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-local-integration-offline.ps1 -OutputDirectory .\artifacts\offline-26.10.07
```

脚本顺序运行 12 个回归套件、文档合同检查和公开 MCP 编译／预演。涉及原生适配器的模拟检查需要本机 Interop，但不会启动 SolidWorks。此版整合验证通过 1,101 项离线检查，安装后的公开接口另通过 128 项检查。

MCP 检查可以单独运行：

```powershell
python scripts/verify-local-integration-mcp.py artifacts/mcp-26.10.07
```

输入位于 `tests/fixtures/local-integration/`。受控历史测试创建明确标注的路径占位文件，执行器被禁止启动；占位文件只验证输入路径与计划，不提供原生 CAD 证据。

## GitHub 自动检查

`.github/workflows/ci.yml` 构建 MCP、检查公开目录和版本合同，并顺序运行 9 个不需要 SolidWorks Interop 的托管回归套件。原生案例和保存重开证据按其原始构建身份记录，云端检查不认证实际 CAD 输出。

## 打包与隔离安装

```powershell
python scripts/package-release.py --output dist/26.10.07
python scripts/verify-release.py dist/26.10.07/auto-solidworks-26.10.07-windows-x64.zip --workdir artifacts/isolated-install-26.10.07
```

打包器拒绝覆盖已有 ZIP。包内含运行程序、技能、文档、安装器和逐文件哈希；SolidWorks Interop 与 PDB 不随包分发。隔离验证使用独立 Codex 配置目录，核对文件哈希和安装后的 MCP 接口，不修改日常插件配置。

`src/`、当前回归项目、公开合成输入和构建脚本随源码提供。私有图纸、CAD 产物、机器日志、历史原生存储、密钥与备份保留在本机。原有公开测试仍保留供历史版本参考，当前自动检查入口以本页为准。
