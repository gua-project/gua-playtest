param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/contracts-' + [guid]::NewGuid().ToString('N')) }
$stage = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $stage) { throw 'Smoke output must be new' }
New-Item -ItemType Directory -Path $stage | Out-Null
$published = Join-Path $stage 'published'
& dotnet publish (Join-Path $root 'src/Gua.Playtest.Cli/Gua.Playtest.Cli.csproj') -c Release --no-restore --self-contained false -o $published
if ($LASTEXITCODE) { throw 'Contract CLI publish failed' }
$nativeNames = @('gua.dll','gua_runtime.dll','libgua.so','libgua_runtime.so','libgua.dylib','libgua_runtime.dylib')
$native = @(Get-ChildItem -LiteralPath $published -Recurse -File | Where-Object Name -In $nativeNames)
if ($native.Count -lt 2) { throw 'Expected actual native payload before removal' }
foreach ($file in $native) {
    $target = [IO.Path]::GetFullPath($file.FullName)
    if (!$target.StartsWith($published + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Native removal escaped smoke publish directory' }
    Remove-Item -LiteralPath $target
}
$fixtures = Join-Path $stage 'inputs'
New-Item -ItemType Directory -Path $fixtures | Out-Null
Copy-Item -Path (Join-Path $root 'fixtures/contracts/*') -Destination $fixtures
$dll = Join-Path $published 'Gua.Playtest.Cli.dll'
$checks = @()
$previous = Get-Location
try {
    Set-Location $stage
    $cases = @(Get-Content -Raw -LiteralPath (Join-Path $fixtures 'expected.json') | ConvertFrom-Json)
    foreach ($case in $cases) {
        $lines = & dotnet $dll validate --allow-root $fixtures (Join-Path $fixtures $case.file)
        $exit = $LASTEXITCODE
        $expectedExit = if ($case.expected -eq 'Valid') { 0 } else { 2 }
        $response = ($lines -join "`n") | ConvertFrom-Json
        if ($exit -ne $expectedExit -or $response.code -ne $case.expected) { throw "Contract mismatch: $($case.file)" }
        $checks += [pscustomobject]@{ file=$case.file; expected=$case.expected; actual=$response.code; exit=$exit }
    }
    $lines = & dotnet $dll doctor --native
    if ($LASTEXITCODE -ne 2 -or (($lines -join "`n") | ConvertFrom-Json).code -ne 'native-package-unavailable-or-incompatible') { throw 'Missing-native control did not fail' }
} finally { Set-Location $previous }
[pscustomobject]@{
    head = (& git -C $root rev-parse HEAD)
    sdk = (& dotnet --version)
    guaVersion = '1.1.1'
    nativeFilesRemoved = $native.Count
    tests = $checks
    scope = 'Actual published CLI with Gua native payload removed; static forms/paths only. No host, engine, Planner or Goal scoring.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'evidence.json') -Encoding utf8NoBOM
Write-Output "Contract offline smoke passed: $stage"
