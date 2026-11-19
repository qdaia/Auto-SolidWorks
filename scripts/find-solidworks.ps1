param([string]$SolidWorksInteropDir, [version]$RequiredVersion, [version]$RequiredConstantsVersion)
$ErrorActionPreference = 'Stop'
$candidates = [System.Collections.Generic.List[string]]::new()
if ($SolidWorksInteropDir) {
    foreach ($dll in 'SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $SolidWorksInteropDir $dll) -PathType Leaf)) {
            throw "Missing $dll in the requested SolidWorksInteropDir: $SolidWorksInteropDir"
        }
    }
    return (Resolve-Path -LiteralPath $SolidWorksInteropDir).Path
}
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
        if ($RequiredVersion) {
            $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $candidate 'SolidWorks.Interop.sldworks.dll')).Version
            if ($version.Major -ne $RequiredVersion.Major -or $version -lt $RequiredVersion) { continue }
        }
        if ($RequiredConstantsVersion) {
            $constants = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $candidate 'SolidWorks.Interop.swconst.dll')).Version
            if ($constants.Major -ne $RequiredConstantsVersion.Major -or $constants -lt $RequiredConstantsVersion) { continue }
        }
        return (Resolve-Path -LiteralPath $candidate).Path
    }
}
if ($RequiredVersion) {
    throw "SW_INTEROP_MISMATCH: No compatible installed Interop for $RequiredVersion. Pass -SolidWorksInteropDir to select the target installation."
}
throw 'SolidWorks interop assemblies were not found. Pass -SolidWorksInteropDir with your local SOLIDWORKS installation directory, or set SOLIDWORKS_INTEROP_DIR.'
