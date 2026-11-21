param([Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Normal','CancelPurchase','PartialDelivery')][string]$Case = 'Normal',
    [string]$Cli = (Join-Path $PSScriptRoot '../../src/Gua.Playtest.Cli/bin/Release/net10.0/Gua.Playtest.Cli.exe'))
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$cliPath = (Resolve-Path -LiteralPath $Cli).Path
$fixture = $PSScriptRoot
$envDoc = Get-Content -LiteralPath "$fixture/environment.json" -Raw | ConvertFrom-Json
$hash = $envDoc.buildId.Substring(7)
if ((Get-FileHash -LiteralPath "$game/guild.exe").Hash -ne $hash -or
    (Get-FileHash -LiteralPath "$game/gua_godot.windows.release.x86_64.dll").Hash -ne '19a2bdd6cc6f3533ada6c5af02ba4b3dfcb95536a451216fb99d9e39ec1c6d0a') { throw 'Unexpected guild distribution' }
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains ([uri]$envDoc.connection.endpoint).Port) { throw 'Guild port already in use' }
$output = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
foreach ($dir in 'runs','appdata','local','save') { New-Item -ItemType Directory -Path "$output/$dir" | Out-Null }
$targets = @('start','shop','qty_plus','qty_plus','buy','confirm_trade','back','talk','talk_supplies','back','quests','accept_0','deliver_0','deliver_0','deliver_0','claim_0')
if ($Case -eq 'CancelPurchase') { $targets[5] = 'cancel_trade' }
if ($Case -eq 'PartialDelivery') { $targets = $targets[0..13] + 'claim_0' }
$launch = @('fixed-click-run','--json','--allow-root',$fixture,'--allow-root',$game,'--allow-root',$output,
    '--scenario',"$fixture/scenario.json",'--environment',"$fixture/environment.json",'--executable',"$game/guild.exe",
    '--executable-sha256',$hash,'--working-directory',$game,'--output-root',"$output/runs",
    '--child-env',"APPDATA=$output/appdata",'--argument','--headless','--argument','--log-file','--argument',"$output/game.log",
    '--argument','--','--argument',"--save-path=$output/save/guild.json")
foreach ($target in $targets) { $launch += '--target-id',$target }
$oldLocal = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = "$output/local"
    & $cliPath @launch > "$output/command.json" 2> "$output/command.stderr"
    $runExit = $LASTEXITCODE
} finally { $env:LOCALAPPDATA = $oldLocal }
$result = Get-Content "$output/command.json" -Raw | ConvertFrom-Json
if ($result.details.artifactDirectory) {
    $manifest = @(Get-ChildItem -LiteralPath (Join-Path $result.details.artifactDirectory 'trace') -Recurse -File -Filter manifest.json)
    if ($manifest.Count -ne 1) { throw 'Expected a single saved Trace' }
    & $cliPath report --json --allow-root $output --trace $manifest[0].DirectoryName --output "$output/report.html" > "$output/report.json"
    if ($LASTEXITCODE) { throw "Report failed; see $output/report.json" }
}
if ($Case -ne 'Normal') {
    if ($runExit -eq 0 -or $result.status -ne 'Failed' -or $result.code -notin @('ActionsExhausted','WaitExpired') -or
        $result.details.actions -ne $targets.Count -or !$result.details.click.succeeded -or
        $result.details.click.nodeId -ne 'claim_0' -or !$result.details.postProcessingComplete) { throw 'Unexpected control outcome' }
} elseif ($runExit -or $result.code -ne 'GoalSatisfied' -or $result.details.actions -ne $targets.Count -or !$result.details.postProcessingComplete) {
    throw "Guild acceptance failed ($runExit, $($result.code)); see $output/command.json"
}
$expectedLabels = switch ($Case) {
    'Normal'          { '1. 薬草 3/3 | done | day ≤ 3 | 20G'; '日付 1  |  所持金 68 G  |  鞄 0 / 3 種（各9個）' }
    'CancelPurchase'  { '1. 薬草 0/3 | active | day ≤ 3 | 20G'; '日付 1  |  所持金 60 G  |  鞄 0 / 3 種（各9個）' }
    'PartialDelivery' { '1. 薬草 2/3 | active | day ≤ 3 | 20G'; '日付 1  |  所持金 48 G  |  鞄 1 / 3 種（各9個）' }
}
& "$fixture/Assert-Trace.ps1" -TraceDirectory $manifest[0].DirectoryName -Targets $targets -ExpectedLabels $expectedLabels
if (Get-Process -Id $result.details.ownedProcessId -ErrorAction SilentlyContinue) { throw 'Owned process still present' }
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains ([uri]$envDoc.connection.endpoint).Port) { throw 'Guild port still in use' }
Write-Output "$Case $($result.status) $($result.code) $($result.details.runId); $output/report.html"
