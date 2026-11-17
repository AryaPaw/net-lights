param([switch]$SelfTest,[switch]$DefinitionsOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($SelfTest -and $DefinitionsOnly) { throw 'Choose either definitions or self-tests.' }

function Get-VerificationInputClass([string]$File) {
    $capture=@('AGENTS.md','.cursor/skills/net-lights-ship/SKILL.md','scripts/native-ui-smoke.py',
        'scripts/test-native-ui.ps1','.github/workflows/native-ui-diagnostics.yml')
    if ($File -cin $capture -or $File.StartsWith('scripts/fixtures/native-frameprobe/',[StringComparison]::Ordinal)) { return 'capture' }
    if ($File -cin @('scripts/verify.ps1','scripts/select-verification-scope.ps1','scripts/check-workflows.ps1','.github/workflows/ci.yml')) { return 'verification' }
    return 'product'
}

function Test-UnchangedApplicationInputs([string[]]$Files) {
    foreach($file in $Files) { if ((Get-VerificationInputClass $file) -ceq 'product') { return $false } }
    return $true
}

function Get-VerificationScope([string]$EventName,[string]$Ref,[bool]$HasBase,[string[]]$Files) {
    # Only the explicitly designated debug branch may use a narrower push check.
    # Main, PRs, tags and explicit final runs always retain the full pipeline.
    if ($EventName -cne 'push' -or $Ref -cne 'refs/heads/codex/1.7.0-ci' -or -not $HasBase -or -not $Files.Count) { return 'full' }
    foreach($file in $Files) {
        if ((Get-VerificationInputClass $file) -cne 'capture') { return 'full' }
    }
    return 'capture'
}
if ($DefinitionsOnly) { return }

if ($SelfTest) {
    foreach($file in @('scripts/native-ui-smoke.py','scripts/fixtures/native-frameprobe/src/capture.rs','AGENTS.md')) {
        if ((Get-VerificationScope 'push' 'refs/heads/codex/1.7.0-ci' $true @($file)) -cne 'capture') { throw 'A capture-only debug change did not select focused verification.' }
    }
    foreach($event in @('pull_request','workflow_dispatch')) {
        if ((Get-VerificationScope $event 'refs/heads/codex/1.7.0-ci' $true @('scripts/native-ui-smoke.py')) -cne 'full') { throw 'An explicit final run or PR lost full verification.' }
    }
    foreach($ref in @('refs/heads/main','refs/tags/v1.7.1','refs/heads/codex/other')) {
        if ((Get-VerificationScope 'push' $ref $true @('scripts/native-ui-smoke.py')) -cne 'full') { throw 'The focused scope escaped its designated debug branch.' }
    }
    foreach($file in @('native/app/src/main.rs','native/core/src/lib.rs','Cargo.lock','installer/net-lights.iss',
        '.github/workflows/ci.yml','scripts/verify.ps1','scripts/select-verification-scope.ps1','scripts/fixtures/native-frameprobe-other/file.rs')) {
        if ((Get-VerificationScope 'push' 'refs/heads/codex/1.7.0-ci' $true @('scripts/native-ui-smoke.py',$file)) -cne 'full') { throw 'A mixed or gate-changing update bypassed full verification.' }
    }
    if ((Get-VerificationScope 'push' 'refs/heads/codex/1.7.0-ci' $false @('scripts/native-ui-smoke.py')) -cne 'full' -or
        (Get-VerificationScope 'push' 'refs/heads/codex/1.7.0-ci' $true @()) -cne 'full') { throw 'An unknown or empty comparison bypassed full verification.' }
    foreach($file in @('scripts/check-workflows.ps1','scripts/select-verification-scope.ps1','.github/workflows/ci.yml','scripts/fixtures/native-frameprobe/src/capture.rs')) {
        if (-not (Test-UnchangedApplicationInputs @($file))) { throw 'An observer-only change prevented reuse of unchanged application inputs.' }
    }
    foreach($file in @('native/app/src/main.rs','native/core/src/lib.rs','Cargo.lock','installer/net-lights.iss','scripts/publish.ps1','unknown-file')) {
        if (Test-UnchangedApplicationInputs @('scripts/check-workflows.ps1',$file)) { throw 'Product or unknown inputs bypassed the exact-payload reuse guard.' }
    }
    Write-Host 'RUN - PASS: focused debug-only scope, mixed inputs, missing base, PR, main, tags and explicit final controls.'
    return
}

$scope='full'
if ($env:GITHUB_EVENT_NAME -ceq 'push' -and $env:GITHUB_REF -ceq 'refs/heads/codex/1.7.0-ci') {
    if ($env:GITHUB_EVENT_PATH -and (Get-Item -LiteralPath $env:GITHUB_EVENT_PATH).Length -le 1MB) {
        $event=Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
        $before=[string]$event.before
        if ($before -cmatch '^[a-f0-9]{40}$' -and $before -cne ('0'*40)) {
            git cat-file -e "${before}^{commit}" 2>$null
            if ($LASTEXITCODE -eq 0) {
                $files=@(git diff --name-only --no-renames $before HEAD)
                if ($LASTEXITCODE -ne 0) { throw 'Changed verification input inventory failed.' }
                $scope=Get-VerificationScope $env:GITHUB_EVENT_NAME $env:GITHUB_REF $true $files
            }
        }
    }
}
if (-not $env:GITHUB_OUTPUT) { throw 'This selector requires the GitHub step output channel.' }
"scope=$scope" >> $env:GITHUB_OUTPUT
Write-Host "Verification scope: $scope. A focused capture run doesn't replace final full CI."
