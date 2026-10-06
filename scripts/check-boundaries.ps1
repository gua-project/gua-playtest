$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$expected = @{
    'Core' = @()
    'Runner' = @('Core')
    'GuaIntegration' = @('Core')
    'Planners.Codex' = @('Core')
    'Cli' = @('Runner', 'GuaIntegration', 'Planners.Codex')
}
foreach ($name in $expected.Keys) {
    $file = Join-Path $root "src/Gua.Playtest.$name/Gua.Playtest.$name.csproj"
    [xml]$project = Get-Content -Raw -LiteralPath $file
    $actual = @($project.SelectNodes('//ProjectReference') | ForEach-Object {
        $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path $file -Parent) $_.Include))
        if (!$resolved.StartsWith((Join-Path $root 'src') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "External project reference: $name"
        }
        [IO.Path]::GetFileNameWithoutExtension($resolved) -replace '^Gua.Playtest\.', ''
    })
    if (@(Compare-Object -ReferenceObject @($expected[$name] | Sort-Object) -DifferenceObject @($actual | Sort-Object)).Count) {
        throw "Dependency graph mismatch: $name"
    }
    $packages = @($project.SelectNodes('//PackageReference'))
    if ($name -notin @('Core', 'GuaIntegration') -and $packages.Count) { throw "Unexpected product package dependency: $name" }
    if ($name -eq 'Core') {
        if ($packages.Count -ne 2) { throw 'Unexpected schema package graph' }
        foreach ($package in $packages) {
            if (($package.Include -eq 'JsonSchema.Net' -and $package.Version -eq '[7.3.4]') -or
                ($package.Include -eq 'YamlDotNet' -and $package.Version -eq '[16.3.0]')) { continue }
            throw 'Schema package pin mismatch'
        }
    }
    if ($name -eq 'GuaIntegration') {
        if ($packages.Count -ne 2) { throw 'Unexpected Gua package graph' }
        foreach ($package in $packages) {
            if ($package.Include -notin @('Gua.Testing', 'Gua.Runtime') -or $package.Version -ne '[1.1.1]') {
                throw 'Gua package pin mismatch'
            }
        }
    }
    if ($project.SelectNodes('//Reference|//Compile[@Include]|//Import').Count) { throw "External source/import boundary: $name" }
}
if (@(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.csproj).Count -ne 5) { throw 'Expected five product projects' }
$coreFiles = Get-ChildItem (Join-Path $root 'src/Gua.Playtest.Core') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '[\\/]obj[\\/]' }
if ($coreFiles | Select-String -Pattern 'using Gua\.(Core|Runtime|Testing)|Codex|DllImport|Process\.') {
    throw 'Core has an engine/planner dependency'
}
Write-Output 'Five-project dependency boundary: passed'
