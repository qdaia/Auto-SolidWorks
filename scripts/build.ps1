param([string]$SolidWorksInteropDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$interop = & (Join-Path $PSScriptRoot 'find-solidworks.ps1') -SolidWorksInteropDir $SolidWorksInteropDir
$runtime = Join-Path $root 'plugins\auto-solidworks\runtime'
$projects = @(
    @{File='src\AutoSolidWorks.ModelingMcp\AutoSolidWorks.ModelingMcp.csproj'; Output='mcp'},
    @{File='src\CadModeling.Executor.SolidWorks\CadModeling.Executor.SolidWorks.csproj'; Output='executor'}
)
foreach ($project in $projects) {
    & dotnet publish (Join-Path $root $project.File) --configuration Release --no-self-contained --output (Join-Path $runtime $project.Output) "-p:SolidWorksInteropDir=$interop" '-p:DebugType=none' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($project.File)" }
}
Write-Output "Plugin built: $(Join-Path $root 'plugins\auto-solidworks')"
