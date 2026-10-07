param([Parameter(Mandatory=$true)][string]$OutputDirectory, [string]$SolidWorksInteropDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$interop = & (Join-Path $PSScriptRoot 'find-solidworks.ps1') -SolidWorksInteropDir $SolidWorksInteropDir
$interop = $interop.TrimEnd([char]'\', [char]'/')
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory to preserve prior evidence.' }
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location $root
try {
    foreach ($name in @('PhysicalThreadRegression','NativeLifetimeRegression','DesignFeatureRegression','DesignExpressionRegression','DesignIntentRegression','EngineeringHardeningRegression','SurfaceBoundaryRegression','OfflineBoundaryRegression','AssemblyMobilityRegression','SemanticTopologyRegression','FillSupportRegression','BoundaryAdapterRegression')) {
        & dotnet build "tests\$name\$name.csproj" -c Release -m:1 -nodeReuse:false "-p:SolidWorksInteropDir=$interop" -p:DebugType=none -p:DebugSymbols=false 2>&1 |
            Tee-Object -FilePath (Join-Path $output "$name-build.log")
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
    }
    & dotnet 'tests\PhysicalThreadRegression\bin\Release\net9.0\PhysicalThreadRegression.dll' (Join-Path $output 'physical-thread') 2>&1 | Tee-Object -FilePath (Join-Path $output 'physical-thread.log')
    if ($LASTEXITCODE -ne 0) { throw 'Physical thread regression failed.' }
    & dotnet 'tests\NativeLifetimeRegression\bin\Release\net9.0-windows\NativeLifetimeRegression.dll' (Join-Path $output 'native-lifetime') 2>&1 | Tee-Object -FilePath (Join-Path $output 'native-lifetime.log')
    if ($LASTEXITCODE -ne 0) { throw 'Native lifetime regression failed.' }
    & dotnet 'tests\DesignFeatureRegression\bin\Release\net9.0\DesignFeatureRegression.dll' (Join-Path $output 'features') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'features.log')
    if ($LASTEXITCODE -ne 0) { throw 'Feature suppression regression failed.' }
    & dotnet 'tests\DesignExpressionRegression\bin\Release\net9.0\DesignExpressionRegression.dll' (Join-Path $output 'expressions') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'expressions.log')
    if ($LASTEXITCODE -ne 0) { throw 'Design expression regression failed.' }
    & dotnet 'tests\DesignIntentRegression\bin\Release\net9.0\DesignIntentRegression.dll' (Join-Path $output 'design-intent') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'design-intent.log')
    if ($LASTEXITCODE -ne 0) { throw 'Design intent regression failed.' }
    & dotnet 'tests\EngineeringHardeningRegression\bin\Release\net9.0-windows\EngineeringHardeningRegression.dll' (Join-Path $output 'engineering') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'engineering.log')
    if ($LASTEXITCODE -ne 0) { throw 'Offline engineering regression failed.' }
    & dotnet 'tests\SurfaceBoundaryRegression\bin\Release\net9.0-windows\SurfaceBoundaryRegression.dll' (Join-Path $output 'curvature') --curvature-core 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'curvature.log')
    if ($LASTEXITCODE -ne 0) { throw 'Offline curvature regression failed.' }
    & dotnet 'tests\OfflineBoundaryRegression\bin\Release\net9.0\OfflineBoundaryRegression.dll' (Join-Path $output 'text') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'text.log')
    if ($LASTEXITCODE -ne 0) { throw 'Offline text regression failed.' }
    & dotnet 'tests\AssemblyMobilityRegression\bin\Release\net9.0\AssemblyMobilityRegression.dll' (Join-Path $output 'mobility') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'mobility.log')
    if ($LASTEXITCODE -ne 0) { throw 'Offline mobility regression failed.' }
    & dotnet 'tests\SemanticTopologyRegression\bin\Release\net9.0\SemanticTopologyRegression.dll' (Join-Path $output 'semantic') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'semantic.log')
    if ($LASTEXITCODE -ne 0) { throw 'Offline semantic topology regression failed.' }
    & dotnet 'tests\FillSupportRegression\bin\Release\net9.0\FillSupportRegression.dll' (Join-Path $output 'fill-support') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'fill-support.log')
    if ($LASTEXITCODE -ne 0) { throw 'Offline fill support regression failed.' }
    & dotnet 'tests\BoundaryAdapterRegression\bin\Release\net9.0\BoundaryAdapterRegression.dll' (Join-Path $output 'boundary') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'boundary.log')
    if ($LASTEXITCODE -ne 0) { throw 'Offline Boundary adapter regression failed.' }
    & python scripts/check-plugin-contract.py --runtime 2>&1 | Tee-Object -FilePath (Join-Path $output 'source-runtime-contract.log')
    if ($LASTEXITCODE -ne 0) { throw 'Source/runtime contract drift.' }
    & python scripts/verify-local-integration-mcp.py (Join-Path $output 'mcp') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'mcp.log')
    if ($LASTEXITCODE -ne 0) { throw 'Offline public MCP regression failed.' }
} finally { Pop-Location }
