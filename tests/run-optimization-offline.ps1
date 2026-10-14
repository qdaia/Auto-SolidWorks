param([Parameter(Mandatory=$true)][string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$SolidWorksInteropDir)
$ErrorActionPreference='Stop'
$testRoot=Split-Path $PSScriptRoot -Parent
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $outputRoot){throw 'Use a fresh verification output directory.'}
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$records=@()
foreach($suiteName in @('OptimizationRegression','FocusedCore','ReportIssueRegression','EngineeringHardeningRegression','DesignIntentRegression','DesignExpressionRegression','DesignFeatureRegression','MiniWorkflowRegression','ModelInputRegression','TokenEconomyRegression')) {
    $project=Join-Path $PSScriptRoot ($suiteName+'\'+$suiteName+'.csproj')
    [xml]$projectXml=Get-Content -LiteralPath $project
    $framework=$projectXml.GetElementsByTagName('TargetFramework')[0].InnerText
    $buildLog=Join-Path $outputRoot ($suiteName+'-build.log')
    & dotnet build $project -c Release -m:1 -nodeReuse:false ('-p:SolidWorksInteropDir='+$SolidWorksInteropDir) *> $buildLog
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath $buildLog -Tail 30;throw ('Build failed: '+$suiteName)}
    $assembly=Join-Path $PSScriptRoot ($suiteName+'\bin\Release\'+$framework+'\'+$suiteName+'.dll')
    $arguments=@($assembly)
    if($suiteName -notin @('OptimizationRegression','FocusedCore')){$arguments+=(Join-Path $outputRoot $suiteName)}
    $runLog=Join-Path $outputRoot ($suiteName+'.log')
    & dotnet @arguments *> $runLog
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath $runLog -Tail 30;throw ('Tests failed: '+$suiteName)}
    $passCount=@(Get-Content -LiteralPath $runLog | Where-Object {$_ -match '^PASS(?:\s|:)'}).Count
    $records+=[pscustomobject]@{suite=$suiteName;passed=$true;checks=$passCount;log=$runLog;framework=$framework}
    Write-Output ($suiteName+' PASS '+$passCount)
}
$records | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json') -Encoding UTF8
