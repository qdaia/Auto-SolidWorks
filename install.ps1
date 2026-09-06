param([string]$SolidWorksInteropDir, [switch]$SkipCodex)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$plugin = Join-Path $root 'plugins\auto-solidworks'
$runtime = Join-Path $plugin 'runtime'
foreach ($relative in @('mcp\AutoSolidWorks.ModelingMcp.exe','executor\CadModeling.Executor.SolidWorks.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtime $relative))) {
        throw 'Compiled runtime is missing. Download the Windows plugin ZIP from Releases, or run scripts\build.ps1 first.'
    }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 9 Windows Desktop Runtime (x64) first.' }
$runtimes = & dotnet --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft.WindowsDesktop.App 9\.')) { throw 'The .NET 9 Windows Desktop Runtime is required.' }
$interop = & (Join-Path $root 'scripts\find-solidworks.ps1') -SolidWorksInteropDir $SolidWorksInteropDir
foreach ($name in @('SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll')) {
    $source = Join-Path $interop $name
    $target = Join-Path $runtime "executor\$name"
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Different local interop already exists at $target. Extract this release into a new directory."
        }
    } else { Copy-Item -LiteralPath $source -Destination $target }
}
if ($SkipCodex) { Write-Output 'Runtime prerequisites prepared. Codex configuration was not changed.'; return }
if (-not (Get-Command codex -ErrorAction SilentlyContinue)) { throw 'Codex CLI with plugin support is required. Install it, then run this script again.' }
& codex plugin marketplace add $root
if ($LASTEXITCODE -ne 0) { throw 'Codex marketplace registration failed.' }
& codex plugin add 'auto-solidworks@auto-solidworks-local'
if ($LASTEXITCODE -ne 0) { throw 'Codex plugin installation failed.' }
Write-Output 'Auto SolidWorks installed. Open a new Codex task to load the plugin.'
