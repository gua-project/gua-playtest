param([Parameter(Mandatory)][string]$TraceDirectory,
    [Parameter(Mandatory)][string[]]$Targets,
    [Parameter(Mandatory)][string[]]$ExpectedLabels)
$ErrorActionPreference = 'Stop'
$events = Get-Content -LiteralPath (Join-Path $TraceDirectory 'events.jsonl') | ConvertFrom-Json
$completed = @($events | Where-Object {
    $_.type -ceq 'runner.ui-click' -and $_.data.Reason -ceq 'host-completed' -and $_.data.Succeeded -eq $true
})
if ($completed.Count -ne $Targets.Count -or
    @($completed.data.RequestId | Sort-Object -Unique).Count -ne $Targets.Count) { throw 'Incomplete click sequence' }
$claim = $completed[-1]
for ($i = 0; $i -lt $completed.Count; $i++) {
    $receipt = $completed[$i].data
    if ($receipt.NodeId -cne $Targets[$i] -or $receipt.Profile -cne 'Player' -or
        $receipt.SourceId -cne $claim.data.SourceId -or $receipt.SessionEpoch -ne $claim.data.SessionEpoch) {
        throw 'Unexpected click identity or target'
    }
}
$last = $events | Where-Object { $_.type -ceq 'observation' -and $_.data.channel -ceq 'gua' } | Select-Object -Last 1
if (!$last -or $last.sequence -le $claim.sequence -or $last.data.availability -cne 'Available' -or
    $last.data.host.sourceId -cne $claim.data.SourceId -or $last.data.host.sessionEpoch -ne [string]$claim.data.SessionEpoch -or
    $last.data.blob -cnotmatch '^snapshots/([0-9a-f]{64})\.json$') { throw 'Missing final observation after the claim receipt' }
$expectedHash = $Matches[1]
$blob = Join-Path $TraceDirectory $last.data.blob
if ((Get-FileHash -LiteralPath $blob).Hash -ne $expectedHash) { throw 'Snapshot hash mismatch' }
$reads = (Get-Content -LiteralPath $blob -Raw | ConvertFrom-Json).Reads
$labels = @($reads | Where-Object {
    $_.Identity.Profile -ceq 'player' -and $_.Identity.SourceId -ceq $claim.data.SourceId -and
    $_.Identity.SessionEpoch -eq $claim.data.SessionEpoch -and $_.Reason -ceq 'available'
} | ForEach-Object { $_.Value.value })
foreach ($label in $ExpectedLabels) {
    if ($labels -cnotcontains $label) { throw 'Final observation does not confirm the expected request and balance' }
}
