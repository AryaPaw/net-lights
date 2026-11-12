param(
    [string]$ResultsDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$NativeUI,
    [switch]$FunctionalUI,
    [switch]$Legacy,
    [switch]$ResourceRun
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($NativeUI -and $FunctionalUI) { throw 'Select visual NativeUI or FunctionalUI evidence, not both.' }
$root = Split-Path -Parent $PSScriptRoot
$savedRustupToolchain = [Environment]::GetEnvironmentVariable('RUSTUP_TOOLCHAIN','Process')
$savedPythonOptimize = [Environment]::GetEnvironmentVariable('PYTHONOPTIMIZE','Process')
Push-Location $root
try {
    if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path 'TestResults' (Get-Date -Format 'yyyyMMdd-HHmmss-ffff') }
    $results = [IO.Path]::GetFullPath($ResultsDirectory)
    if ((Test-Path -LiteralPath $results) -and @(Get-ChildItem -LiteralPath $results -Force).Count) { throw 'Results directory must be empty.' }
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    function Get-SourceSnapshot {
        $files = [Collections.Generic.List[string]]::new()
        foreach ($directory in @('native','.cargo','scripts','.github','installer')) {
            $path = Join-Path $root $directory
            if (Test-Path -LiteralPath $path) {
                foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File -Force) {
                    # Python's interpreter cache isn't an input of the candidate.
                    if ($file.FullName -notmatch '[\\/]__pycache__[\\/]' -and $file.Extension -ne '.pyc') { $files.Add($file.FullName) }
                }
            }
        }
        foreach ($name in @('Cargo.toml','Cargo.lock','rust-toolchain.toml','Directory.Build.props','Directory.Packages.props','global.json')) {
            $path = Join-Path $root $name
            if (Test-Path -LiteralPath $path) { $files.Add($path) }
        }
        $snapshot = [ordered]@{}
        foreach ($path in $files | Sort-Object -Unique) {
            $snapshot[[IO.Path]::GetRelativePath($root,$path)] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
        return $snapshot
    }
    function Assert-SourceUnchanged {
        $current = Get-SourceSnapshot
        $changed = @(@($sourceSnapshot.Keys) + @($current.Keys) | Sort-Object -Unique | Where-Object {
            -not $sourceSnapshot.Contains($_) -or -not $current.Contains($_) -or $sourceSnapshot[$_] -ne $current[$_]
        })
        if ($changed.Count) {
            $current | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $results 'source-after-drift.json') -Encoding utf8
            throw "Verification inputs changed during the run; the candidate isn't verified: $($changed -join ', ')"
        }
    }
    $sourceSnapshot = Get-SourceSnapshot
    $sourceSnapshot | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $results 'source-before.json') -Encoding utf8
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-resource-stability.ps1') -SelfTest *> (Join-Path $results 'resource-gate-selftest.log')
    if ($LASTEXITCODE -ne 0) { throw 'Resource analyzer controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-native-ui.ps1') -SelfTest *> (Join-Path $results 'ui-isolation-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'UI isolation controls FAIL.' }
    function Invoke-Cargo([string]$Name,[string[]]$Arguments) {
        $log = Join-Path $results "$Name.log"
        Write-Host "RUN: cargo $($Arguments -join ' ')"
        & cargo @Arguments *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 100; throw "$Name FAIL ($LASTEXITCODE): $log" }
        Get-Content -LiteralPath $log -Tail 8
    }
    # Coverage is part of this entry point. Its tool is local to the workspace;
    # LLVM tools must match the project's pinned Rust compiler, not a system LLVM.
    $nativeRustChannel = [regex]::Match(
        (Get-Content -LiteralPath (Join-Path $root 'rust-toolchain.toml') -Raw),
        '(?m)^channel\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"\s*$'
    ).Groups[1].Value
    if (-not $nativeRustChannel) { throw 'The native test toolchain must be pinned to an exact Rust version.' }
    [Environment]::SetEnvironmentVariable('RUSTUP_TOOLCHAIN',$nativeRustChannel,'Process')
    [Environment]::SetEnvironmentVariable('PYTHONOPTIMIZE',$null,'Process')
    $nativeCoverageVersion = '0.9.1'
    $nativeTools = Join-Path $root 'artifacts/tools'
    $nativeToolBuild = Join-Path $root 'artifacts/rust-test-tools-target'
    $nativeCoverageTool = Join-Path $nativeTools 'bin/cargo-llvm-cov.exe'
    foreach ($ownedPath in @($nativeCoverageTool,$nativeToolBuild)) {
        for ($part = $ownedPath; $part; $part = [IO.Path]::GetDirectoryName($part)) {
            if ((Test-Path -LiteralPath $part) -and
                ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Native verification tools cannot pass through a reparse point.'
            }
        }
    }
    & rustup component add llvm-tools-preview --toolchain $nativeRustChannel *> (Join-Path $results 'llvm-tools-bootstrap.log')
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $results 'llvm-tools-bootstrap.log') -Tail 30; throw 'Pinned LLVM tools setup failed.' }
    if (-not (Test-Path -LiteralPath $nativeCoverageTool -PathType Leaf)) {
        Invoke-Cargo 'coverage-tool-bootstrap' @("+$nativeRustChannel",'install','cargo-llvm-cov','--version',$nativeCoverageVersion,'--locked','--root',$nativeTools,'--target-dir',$nativeToolBuild)
    }
    & $nativeCoverageTool llvm-cov --version *> (Join-Path $results 'coverage-tool-version.log')
    if ($LASTEXITCODE -ne 0 -or
        (Get-Content -LiteralPath (Join-Path $results 'coverage-tool-version.log') -Raw).Trim() -cne "cargo-llvm-cov $nativeCoverageVersion") {
        throw 'The workspace coverage tool has a different version; the existing executable was retained.'
    }
    [string[]]$cargoProfileArguments = @(if ($Configuration -eq 'Release') { '--release' })
    $common = @('--workspace','--locked','--target-dir','artifacts/rust-target','-j','4') + $cargoProfileArguments
    [xml]$props = Get-Content -LiteralPath Directory.Build.props -Raw
    $manifest = Get-Content -LiteralPath Cargo.toml -Raw
    $version = [regex]::Match($manifest,'(?m)^version = "([^"]+)"').Groups[1].Value
    if (-not $version -or $version -ne $props.Project.PropertyGroup.Version) { throw 'Canonical and compatibility versions differ.' }
    Invoke-Cargo 'format' @('fmt','--all','--','--check')
    Invoke-Cargo 'lint' (@('clippy','--all-targets') + $common + @('--','-D','warnings'))
    Invoke-Cargo 'tests' (@('test') + $common + @('--','--test-threads=1'))
    Assert-SourceUnchanged
    $published = Join-Path $results 'publish'
    & (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $published -Configuration $Configuration
    & (Join-Path $PSScriptRoot 'test-published.ps1') -PublishDirectory $published -ResultsDirectory (Join-Path $results 'published-smoke')
    & (Join-Path $PSScriptRoot 'test-local-publish-transaction.ps1') -ResultsDirectory (Join-Path $results 'local-publish')
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-sandbox-update.ps1') -SelfTest *> (Join-Path $results 'sandbox-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox identity and safety controls FAIL.' }
    Assert-SourceUnchanged
    # This gate is unconditional. Live public services and visual UI acceptance
    # are separate opt-in checks; neither can replace missing offline coverage.
    & (Join-Path $PSScriptRoot 'test-native-coverage.ps1') -ResultsDirectory (Join-Path $results 'coverage') -FunctionalUI
    Assert-SourceUnchanged
    if ($Legacy) { & (Join-Path $PSScriptRoot 'verify-legacy.ps1') -ResultsDirectory (Join-Path $results 'legacy') }
    if ($NativeUI -or $FunctionalUI -or $ResourceRun) {
        $candidateHash=(Get-FileHash -LiteralPath (Join-Path $published 'NetLights.exe') -Algorithm SHA256).Hash
        $uiArguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'test-native-ui.ps1'),
            '-Executable',(Join-Path $published 'NetLights.exe'),'-ResultsDirectory',(Join-Path $results 'native-ui'),'-ExpectedSha256',$candidateHash)
        if ($FunctionalUI) { $uiArguments += '-FunctionalUI' }
        if ($ResourceRun) { $uiArguments += @('-MeasureSeconds','3600','-EnableCountryForMeasurement','-MaximumHistory') }
        & pwsh @uiArguments *> (Join-Path $results 'native-ui.log')
        if ($LASTEXITCODE -ne 0) { throw 'Native UI/resource verification FAIL.' }
        if ($ResourceRun) {
            $resourceCsv = Join-Path $results 'native-ui/ui/resource-measurement.csv'
            if (-not (Test-Path -LiteralPath $resourceCsv -PathType Leaf)) { throw 'Final-PID resource evidence is missing.' }
            & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-resource-stability.ps1') -InputPath $resourceCsv *> (Join-Path $results 'resource-stability.log')
            if ($LASTEXITCODE -ne 0) { throw 'Hour-long resource trend gate FAIL/INCONCLUSIVE; see resource-stability.log.' }
            Write-Host 'RUN - PASS: hour-long maximum history and both GeoIP databases resource gate.'
        }
    } else { Write-Host 'Normal-payload UI and hour resource verification: NOT RUN (use -FunctionalUI / -NativeUI / -ResourceRun).' }
    Write-Host 'Real Inno update/rollback in Windows Sandbox is a separate required cutover check.'
    Assert-SourceUnchanged
    Write-Host "RUN - PASS: Rust $Configuration build, tests and mandatory offline coverage. Logs: $results"
}
finally {
    [Environment]::SetEnvironmentVariable('RUSTUP_TOOLCHAIN',$savedRustupToolchain,'Process')
    [Environment]::SetEnvironmentVariable('PYTHONOPTIMIZE',$savedPythonOptimize,'Process')
    Pop-Location
}
