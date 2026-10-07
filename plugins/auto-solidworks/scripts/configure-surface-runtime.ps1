param([Parameter(Mandatory=$true)][string]$PythonExecutable)
$ErrorActionPreference = 'Stop'
$pythonPath = [System.IO.Path]::GetFullPath($PythonExecutable)
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) { throw 'FreeCAD Python 可执行文件不存在。' }
& $pythonPath -c 'import FreeCAD, Part, numpy; print(FreeCAD.Version()); print(Part.OCC_VERSION)'
if ($LASTEXITCODE -ne 0) { throw '此 Python 无法加载 FreeCAD、Part 和 numpy。' }
$configDirectory = Join-Path $env:LOCALAPPDATA 'AutoSolidWorks\dependencies'
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
$configPath = Join-Path $configDirectory 'surface-runtime.json'
if (Test-Path -LiteralPath $configPath) {
    Copy-Item -LiteralPath $configPath -Destination ($configPath + '.backup-' + [Guid]::NewGuid().ToString('N'))
}
@{ python_executable = $pythonPath } | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
Write-Output "本地 Gordon 曲面运行环境已配置：$configPath"
