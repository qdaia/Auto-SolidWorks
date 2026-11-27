[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReferenceRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int[]]$Years = (2018..2027)
)
# No native CAD commands: NativeLifetimeRegression uses a blocking fake executor.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$refs = (Resolve-Path -LiteralPath $ReferenceRoot).Path
$out = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $out
$rows = foreach ($year in $Years) {
    $dir = Join-Path $refs $year
    $stage = Join-Path $out $year
    $null = New-Item -ItemType Directory -Path $stage
    $row = [ordered]@{year=$year; status='missing_references'; references=@(); fake_checks=0; adapter_checks_passed=$false; error=$null}
    try {
        if (Test-Path -LiteralPath $dir) {
            $row.status = 'failed'
            foreach ($id in 'sldworks','swconst') {
                $file = Join-Path $dir "SolidWorks.Interop.$id.dll"
                $identity = [Reflection.AssemblyName]::GetAssemblyName($file)
                $key = if ($id -eq 'sldworks') { '7c4797c3e4eeac03' } else { '19f43e188e4269d8' }
                if ($identity.Version.Major -ne $year - 1992 -or $identity.Name -ne "SolidWorks.Interop.$id" -or
                    [BitConverter]::ToString($identity.GetPublicKeyToken()).Replace('-','').ToLowerInvariant() -ne $key) {
                    throw "Unexpected $year reference identity: $identity"
                }
                $row.references += [ordered]@{name=$identity.Name; version=$identity.Version.ToString();
                    identity=$identity.FullName; sha256=(Get-FileHash -LiteralPath $file).Hash}
            }
            $project = Join-Path $root 'tests\NativeLifetimeRegression\NativeLifetimeRegression.csproj'
            & dotnet build $project -c Release --artifacts-path $stage "-p:SolidWorksInteropDir=$dir" -p:NuGetAudit=false --nologo -v:q *> (Join-Path $stage 'build.log')
            if ($LASTEXITCODE) { throw 'Build failed; see build.log.' }
            $bin = Join-Path $stage 'bin\CadModeling.Executor.SolidWorks\release'
            $executor = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $bin 'CadModeling.Executor.SolidWorks.dll')))
            $references = $executor.GetReferencedAssemblies()
            foreach ($expected in $row.references) {
                $match = @($references | Where-Object FullName -eq $expected.identity)
                $copied = Get-FileHash -LiteralPath (Join-Path $bin ($expected.name + '.dll'))
                if ($match.Count -ne 1 -or $copied.Hash -ne $expected.sha256) { throw "Wrong emitted reference: $($expected.name)" }
            }
            $test = Join-Path $stage 'bin\NativeLifetimeRegression\release\NativeLifetimeRegression.dll'
            $evidence = Join-Path $stage 'fake-port'
            & dotnet $test $evidence *> (Join-Path $stage 'fake-port.log')
            if ($LASTEXITCODE) { throw 'Fake executor regression failed; see fake-port.log.' }
            $result = Get-Content -LiteralPath (Join-Path $evidence 'summary.json') -Raw | ConvertFrom-Json
            if ($result.status -ne 'pass' -or $result.solidworks_started -ne $false -or $result.passed -lt 8) {
                throw 'Incomplete fake-port receipt.'
            }
            $row.fake_checks = $result.passed
            $adapter = Join-Path $root 'tests\InteropAdapterRegression\InteropAdapterRegression.csproj'
            & dotnet build $adapter -c Release --artifacts-path $stage "-p:SolidWorksInteropDir=$dir" -p:NuGetAudit=false --nologo -v:q *> (Join-Path $stage 'adapter-build.log')
            if ($LASTEXITCODE) { throw 'Interop adapter build failed; see adapter-build.log.' }
            $adapterDll = Join-Path $stage 'bin\InteropAdapterRegression\release\InteropAdapterRegression.dll'
            & dotnet $adapterDll *> (Join-Path $stage 'adapter.log')
            if ($LASTEXITCODE) { throw 'Interop adapter regression failed; see adapter.log.' }
            $row.adapter_checks_passed = $true
            $row.status = 'passed'
        }
    } catch {
        $row.error = $_.Exception.Message
    }
    Write-Host "$year $($row.status)"
    [pscustomobject]$row
}
ConvertTo-Json -Depth 6 -InputObject @($rows) | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $out 'matrix.json')
if (@($rows | Where-Object status -ne 'passed').Count) { exit 1 }
