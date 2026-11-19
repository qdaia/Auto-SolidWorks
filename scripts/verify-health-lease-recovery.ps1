param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$null = New-Item -Path $OutputDirectory -ItemType Directory -ErrorAction Stop
$script = Join-Path $PSScriptRoot 'recover-health-lease.ps1'
$request = [Guid]::NewGuid().ToString('N')
$path = Join-Path $OutputDirectory 'native-execution.lock'
$receiptPath = Join-Path $OutputDirectory "$request.json"
$archive = "$path.$request.bak"
$dead = [int]::MaxValue
$lease = @{ state='active'; request_id=$request; process_id=$dead }
$receipt = @{ state='active'; id=$request; action='health'; process_id=$dead; response=$null }
function Save-Fixture {
    $lease | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding UTF8
    $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8
}
function Assert-Blocked {
    $before = (Get-FileHash -LiteralPath $path).Hash
    $blocked = $false
    try { & $script -RequestId $request -ReceiptsDir $OutputDirectory -Apply | Out-Null }
    catch { $blocked = $true }
    if (-not $blocked -or $before -ne (Get-FileHash -LiteralPath $path).Hash -or (Test-Path $archive)) {
        throw 'Recovery unexpectedly changed an ineligible lease.'
    }
}
$receipt.action = 'execute'; Save-Fixture; Assert-Blocked
$receipt.action = 'health'; $receipt.id = 'wrong'; Save-Fixture; Assert-Blocked
$receipt.id = $request; $receipt.process_id = 0; Save-Fixture; Assert-Blocked
$lease.process_id = $PID; $receipt.process_id = $PID; Save-Fixture; Assert-Blocked
$lease.process_id = $dead; $receipt.process_id = $dead; $receipt.response = @{}; Save-Fixture; Assert-Blocked
$receipt.response = $null; Save-Fixture
[IO.File]::WriteAllText($receiptPath, '{broken'); Assert-Blocked
[IO.File]::Move($receiptPath, "$receiptPath.invalid"); Assert-Blocked
Save-Fixture
$held = [IO.File]::Open($path, 'Open', 'ReadWrite', 'Read')
try {
    $blocked = $false
    try { & $script -RequestId $request -ReceiptsDir $OutputDirectory -Apply | Out-Null } catch { $blocked = $true }
    if (-not $blocked -or (Test-Path $archive)) { throw 'Recovery ignored an active writer.' }
} finally { $held.Dispose() }
$original = (Get-FileHash -LiteralPath $path).Hash
$originalReceipt = (Get-FileHash -LiteralPath $receiptPath).Hash
& $script -RequestId $request -ReceiptsDir $OutputDirectory | Out-Null
if (!(Test-Path $path) -or (Test-Path $archive)) { throw 'Preview changed the lease.' }
& $script -RequestId $request -ReceiptsDir $OutputDirectory -Apply | Out-Null
if ((Test-Path $path) -or (Get-FileHash $archive).Hash -ne $original -or
    (Get-FileHash $receiptPath).Hash -ne $originalReceipt) { throw 'Recovery lost original evidence.' }
Write-Output 'PASS: invalid and missing records, live PID, held handle, preview and archival.'
