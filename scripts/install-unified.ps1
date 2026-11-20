[CmdletBinding()]
param([string]$SolidWorksInteropDir, [switch]$SkipCodex)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    # A parent PowerShell Core host can export its incompatible module path.
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
}
$common = Join-Path $PSScriptRoot 'common'
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'executors.json') -Raw | ConvertFrom-Json
$dllNames = 'SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll'
# An explicit installation or environment setting takes precedence. For automatic
# discovery, try compatible registered installations from newest to oldest.
$interop = $null
if ($SolidWorksInteropDir -or $env:SOLIDWORKS_INTEROP_DIR) {
    $selectedDir = if ($SolidWorksInteropDir) { $SolidWorksInteropDir } else { $env:SOLIDWORKS_INTEROP_DIR }
    $interop = & (Join-Path $common 'scripts\find-solidworks.ps1') -SolidWorksInteropDir $selectedDir
} else {
    $finder = Join-Path $common 'scripts\find-solidworks.ps1'
    foreach ($entry in ($catalog.builds | Sort-Object { [version]$_.sldworks } -Descending)) {
        try {
            $interop = & $finder -RequiredVersion $entry.sldworks -RequiredConstantsVersion $entry.swconst
            break
        } catch {
            if ($_.Exception.Message -notlike 'SW_INTEROP_MISMATCH:*') { throw }
        }
    }
}
if (-not $interop) { throw 'SW_INTEROP_MISMATCH: No supported local SOLIDWORKS Interop pair was found. Use -SolidWorksInteropDir to select the installation.' }
$versions = foreach ($name in $dllNames) {
    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $interop $name))
    if ($assembly.Name -ne [IO.Path]::GetFileNameWithoutExtension($name)) { throw "Unexpected assembly: $name" }
    $assembly.Version
}
$match = $catalog.builds | Where-Object {
    $sw = [version]$_.sldworks
    $constants = [version]$_.swconst
    $sw.Major -eq $versions[0].Major -and $constants.Major -eq $versions[1].Major -and
        $versions[0] -ge $sw -and $versions[1] -ge $constants
} | Sort-Object { [version]$_.sldworks } -Descending | Select-Object -First 1
if (-not $match) {
    throw "SW_INTEROP_MISMATCH: No executor matches sldworks $($versions[0]) and swconst $($versions[1])."
}
if ($match.id -notmatch '^\d{4}(-SP[0-9]+)?$') { throw 'Invalid executor catalog.' }
$variant = Join-Path $PSScriptRoot ('executors\' + $match.id)
$hashes = foreach ($name in $dllNames) { (Get-FileHash -LiteralPath (Join-Path $interop $name)).Hash }
$local = Join-Path $PSScriptRoot 'local'
[IO.Directory]::CreateDirectory($local) | Out-Null
$target = Join-Path $local ($match.id + '-' + $hashes[0].Substring(0,10) + $hashes[1].Substring(0,10))
$lock = [IO.File]::Open((Join-Path $local 'install.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    if (-not (Test-Path -LiteralPath $target -PathType Container)) {
        $stage = Join-Path $local ([guid]::NewGuid().ToString('N') + '.partial')
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        Get-ChildItem -LiteralPath $common -Force | Copy-Item -Destination $stage -Recurse -Force
        $runtime = Join-Path $stage 'plugins\auto-solidworks\runtime'
        Copy-Item -LiteralPath (Join-Path $variant 'executor') -Destination $runtime -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $variant 'build-identity.json') -Destination $runtime -Force
        & (Join-Path $stage 'install.ps1') -SolidWorksInteropDir $interop -SkipCodex
        [IO.Directory]::Move($stage, $target)
    }
    Write-Output "Selected executor $($match.id) for SOLIDWORKS Interop $($versions[0]). Installation directory: $target"
    & (Join-Path $target 'install.ps1') -SolidWorksInteropDir $interop -SkipCodex:$SkipCodex
} finally {
    $lock.Dispose()
}
