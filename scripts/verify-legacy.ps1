param([string]$ResultsDirectory, [switch]$ResourceRun)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$previousResourceRun = $env:NETLIGHTS_RESOURCE_RUN
$previousResourceOutput = $env:NETLIGHTS_RESOURCE_OUTPUT
Push-Location $root
try {
    if (-not $ResultsDirectory) {
        $ResultsDirectory = Join-Path 'TestResults' (Get-Date -Format 'yyyyMMdd-HHmmss-ffff')
    }
    $ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
    if ((Test-Path -LiteralPath $ResultsDirectory) -and @(Get-ChildItem -LiteralPath $ResultsDirectory -Force).Count -gt 0) {
        throw "Results directory must be empty to prevent stale coverage: $ResultsDirectory"
    }
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    if ($ResourceRun) {
        $env:NETLIGHTS_RESOURCE_RUN = '1'
        $env:NETLIGHTS_RESOURCE_OUTPUT = Join-Path $ResultsDirectory 'resources-hour.csv'
    }

    function Invoke-DotNet([string[]]$Arguments) {
        Write-Host "dotnet $($Arguments -join ' ')"
        $limitedArguments = @($Arguments[0], '-maxcpucount:8') + @($Arguments | Select-Object -Skip 1)
        & dotnet @limitedArguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
    }

    Invoke-DotNet -Arguments @('restore', 'NetLights.slnx', '--locked-mode')
    Invoke-DotNet -Arguments @('build', 'NetLights.slnx', '-c', 'Release', '--no-restore')
    foreach ($suite in @('Unit', 'Integration', 'Soak')) {
        $name = $suite.ToLowerInvariant()
        $arguments = @('test', "tests/NetLights.${suite}Tests/NetLights.${suite}Tests.csproj", '-c', 'Release', '--no-build', '--logger', "trx;LogFileName=$name.trx", '--results-directory', (Join-Path $ResultsDirectory $name))
        if ($suite -ne 'Soak') {
            $arguments += @('--collect:XPlat Code Coverage', '--settings', 'tests/coverlet.runsettings')
            $sessionTimeout = if ($ResourceRun -and $suite -eq 'Integration') { 4200000 } else { 600000 }
            $arguments += @('--', "RunConfiguration.TestSessionTimeout=$sessionTimeout")
        }
        Invoke-DotNet $arguments
    }
    & (Join-Path $PSScriptRoot 'check-coverage.ps1') -ResultsDirectory $ResultsDirectory
    & (Join-Path $PSScriptRoot 'test-resource-stability.ps1') -SelfTest
    & (Join-Path $PSScriptRoot 'test-local-publish-transaction.ps1')
    Write-Host "Verification passed. Results: $ResultsDirectory"
    Write-Host 'The default Soak suite is a short smoke test; Sandbox and long resource tests are separate checks.'
}
finally {
    if ($ResourceRun) {
        $env:NETLIGHTS_RESOURCE_RUN = $previousResourceRun
        $env:NETLIGHTS_RESOURCE_OUTPUT = $previousResourceOutput
    }
    Pop-Location
}
