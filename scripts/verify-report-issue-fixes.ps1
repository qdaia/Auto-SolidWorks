param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$SolidWorksInteropDir)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$interop=(& (Join-Path $PSScriptRoot 'find-solidworks.ps1') -SolidWorksInteropDir $SolidWorksInteropDir).TrimEnd([char]'\',[char]'/')
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh verification directory.'}
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location $root
try {
    foreach($name in @('ReportIssueRegression','OmissionRegression','DrawingRegression','EngineeringHardeningRegression')) {
        & dotnet build "tests\$name\$name.csproj" -c Release -m:1 -nodeReuse:false "-p:SolidWorksInteropDir=$interop" -p:DebugType=none -p:DebugSymbols=false *> (Join-Path $output "$name-build.log")
        if($LASTEXITCODE -ne 0){throw "Build failed: $name"}
    }
    foreach($name in @('ReportIssueRegression','OmissionRegression','EngineeringHardeningRegression')) {
        & dotnet "tests\$name\bin\Release\net9.0-windows\$name.dll" (Join-Path $output $name) *> (Join-Path $output "$name.log")
        if($LASTEXITCODE -ne 0){throw "Regression failed: $name"}
        Get-Content (Join-Path $output "$name.log") -Tail 1
    }
    & dotnet 'tests\DrawingRegression\bin\Release\net9.0-windows\DrawingRegression.dll' *> (Join-Path $output 'DrawingRegression.log')
    if($LASTEXITCODE -ne 0){throw 'Drawing parser regression failed'}
    Get-Content (Join-Path $output 'DrawingRegression.log') -Tail 1
    & python scripts/verify-local-integration-mcp.py (Join-Path $output 'public-mcp') *> (Join-Path $output 'public-mcp.log')
    if($LASTEXITCODE -ne 0){throw 'Public MCP regression failed'}
    Get-Content (Join-Path $output 'public-mcp.log') -Tail 1
    & python scripts/verify-token-economy-mcp.py (Join-Path $output 'compact-mcp') *> (Join-Path $output 'compact-mcp.log')
    if($LASTEXITCODE -ne 0){throw 'Compact MCP regression failed'}
    Get-Content (Join-Path $output 'compact-mcp.log') -Tail 1
} finally {Pop-Location}
