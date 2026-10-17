$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$env:CAD_SOLIDWORKS_EXECUTOR = Join-Path $pluginRoot 'runtime\executor\CadModeling.Executor.SolidWorks.exe'
$pipeHasher = [System.Security.Cryptography.SHA256]::Create()
try {
    $runtimeIdentity = $pluginRoot.ToLowerInvariant()
    foreach ($runtimeName in @('CadModeling.Executor.SolidWorks.dll','CadModeling.Core.dll','CadModeling.Ir.dll')) {
        $runtimeBytes = [System.IO.File]::ReadAllBytes((Join-Path $pluginRoot ('runtime\executor\'+$runtimeName)))
        $runtimeIdentity += '|' + $runtimeName + ':' + [System.BitConverter]::ToString($pipeHasher.ComputeHash($runtimeBytes))
    }
    $pipeBytes = $pipeHasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($runtimeIdentity))
    $pipeSuffix = ([System.BitConverter]::ToString($pipeBytes)).Replace('-', '').Substring(0, 16).ToLowerInvariant()
} finally { $pipeHasher.Dispose() }
$env:CAD_SOLIDWORKS_PIPE = 'auto-solidworks-26-10-13-opt1-' + $pipeSuffix
# Raster text and dimension interpretation are performed by the connected AI.
# Do not load OCR configuration or provision a local OCR engine.
if (-not $env:CAD_EXECUTOR_LOG_DIR) {
    $env:CAD_EXECUTOR_LOG_DIR = Join-Path $env:LOCALAPPDATA 'AutoSolidWorks\logs'
}
& (Join-Path $pluginRoot 'runtime\mcp\AutoSolidWorks.ModelingMcp.exe')
exit $LASTEXITCODE
