param([string]$SolidWorksInteropDir)
$ErrorActionPreference = 'Stop'
$candidates = [System.Collections.Generic.List[string]]::new()
if ($SolidWorksInteropDir) { $candidates.Add($SolidWorksInteropDir) }
if ($env:SOLIDWORKS_INTEROP_DIR) { $candidates.Add($env:SOLIDWORKS_INTEROP_DIR) }
foreach ($base in @($env:ProgramW6432, $env:ProgramFiles)) {
    if ($base) { $candidates.Add((Join-Path $base 'SOLIDWORKS Corp\SOLIDWORKS')) }
}
$registryRoot = 'HKLM:\SOFTWARE\SolidWorks'
if (Test-Path -LiteralPath $registryRoot) {
    foreach ($key in @(Get-ChildItem -LiteralPath $registryRoot | Where-Object PSChildName -match '^SOLIDWORKS \d{4}$' | Sort-Object PSChildName -Descending)) {
        $setup = Join-Path $key.PSPath 'Setup'
        if (Test-Path -LiteralPath $setup) {
            $values = Get-ItemProperty -LiteralPath $setup
            foreach ($property in @('SolidWorks Folder','SolidWorksFolder')) {
                $value = $values.PSObject.Properties[$property]
                if ($value -and $value.Value) { $candidates.Add([string]$value.Value) }
            }
        }
    }
}
foreach ($candidate in $candidates) {
    if ((Test-Path -LiteralPath (Join-Path $candidate 'SolidWorks.Interop.sldworks.dll')) -and
        (Test-Path -LiteralPath (Join-Path $candidate 'SolidWorks.Interop.swconst.dll'))) {
        return (Resolve-Path -LiteralPath $candidate).Path
    }
}
throw 'SolidWorks interop assemblies were not found. Pass -SolidWorksInteropDir with your local SOLIDWORKS installation directory, or set SOLIDWORKS_INTEROP_DIR.'
