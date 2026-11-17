param(
    [string]$ResultsDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$NativeUI,
    [switch]$FunctionalUI,
    [switch]$Legacy,
    [switch]$ResourceRun,
    [switch]$Preflight,
    [switch]$CapturePreflight
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($NativeUI -and $FunctionalUI) { throw 'Select visual NativeUI or FunctionalUI evidence, not both.' }
if ($ResourceRun -and $FunctionalUI) { throw 'ResourceRun requires visual UI evidence, not offline FunctionalUI.' }
if ($Preflight -and ($NativeUI -or $FunctionalUI -or $Legacy -or $ResourceRun)) { throw 'Preflight is a separate headless check, not full verification.' }
if ($CapturePreflight -and ($Preflight -or $NativeUI -or $FunctionalUI -or $Legacy -or $ResourceRun)) { throw 'CapturePreflight is a separate headless fixture check.' }
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
        foreach ($directory in @('native','.cargo','scripts','.github','installer','compat')) {
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
    [Environment]::SetEnvironmentVariable('PYTHONOPTIMIZE',$null,'Process')
    # Parse every maintained PowerShell entry point before expensive compilation.
    $syntaxErrors = @(
        foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -Recurse -File) {
            $tokens=$null; $parseErrors=$null
            [Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$parseErrors) | Out-Null
            foreach ($parseError in $parseErrors) { '{0}:{1}: {2}' -f [IO.Path]::GetRelativePath($root,$script.FullName),$parseError.Extent.StartLineNumber,$parseError.Message }
        }
    )
    $syntaxErrors | Set-Content -LiteralPath (Join-Path $results 'powershell-syntax.log') -Encoding utf8
    if ($syntaxErrors.Count) { throw 'PowerShell syntax checks FAIL; see powershell-syntax.log.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-workflows.ps1') -ResultsDirectory (Join-Path $results 'workflows') *> (Join-Path $results 'workflow-controls.log')
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $results 'workflow-controls.log') -Tail 40; throw 'Workflow static checks FAIL.' }
    function Invoke-Cargo([string]$Name,[string[]]$Arguments) {
        $log = Join-Path $results "$Name.log"
        Write-Host "RUN: cargo $($Arguments -join ' ')"
        & cargo @Arguments *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 100; throw "$Name FAIL ($LASTEXITCODE): $log" }
        Get-Content -LiteralPath $log -Tail 8
    }
    $nativeRustChannel = [regex]::Match((Get-Content -LiteralPath (Join-Path $root 'rust-toolchain.toml') -Raw),'(?m)^channel\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"\s*$').Groups[1].Value
    if (-not $nativeRustChannel) { throw 'The native test toolchain must be pinned to an exact Rust version.' }
    [Environment]::SetEnvironmentVariable('RUSTUP_TOOLCHAIN',$nativeRustChannel,'Process')
    [string[]]$cargoProfileArguments = @(if ($Configuration -eq 'Release') { '--release' })
    $frameArguments=@('--manifest-path','scripts/fixtures/native-frameprobe/Cargo.toml','--locked','--target-dir','artifacts/frameprobe-target','-j','4')+$cargoProfileArguments
    function Test-Frameprobe {
        Invoke-Cargo 'format-frameprobe' @('fmt','--manifest-path','scripts/fixtures/native-frameprobe/Cargo.toml','--','--check')
        Invoke-Cargo 'lint-frameprobe' (@('clippy','--all-targets')+$frameArguments+@('--','-D','warnings'))
        Invoke-Cargo 'tests-frameprobe' (@('test')+$frameArguments+@('--','--test-threads=1'))
    }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'select-verification-scope.ps1') -SelfTest *> (Join-Path $results 'verification-scope-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Verification scope controls FAIL.' }
    if ($CapturePreflight) {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-native-ui.ps1') -SelfTest *> (Join-Path $results 'ui-isolation-controls.log')
        if ($LASTEXITCODE -ne 0) { throw 'UI isolation controls FAIL.' }
        Test-Frameprobe
        Assert-SourceUnchanged
        Write-Host "RUN - PASS: capture contract $Configuration preflight. Logs: $results"
        Write-Host 'NOT RUN: application tests/build, runtime coverage, GUI, installed updates, motion and resources. This is not full verification.'
        return
    }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-resource-stability.ps1') -SelfTest *> (Join-Path $results 'resource-gate-selftest.log')
    if ($LASTEXITCODE -ne 0) { throw 'Resource analyzer controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-native-ui.ps1') -SelfTest *> (Join-Path $results 'ui-isolation-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'UI isolation controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'collect-native-licenses.ps1') -SelfTest *> (Join-Path $results 'license-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'License collector controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-native-coverage.ps1') -SelfTest *> (Join-Path $results 'coverage-gate-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Coverage gate controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-native-coverage.ps1') -SelfTest *> (Join-Path $results 'coverage-runner-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Coverage runner controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-sandbox-update.ps1') -SelfTest *> (Join-Path $results 'sandbox-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox identity and safety controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-ci-upgrade.ps1') -SelfTest *> (Join-Path $results 'upgrade-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Installed update fixture controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'prepare-bridge.ps1') -SelfTest *> (Join-Path $results 'bridge-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility bridge scope controls FAIL.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'promote-verified-release.ps1') -SelfTest *> (Join-Path $results 'release-promotion-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Verified release promotion controls FAIL.' }
    & uv run --offline --cache-dir (Join-Path $root 'artifacts/uv-cache') --python 3.11 python (Join-Path $PSScriptRoot 'fixtures/automatic-update-feed.py') --self-test *> (Join-Path $results 'automatic-feed-controls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Automatic update feed controls FAIL.' }
    $common = @('--workspace','--locked','--target-dir','artifacts/rust-target','-j','4') + $cargoProfileArguments
    [xml]$props = Get-Content -LiteralPath Directory.Build.props -Raw
    $manifest = Get-Content -LiteralPath Cargo.toml -Raw
    $version = [regex]::Match($manifest,'(?m)^version = "([^"]+)"').Groups[1].Value
    if (-not $version -or $version -ne $props.Project.PropertyGroup.Version) { throw 'Canonical and compatibility versions differ.' }
    Invoke-Cargo 'format' @('fmt','--all','--','--check')
    & rustfmt --edition 2024 --check native/vendor/gpui-pre/src/elements/div.rs native/vendor/gpui-pre/src/elements/list.rs native/vendor/gpui-pre/src/elements/smooth_scroll.rs *> (Join-Path $results 'format-scroll-patch.log')
    if ($LASTEXITCODE -ne 0) { throw 'Local GPUI scroll patch format FAIL.' }
    Invoke-Cargo 'lint' (@('clippy','--all-targets') + $common + @('--','-D','warnings'))
    # Run every workspace package once, with critical headless paths first.
    # A core failure shouldn't wait for the large GPUI test executable to link.
    $workspaceFile = Join-Path $results 'workspace.json'
    & cargo metadata --locked --no-deps --format-version 1 1> $workspaceFile 2> (Join-Path $results 'workspace-inventory.stderr.log')
    if ($LASTEXITCODE -ne 0) { throw 'Workspace test inventory failed.' }
    $workspace = Get-Content -LiteralPath $workspaceFile -Raw | ConvertFrom-Json
    $packages = @($workspace.packages | Where-Object { $_.id -cin $workspace.workspace_members } |
        Sort-Object @{Expression={ if ($_.name -ceq 'netlights-core') { 0 } elseif ($_.name -ceq 'netlights-update-agent') { 1 } else { 2 } }}, name)
    if (-not $packages.Count -or $packages.Count -ne @($workspace.workspace_members).Count) { throw 'Workspace test inventory is incomplete.' }
    $packageArguments = @($common | Where-Object { $_ -cne '--workspace' })
    foreach ($package in $packages) {
        Invoke-Cargo ("tests-" + $package.name) (@('test','--package',$package.name) + $packageArguments + @('--','--test-threads=1'))
    }
    Invoke-Cargo 'tests-gpui-cursor-patch' (@('test','--package','gpui-pre-windows','--lib') + $packageArguments + @('netlights_cursor_tests','--','--test-threads=1'))
    Test-Frameprobe
    if ($Preflight) {
        Assert-SourceUnchanged
        Write-Host "RUN - PASS: headless $Configuration preflight (syntax, controls, format, Clippy, workspace and frameprobe tests). Logs: $results"
        Write-Host 'NOT RUN: GUI, published-process smoke, runtime coverage, installed upgrade, motion and resource measurement. Preflight is not full verification.'
        return
    }
    # Full verification still requires runtime coverage. Install its pinned tools
    # only after cheap checks have passed; preflight never needs them.
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
    if ($env:GITHUB_ACTIONS -ceq 'true') {
        . (Join-Path $PSScriptRoot 'ci-environment.ps1')
        Assert-HostedRunner
        Invoke-Cargo 'tests-tray-shell' (@('test','--package','netlights-app','--bin','netlights') + $packageArguments + @('tray::tests::synthetic_taskbar_created_restores_both_owned_icons','--','--ignored','--exact','--test-threads=1'))
        if (-not (Select-String -LiteralPath (Join-Path $results 'tests-tray-shell.log') -SimpleMatch 'test result: ok. 1 passed; 0 failed; 0 ignored;')) { throw 'The isolated shell test was not executed.' }
    } else {
        Write-Host 'NOT RUN: interactive tray-shell verification requires the disposable hosted runner.'
    }
    Assert-SourceUnchanged
    $published = Join-Path $results 'publish'
    & (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $published -Configuration $Configuration
    & (Join-Path $PSScriptRoot 'test-published.ps1') -PublishDirectory $published -ResultsDirectory (Join-Path $results 'published-smoke')
    & (Join-Path $PSScriptRoot 'test-local-publish-transaction.ps1') -ResultsDirectory (Join-Path $results 'local-publish')
    Assert-SourceUnchanged
    # Always exercise the normal payload, separately from coverage instrumentation.
    # Visual Windows 11/resource acceptance remains an explicit requested mode.
    $candidateHash=(Get-FileHash -LiteralPath (Join-Path $published 'NetLights.exe') -Algorithm SHA256).Hash
    $uiArguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'test-native-ui.ps1'),
        '-Executable',(Join-Path $published 'NetLights.exe'),'-ResultsDirectory',(Join-Path $results 'native-ui'),'-ExpectedSha256',$candidateHash)
    if (-not $NativeUI -and -not $ResourceRun) { $uiArguments += '-FunctionalUI' }
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
    Assert-SourceUnchanged
    # This gate is unconditional. Live public services and visual UI acceptance
    # are separate opt-in checks; neither can replace missing offline coverage.
    & (Join-Path $PSScriptRoot 'test-native-coverage.ps1') -ResultsDirectory (Join-Path $results 'coverage') -FunctionalUI
    Assert-SourceUnchanged
    if ($Legacy) { & (Join-Path $PSScriptRoot 'verify-legacy.ps1') -ResultsDirectory (Join-Path $results 'legacy') }
    Write-Host 'Real Inno update/rollback in Windows Sandbox is a separate required cutover check.'
    Assert-SourceUnchanged
    Write-Host "RUN - PASS: Rust $Configuration build, tests and mandatory offline coverage. Logs: $results"
}
finally {
    [Environment]::SetEnvironmentVariable('RUSTUP_TOOLCHAIN',$savedRustupToolchain,'Process')
    [Environment]::SetEnvironmentVariable('PYTHONOPTIMIZE',$savedPythonOptimize,'Process')
    Pop-Location
}
