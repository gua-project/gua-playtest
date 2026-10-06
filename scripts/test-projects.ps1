$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -File -Filter '*.csproj' | Sort-Object FullName)
if ($testProjects.Count -eq 0) { throw 'No test projects were discovered.' }
$testsFailed = $false
Push-Location $repositoryRoot
try {
    # Independent native/Trace tests retain their deadlines without competing
    # with other test processes. Keep all tests and collect every project's TRX.
    foreach ($testProject in $testProjects) {
        dotnet test $testProject.FullName --no-build --no-restore -c Release --logger trx --results-directory (Join-Path $repositoryRoot 'artifacts/tests')
        if ($LASTEXITCODE -ne 0) { $testsFailed = $true }
    }
}
finally { Pop-Location }
if ($testsFailed) { exit 1 }
