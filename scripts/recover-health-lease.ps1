[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{32}$')][string]$RequestId,
    [string]$ReceiptsDir = $env:CAD_EXECUTOR_RECEIPTS_DIR,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
if (-not $ReceiptsDir) {
    $ReceiptsDir = Join-Path $env:LOCALAPPDATA 'AutoSolidWorks\executor-receipts'
}
$lockPath = Join-Path $ReceiptsDir 'native-execution.lock'
$receiptPath = Join-Path $ReceiptsDir "$RequestId.json"
$archivePath = "$lockPath.$RequestId.bak"

# Block native writers and competing recovery attempts until the rename completes.
# FileShare.Delete allows the rename while we still hold this handle on Windows.
$stream = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'Delete')
try {
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 1024, $true)
    try { $lease = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($lease.request_id -cne $RequestId -or $receipt.id -cne $RequestId -or
        $lease.state -cne 'active' -or $receipt.state -cne 'active' -or $receipt.action -cne 'health' -or
        $null -ne $receipt.response -or ($lease.process_id -isnot [int] -and $lease.process_id -isnot [long]) -or
        $lease.process_id -le 0 -or $lease.process_id -gt [int]::MaxValue -or
        $receipt.process_id -isnot $lease.process_id.GetType() -or $receipt.process_id -ne $lease.process_id) {
        throw 'Recovery requires matching active lease and health receipt with the same owner and no final response.'
    }
    try {
        $owner = [Diagnostics.Process]::GetProcessById($lease.process_id)
    } catch [ArgumentException] {
        $owner = $null
    }
    if ($owner) {
        $owner.Dispose()
        throw 'The recorded PID still exists. Leave the lease in place.'
    }
    if ($Apply) {
        [IO.File]::Move($lockPath, $archivePath)
        Write-Output "Archived the interrupted health lease: $archivePath. Original receipt retained."
    } else {
        Write-Output "Eligible interrupted health lease $RequestId. Run again with -Apply to archive it."
    }
} finally {
    $stream.Dispose()
}
