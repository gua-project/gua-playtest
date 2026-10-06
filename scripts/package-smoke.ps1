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
    $lines = & $Command @Arguments
    $actual = $LASTEXITCODE
    if ($actual -ne $Expected) { throw "CLI exit $actual, expected $Expected : $lines" }
    $result = ($lines -join "`n") | ConvertFrom-Json
    if ($result.code -ne $Code) { throw "Unexpected CLI result: $lines" }
    $script:records += [pscustomobject]@{ command = [IO.Path]::GetFileName($Command); arguments = $Arguments; exit = $actual; code = $result.code }
}
$records = @()
$valid = Join-Path $stage 'valid.json'
$invalid = Join-Path $stage 'invalid.json'
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
[pscustomobject]@{
    rid = $Rid
    head = (& git rev-parse HEAD)
    sdk = (& dotnet --version)
    guaVersion = '1.1.1'
    guaCommit = '88f5dca4aa97c5d5187ab66ea4416377f3affc96'
    archiveSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    checks = $records
    scope = 'Package core/runtime load and offline validation; no engine, bridge, Scenario or Codex execution.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'evidence.json') -Encoding utf8NoBOM
Write-Output "Package smoke passed: $stage"
