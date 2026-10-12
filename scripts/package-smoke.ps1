param([Parameter(Mandatory)][string]$Rid)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$stage = Join-Path $root ('artifacts/smoke-' + $Rid + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$project = 'src/Gua.Playtest.Cli/Gua.Playtest.Cli.csproj'
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE)" }
}
function Assert-Cli([string]$Command, [string[]]$Arguments, [int]$Expected, [string]$Code) {
    $lines = & $Command --json @Arguments
    $actual = $LASTEXITCODE
    if ($actual -ne $Expected) { throw "CLI exit $actual, expected $Expected : $lines" }
    $result = ($lines -join "`n") | ConvertFrom-Json
    if ($result.code -ne $Code) { throw "Unexpected CLI result: $lines" }
    $script:records += [pscustomobject]@{ command = [IO.Path]::GetFileName($Command); arguments = $Arguments; exit = $actual; code = $result.code }
}
$records = @()
$valid = Join-Path $stage 'valid.json'
$invalid = Join-Path $stage 'invalid.json'
$contractRoot = Join-Path $root 'fixtures/contracts'
$probe = Join-Path $root 'tests/Gua.Playtest.ProcessProbe/Gua.Playtest.ProcessProbe.csproj'
Invoke-Dotnet @('build', $probe, '-c', 'Release', '--no-restore')
$probeDll = Join-Path $root 'tests/Gua.Playtest.ProcessProbe/bin/Release/net10.0/Gua.Playtest.ProcessProbe.dll'
$trace = (& dotnet $probeDll trace (Join-Path $stage 'saved-trace') | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0) { throw 'Saved Gua Trace fixture creation failed' }
function Assert-PackagedDashboard([string]$Command, [string]$Label) {
    $dashboardRoot = Join-Path $stage ('dashboard-' + $Label)
    $runRoot = Join-Path $dashboardRoot 'explicit-runs'
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    foreach ($name in @('registry.json', 'scenario.json')) {
        Copy-Item -LiteralPath (Join-Path $contractRoot $name) -Destination (Join-Path $dashboardRoot $name)
    }
    $before = @(Get-ChildItem -LiteralPath $dashboardRoot -Recurse -File | ForEach-Object {
        [pscustomobject]@{ path = $_.FullName; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $reservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $reservation.Start(); $port = $reservation.LocalEndpoint.Port }
    finally { $reservation.Stop() }
    $start = [Diagnostics.ProcessStartInfo]::new($Command)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--json', 'dashboard', '--registry', (Join-Path $dashboardRoot 'registry.json'),
        '--artifact-root', $runRoot, '--allow-root', $dashboardRoot, '--port', $port.ToString([Globalization.CultureInfo]::InvariantCulture))) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    $started = $false
    try {
        if (!$process.Start()) { throw 'Dashboard process did not start' }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $readyTask = $process.StandardError.ReadLineAsync()
        if (!$readyTask.Wait(15000)) { throw 'Packaged Dashboard did not become ready' }
        $ready = $readyTask.GetAwaiter().GetResult()
        if ($ready -ne "http://127.0.0.1:$port/") { throw 'Dashboard readiness did not use the explicit loopback port' }
        $client.BaseAddress = [Uri]::new($ready)
        foreach ($route in @('/', '/dashboard.js', '/dashboard.css')) {
            $body = $client.GetStringAsync($route).GetAwaiter().GetResult()
            if ([string]::IsNullOrEmpty($body)) { throw 'Packaged Dashboard asset missing' }
        }
        $snapshot = $client.GetStringAsync('/api/snapshot').GetAwaiter().GetResult() | ConvertFrom-Json
        if ($snapshot.registryState -ne 'Ready' -or @($snapshot.scenarios).Count -ne 1 -or @($snapshot.runs).Count -ne 0) {
            throw 'Packaged Dashboard did not preserve the explicit read-only index'
        }
        foreach ($file in $before) {
            if ((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Dashboard changed source bytes' }
        }
        if (@(Get-ChildItem -LiteralPath $dashboardRoot -Recurse -File).Count -ne $before.Count) { throw 'Dashboard wrote source artifacts' }
    }
    finally {
        $client.Dispose()
        if ($started -and !$process.HasExited) { $process.Kill($true) }
        if ($started -and !$process.WaitForExit(10000)) { throw 'Dashboard smoke process cleanup timed out' }
        if ($started) { $capturedStdout = $stdoutTask.GetAwaiter().GetResult() }
        $process.Dispose()
    }
    if ($capturedStdout.Length -ne 0) { throw 'Dashboard startup polluted JSON stdout' }
    $script:records += [pscustomobject]@{
        command = [IO.Path]::GetFileName($Command); operation = 'dashboard-startup'; port = $port
        registryState = 'Ready'; scenarios = 1; runs = 0; stdoutBytes = 0
        shutdown = 'forced-process-cleanup'; scope = 'Read-only packaged HTTP startup, not graceful cancellation or game E2E'
    }
}

function Assert-OfflineCommands([string]$Command, [string]$Label) {
    Assert-Cli $Command @('validate', '--allow-root', $contractRoot, (Join-Path $contractRoot 'scenario.json')) 0 'Valid'
    Assert-Cli $Command @('plan', 'create', '--managed-root', (Join-Path $stage ('reject-' + $Label)), '--allow-root', $contractRoot, '--scenario', (Join-Path $contractRoot 'multiple-goals.json'), '--recording', (Join-Path $contractRoot 'recording.json'), '--timing', 'recorded') 2 'ExecutionInputInvalid'
    $html = Join-Path $stage ('report-' + $Label + '.html')
    Assert-Cli $Command @('report', '--allow-root', $stage, '--trace', $trace, '--output', $html) 0 'SavedTraceRead'
    Assert-Cli $Command @('report', '--allow-root', $stage, '--trace', $trace, '--output', $html) 2 'ReportOutputExists'
    Assert-Cli $Command @('replay', '--allow-root', $contractRoot, '--plan', (Join-Path $contractRoot 'plan.json'), '--environment', (Join-Path $contractRoot 'environment.json')) 2 'ProductionRuntimeUnavailable'
}
Set-Content -LiteralPath $valid -Value '{"id":{"value":"ready"}}' -Encoding utf8NoBOM
Set-Content -LiteralPath $invalid -Value '{"id":42}' -Encoding utf8NoBOM
$published = Join-Path $stage 'published'
Invoke-Dotnet @('publish', $project, '-c', 'Release', '-r', $Rid, '--self-contained', 'true', '-o', $published)
$zip = Join-Path $stage 'gua-playtest.zip'
Compress-Archive -Path (Join-Path $published '*') -DestinationPath $zip
$extracted = Join-Path $stage 'extracted'
Expand-Archive -LiteralPath $zip -DestinationPath $extracted
$exe = Join-Path $extracted $(if ($IsWindows) { 'Gua.Playtest.Cli.exe' } else { 'Gua.Playtest.Cli' })
if (!$IsWindows) { & chmod +x $exe; if ($LASTEXITCODE) { throw 'chmod failed' } }
Assert-Cli $exe @('validate', '--gua-schema', 'selector.schema.json', $valid) 0 'gua-schema-valid'
Assert-Cli $exe @('validate', '--gua-schema', 'selector.schema.json', $invalid) 2 'gua-schema-invalid'
Assert-Cli $exe @('doctor', '--native') 0 'native-package-compatible'
Assert-OfflineCommands $exe 'archive'
Assert-PackagedDashboard $exe 'archive'
# Remove just Gua native assets from this uniquely created extraction. Offline validation must still work.
$nativeNames = @('gua.dll', 'gua_runtime.dll', 'libgua.so', 'libgua_runtime.so', 'libgua.dylib', 'libgua_runtime.dylib')
$native = @(Get-ChildItem -LiteralPath $extracted -Recurse -File | Where-Object Name -In $nativeNames)
if ($native.Count -lt 2) { throw 'Archive did not contain both native libraries' }
foreach ($file in $native) {
    $resolved = [IO.Path]::GetFullPath($file.FullName)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath($extracted) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Removal escaped extraction' }
    Remove-Item -LiteralPath $resolved
}
Assert-Cli $exe @('validate', '--gua-schema', 'selector.schema.json', $valid) 0 'gua-schema-valid'
Assert-Cli $exe @('doctor', '--native') 2 'native-package-unavailable-or-incompatible'
$feed = Join-Path $stage 'feed'
Invoke-Dotnet @('pack', $project, '-c', 'Release', '-o', $feed)
$tool = Join-Path $stage 'tool'
# A fresh cache and local-only feed prevent reuse of an older build with the same development version.
$config = Join-Path $stage 'NuGet.Config'
$escapedFeed = [Security.SecurityElement]::Escape($feed)
Set-Content -LiteralPath $config -Value "<configuration><packageSources><clear/><add key=`"smoke`" value=`"$escapedFeed`"/></packageSources></configuration>" -Encoding utf8NoBOM
$previousCache = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path $stage 'tool-cache'
    Invoke-Dotnet @('tool', 'install', 'Gua.Playtest.Cli', '--version', '0.1.0-dev', '--tool-path', $tool, '--configfile', $config)
} finally { $env:NUGET_PACKAGES = $previousCache }
$toolExe = Join-Path $tool $(if ($IsWindows) { 'gua-playtest.exe' } else { 'gua-playtest' })
Assert-Cli $toolExe @('validate', '--gua-schema', 'selector.schema.json', $valid) 0 'gua-schema-valid'
Assert-Cli $toolExe @('validate', '--gua-schema', 'selector.schema.json', $invalid) 2 'gua-schema-invalid'
Assert-Cli $toolExe @('doctor', '--native') 0 'native-package-compatible'
Assert-OfflineCommands $toolExe 'tool'
Assert-PackagedDashboard $toolExe 'tool'
[pscustomobject]@{
    rid = $Rid
    head = (& git rev-parse HEAD)
    sdk = (& dotnet --version)
    guaVersion = '1.1.1'
    guaCommit = '88f5dca4aa97c5d5187ab66ea4416377f3affc96'
    archiveSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    checks = $records
    scope = 'Actual archive/fresh Tool offline validate, Plan refusal, saved report and fail-closed Replay; no engine, bridge or Codex authentication/execution.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'evidence.json') -Encoding utf8NoBOM
Write-Output "Package smoke passed: $stage"
