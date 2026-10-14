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
$ocrConfigPath = Join-Path $env:LOCALAPPDATA 'AutoSolidWorks\dependencies\ocr.json'
if (Test-Path -LiteralPath $ocrConfigPath) {
    $ocrConfig = Get-Content -LiteralPath $ocrConfigPath -Raw | ConvertFrom-Json
    if (-not $env:CAD_TESSERACT_PATH) { $env:CAD_TESSERACT_PATH = $ocrConfig.executable }
    if (-not $env:CAD_TESSDATA_DIR) { $env:CAD_TESSDATA_DIR = $ocrConfig.tessdata }
    if (-not $env:CAD_TESSERACT_LANG) { $env:CAD_TESSERACT_LANG = 'eng+chi_sim' }
}
if (-not $env:CAD_EXECUTOR_LOG_DIR) {
    $env:CAD_EXECUTOR_LOG_DIR = Join-Path $env:LOCALAPPDATA 'AutoSolidWorks\logs'
}
& (Join-Path $pluginRoot 'runtime\mcp\AutoSolidWorks.ModelingMcp.exe')
exit $LASTEXITCODE
