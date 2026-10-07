param([string]$OutputRoot,[string]$SolidWorksInteropDir)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(-not $OutputRoot){$OutputRoot=Join-Path $repo ('artifacts\root-hardening-offline-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
if(Test-Path -LiteralPath $OutputRoot){throw 'Use an unused output directory to preserve earlier evidence.'}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$env:CAD_EXECUTOR_RECEIPTS_DIR=Join-Path $OutputRoot 'executor-receipts'
$interop=(& (Join-Path $PSScriptRoot 'find-solidworks.ps1') -SolidWorksInteropDir $SolidWorksInteropDir).TrimEnd([char]'\',[char]'/')
$before=@(Get-Process SLDWORKS -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$projects=@('RootHardeningRegression','RootHardeningAdapterRegression','FocusedCore','SurfaceRegression','SurfaceAdapterRegression','SurfaceReliabilityRegression','DrawingRegression','Stage2AdapterRegression','Stage4IntegrationRegression','OmissionRegression')
$results=@()
foreach($project in $projects){
    $path=Join-Path $repo "tests\$project\$project.csproj"
    $output=& dotnet run --project $path "-p:SolidWorksInteropDir=$interop" 2>&1
    $code=$LASTEXITCODE
    $output | Out-File -LiteralPath (Join-Path $OutputRoot "$project.log") -Encoding utf8
    $last=@($output | Select-Object -Last 1)
    $results+=@{project=$project;exit_code=$code;summary=($last -join ' ')}
    Write-Output "$project : exit=$code : $last"
    if($code -ne 0){$results | ConvertTo-Json -Depth 5 | Out-File (Join-Path $OutputRoot 'results.json') -Encoding utf8;throw "Offline regression failed: $project"}
}
$after=@(Get-Process SLDWORKS -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
if(@($after | Where-Object {$_ -notin $before}).Count -gt 0){throw 'Unexpected new SolidWorks process detected.'}
@{status='passed';scope='offline and managed adapters only';solidworks_before=$before;solidworks_after=$after;results=$results} | ConvertTo-Json -Depth 5 | Out-File (Join-Path $OutputRoot 'results.json') -Encoding utf8
