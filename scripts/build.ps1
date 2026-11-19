param([string]$SolidWorksInteropDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$interop = & (Join-Path $PSScriptRoot 'find-solidworks.ps1') -SolidWorksInteropDir $SolidWorksInteropDir
# A trailing backslash can escape the closing native-command quote in Windows PowerShell.
$interop = $interop.TrimEnd([char]'\', [char]'/')
$runtime = Join-Path $root 'plugins\auto-solidworks\runtime'
$interopVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $interop 'SolidWorks.Interop.sldworks.dll')).Version
# Redistributable variants can share an assembly version but have different bytes.
$sldHash = (Get-FileHash -LiteralPath (Join-Path $interop 'SolidWorks.Interop.sldworks.dll')).Hash.Substring(0,16)
$constHash = (Get-FileHash -LiteralPath (Join-Path $interop 'SolidWorks.Interop.swconst.dll')).Hash.Substring(0,16)
$artifacts = Join-Path $root "artifacts\build\$interopVersion-$sldHash-$constHash"
$projects = @(
    @{File='src\AutoSolidWorks.ModelingMcp\AutoSolidWorks.ModelingMcp.csproj'; Output='mcp'},
    @{File='src\CadModeling.Executor.SolidWorks\CadModeling.Executor.SolidWorks.csproj'; Output='executor'}
)
foreach ($project in $projects) {
    $publish = Join-Path $artifacts ("publish\" + $project.Output)
    $target = Join-Path $runtime $project.Output
    & dotnet publish (Join-Path $root $project.File) --configuration Release --artifacts-path $artifacts -m:1 -nodeReuse:false --no-self-contained --output $publish "-p:SolidWorksInteropDir=$interop" '-p:DebugType=none' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($project.File)" }
    if ($project.Output -eq 'executor') {
        foreach ($name in 'SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll') {
            if ((Get-FileHash -LiteralPath (Join-Path $publish $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $interop $name)).Hash) {
                throw "Published $name differs from the selected SolidWorks installation."
            }
        }
    }
    $null = New-Item -ItemType Directory -Path $target -Force
    Copy-Item -Path (Join-Path $publish '*') -Destination $target -Recurse -Force
}
& python (Join-Path $PSScriptRoot 'record-build-identity.py')
if ($LASTEXITCODE -ne 0) { throw 'Could not record runtime/source identity.' }
Write-Output "Plugin built: $(Join-Path $root 'plugins\auto-solidworks')"
