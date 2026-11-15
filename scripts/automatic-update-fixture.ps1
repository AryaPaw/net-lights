# Dot-sourced only by test-ci-upgrade.ps1. Operational functions retain its
# hosted-runner guard, owned installation, seed and bounded process helpers.

function New-AutomaticFeedCertificate {
    $now = [DateTimeOffset]::UtcNow
    $rootKey = [Security.Cryptography.RSA]::Create(2048)
    $leafKey = [Security.Cryptography.RSA]::Create(2048)
    $root = $null; $unsignedLeaf = $null; $leaf = $null
    try {
        $sha = [Security.Cryptography.HashAlgorithmName]::SHA256
        $padding = [Security.Cryptography.RSASignaturePadding]::Pkcs1
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            ('CN=NetLights CI ' + [Guid]::NewGuid().ToString('N')), $rootKey, $sha, $padding)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,$true))
        $root = $request.CreateSelfSigned($now.AddMinutes(-1),$now.AddMinutes(15))
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=api.github.com',$leafKey,$sha,$padding)
        $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        foreach ($hostName in @('github.com','api.github.com','release-assets.githubusercontent.com')) { $san.AddDnsName($hostName) }
        $request.CertificateExtensions.Add($san.Build())
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
        $oids = [Security.Cryptography.OidCollection]::new()
        $null = $oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids,$true))
        $serial = [byte[]]::new(16)
        [Security.Cryptography.RandomNumberGenerator]::Fill($serial)
        $unsignedLeaf = $request.Create($root,$now.AddMinutes(-1),$now.AddMinutes(12),$serial)
        return @{ root = $root; leafPem = $unsignedLeaf.ExportCertificatePem() + "`n" + $root.ExportCertificatePem(); keyPem = $leafKey.ExportPkcs8PrivateKeyPem() }
    } catch {
        if ($root) { $root.Dispose() }
        throw
    } finally {
        if ($unsignedLeaf) { $unsignedLeaf.Dispose() }
        $leafKey.Dispose(); $rootKey.Dispose()
    }
}

function Test-AutomaticFeedCertificate {
    $certificates = New-AutomaticFeedCertificate
    $leaf = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($certificates.leafPem)
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $null = $chain.ChainPolicy.CustomTrustStore.Add($certificates.root)
        Assert-Condition ($chain.Build($leaf)) 'The isolated feed certificate doesn''t form a valid custom-root chain.'
        $chain.ChainPolicy.CustomTrustStore.Clear()
        Assert-Condition (-not $chain.Build($leaf)) 'An untrusted fixture certificate was accepted by the negative control.'
    } finally { $chain.Dispose(); $leaf.Dispose(); $certificates.root.Dispose() }
}

function Invoke-AutomaticUpgrade {
    Assert-HostedRunner
    Assert-Condition ($ownsFixture -and $ReleaseTag -ceq 'v1.6.0') 'Automatic chain requires the owned pinned 1.6.0 baseline.'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { Assert-Condition ([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'The disposable fixture requires an administrative hosted-runner token.' }
    finally { $identity.Dispose() }
    $private = Join-Path ([IO.Path]::GetTempPath()) ('NetLightsAutoUpdate-' + [Guid]::NewGuid().ToString('N'))
    Assert-Unlinked $private
    Assert-Condition (-not [IO.Path]::Exists($private)) 'Automatic feed directory already exists.'
    $null = [IO.Directory]::CreateDirectory($private)
    $certificates = $null; $trust = $null; $proxy = $null; $rootAdded = $false
    $savedProxy = @{}
    foreach ($name in @('HTTPS_PROXY','HTTP_PROXY','ALL_PROXY','NO_PROXY')) { $savedProxy[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
    $chainStarted = [DateTimeOffset]::UtcNow
    try {
        $certificates = New-AutomaticFeedCertificate
        [IO.File]::WriteAllText((Join-Path $private 'leaf.pem'),$certificates.leafPem,$utf8)
        [IO.File]::WriteAllText((Join-Path $private 'key.pem'),$certificates.keyPem,$utf8)
        $payloads = @{}
        foreach ($payload in @($bridge,$native)) {
            $payloads[$payload.version] = @{ path = $payload.installer; sha256 = $payload.sha256; bytes = (Get-Item -LiteralPath $payload.installer).Length }
        }
        [IO.File]::WriteAllText((Join-Path $private 'config.json'),(@{payloads=$payloads} | ConvertTo-Json -Depth 5),$utf8)
        # CurrentUser Root invokes a native confirmation dialog. Only inside
        # this administrative disposable VM, machine Root is noninteractive.
        # https://github.com/dotnet/runtime/issues/24160
        Write-Host 'RUN: trust ephemeral HTTPS fixture in the disposable VM'
        $trust = [Security.Cryptography.X509Certificates.X509Store]::new('Root','LocalMachine')
        $trust.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        Assert-Condition (@($trust.Certificates | Where-Object Thumbprint -CEQ $certificates.root.Thumbprint).Count -eq 0) 'The ephemeral certificate already exists.'
        $publicRoot = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificates.root.RawData)
        try { $trust.Add($publicRoot); $rootAdded = $true } finally { $publicRoot.Dispose() }
        Write-Host 'RUN: start bounded HTTPS fixture'
        $python = (& uv --cache-dir (Join-Path $repository 'artifacts/uv-cache') python find 3.11)
        Assert-Condition ($LASTEXITCODE -eq 0 -and [IO.File]::Exists($python)) 'The pinned fixture Python runtime is unavailable.'
        # uv's minor-version directory can be a junction to its exact runtime.
        # Resolve that tool-owned alias, then apply the unchanged no-link guard.
        $runtimeDirectory = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($python))
        if ($runtimeDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $runtimeDirectory = $runtimeDirectory.ResolveLinkTarget($true)
            Assert-Condition ($null -ne $runtimeDirectory -and $runtimeDirectory -is [IO.DirectoryInfo]) 'The Python runtime alias doesn''t resolve to a directory.'
        }
        $python = Join-Path $runtimeDirectory.FullName ([IO.Path]::GetFileName($python))
        $proxy = Start-Owned $python @((Join-Path $PSScriptRoot 'fixtures/automatic-update-feed.py'),'--directory',$private)
        $wait = [Diagnostics.Stopwatch]::StartNew()
        $ready = $null
        while (-not $ready) {
            $proxy.Refresh()
            Assert-Condition (-not $proxy.HasExited -and $wait.Elapsed.TotalSeconds -lt 15) 'The bounded automatic HTTPS fixture didn''t start.'
            $readyPath = Join-Path $private 'ready.json'
            if ([IO.File]::Exists($readyPath)) {
                try { $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json } catch { $ready = $null }
            }
            if (-not $ready) { Start-Sleep -Milliseconds 25 }
        }
        Assert-Condition ($ready.port -gt 0 -and $ready.port -le 65535) 'The loopback fixture returned an invalid port.'
        foreach ($name in @('HTTPS_PROXY','HTTP_PROXY','ALL_PROXY')) { [Environment]::SetEnvironmentVariable($name,('http://127.0.0.1:' + $ready.port),'Process') }
        [Environment]::SetEnvironmentVariable('NO_PROXY','127.0.0.1,localhost','Process')
        # Install-Baseline enables automatic updates for this scenario only.
        # No agent command, staged installer or --exit-local handoff is issued
        # by this test between the baseline and the final 1.7.0 process.
        $script:automaticChainRunning = $true
        Write-Host 'RUN: install unchanged published 1.6.0 with automatic updates enabled'
        $baselineAgentSha = Install-Baseline
        $baselinePid = (Get-App).Id
        [IO.File]::WriteAllText((Join-Path $private 'armed-1.6.1'),'go',$utf8)
        Write-Host 'RUN: wait for automatic 1.6.0 -> 1.6.1 -> 1.7.0 without delaying the second hop'
        # The bridge feed is immediately available. Holding it until the bridge
        # has lived for several seconds would hide overlapping update handoffs.
        $wait.Restart()
        $final = $null
        while (-not $final) {
            Assert-Condition ($wait.Elapsed.TotalSeconds -lt 240) 'The automatic two-hop chain didn''t install and start native 1.7.0.'
            $statusPath = Join-Path $updates 'result.json'
            if ([IO.File]::Exists($statusPath)) {
                $candidate = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if ($candidate.version -ceq 'NetLights-Setup-win-x64-1.7.0.exe' -and $candidate.success -eq $true) { $final = $candidate }
            }
            if (-not $final) { Start-Sleep -Milliseconds 100 }
        }
        Assert-AttemptResult $final 'NetLights-Setup-win-x64-1.7.0.exe' $chainStarted ([DateTimeOffset]::UtcNow)
        $owner = Get-App
        Assert-Condition ($owner.Id -ne $baselinePid) 'The final process identity wasn''t replaced.'
        Assert-Live $owner
        Write-Host 'RUN: verify native process, both real agent handoffs and persisted data'
        $stagedAgents = @(foreach ($directory in Get-ChildItem -LiteralPath $updates -Directory) {
            if ($directory.Name -cmatch '^[0-9a-f]{32}$') {
                $path = Join-Path $directory.FullName 'NetLights.UpdateAgent.exe'
                if ([IO.File]::Exists($path)) { Assert-Unlinked $path; @{version=Read-Version $path; sha256=(Get-FileHash -LiteralPath $path).Hash} }
            }
        })
        foreach ($expected in @(@{version='1.6.0';sha256=$baselineAgentSha},@{version='1.6.1';sha256=$bridge.agentSha256})) {
            Assert-Condition (@($stagedAgents | Where-Object { $_.version -ceq $expected.version -and $_.sha256 -ieq $expected.sha256 }).Count -eq 1) 'The automatic handoff didn''t stage the exact installed agent for both hops.'
        }
        $phasesPath = Join-Path $updates 'phases.jsonl'
        Assert-Condition ((Get-Item -LiteralPath $phasesPath).Length -le 2MB) 'The agent journal exceeds its bound.'
        $phases = @(Get-Content -LiteralPath $phasesPath | ForEach-Object { $_ | ConvertFrom-Json })
        $agents = @($phases | Where-Object { $_.phase -ceq 'agent-start' } | Select-Object -ExpandProperty pid -Unique)
        Assert-Condition ($agents.Count -eq 2) 'The automatic chain didn''t produce exactly two real agent handoffs.'
        foreach ($agentPid in $agents) {
            foreach ($phase in @('installed-files-backed-up','installer-started','version-confirmed','process-restarted')) {
                Assert-Condition (@($phases | Where-Object { $_.pid -eq $agentPid -and $_.phase -ceq $phase }).Count -eq 1) 'An automatic update agent didn''t confirm all successful stages.'
            }
        }
        $nativeEvidence = Assert-Native @{exitCode=0;success=$final.success}
        $lifecycle = @(foreach ($file in Get-ChildItem -LiteralPath (Join-Path $data 'diagnostics') -Filter 'runs-*.jsonl' -File) {
            Assert-Unlinked $file.FullName
            Assert-Condition ($file.Length -le 2MB) 'The fixture lifecycle journal exceeds its segment bound.'
            Get-Content -LiteralPath $file.FullName | ForEach-Object { $_ | ConvertFrom-Json }
        })
        $bridgeRuns = @($lifecycle | Where-Object { $_.version -ceq '1.6.1' -and $_.code -ceq 'message-loop-starting' })
        Assert-Condition ($bridgeRuns.Count -eq 1) 'The automatic chain didn''t run exactly one real bridge application.'
        $bridgePid = $bridgeRuns[0].pid
        foreach ($code in @('monitor-ready','update-agent-ready-exit-requested')) {
            Assert-Condition (@($lifecycle | Where-Object { $_.pid -eq $bridgePid -and $_.version -ceq '1.6.1' -and $_.code -ceq $code }).Count -eq 1) 'The bridge didn''t confirm monitoring and automatic handoff.'
        }
        Assert-Condition ($owner.Id -ne $bridgePid) 'The final process is still the bridge.'
        $events = Get-Content -LiteralPath (Join-Path $private 'events.json') -Raw | ConvertFrom-Json
        Assert-Condition (@($events | Where-Object { $_.code -match 'reject|error' }).Count -eq 0) 'The automatic fixture rejected a request or had a handler error.'
        foreach ($version in @('1.6.1','1.7.0')) {
            foreach ($code in @('redirect-request','payload-request','payload-complete')) {
                Assert-Condition (@($events | Where-Object { $_.code -ceq $code -and $_.version -ceq $version }).Count -eq 1) 'The application didn''t download exactly one validated payload for each hop.'
            }
        }
        foreach ($code in @('latest-request','stable-request')) {
            Assert-Condition (@($events | Where-Object { $_.code -ceq $code }).Count -ge 1) 'The installed application didn''t query both release channels.'
        }
        return @{ discovery='PASS'; httpsDownloadAndRedirects='PASS'; automaticHandoffs='PASS';
            fixture='isolated loopback HTTPS release service, real unmodified apps/agents/installers'; publicPublishedChannel='NOT RUN';
            baselinePid=$baselinePid; bridgePid=$bridgePid; nativePid=$owner.Id; agents=$stagedAgents; native=$nativeEvidence }
    } finally {
        # Stop only this test's app while the private trust/proxy still exist.
        try { Stop-App -CleanupOnly } finally {
            $script:automaticChainRunning = $false
            try {
                foreach ($name in $savedProxy.Keys) { [Environment]::SetEnvironmentVariable($name,$savedProxy[$name],'Process') }
            } finally {
                try { if ($proxy) { Stop-OwnedTree $proxy } } finally {
                    try {
                        if ($trust -and $rootAdded) {
                            $trust.Remove($certificates.root)
                            Assert-Condition (@($trust.Certificates | Where-Object Thumbprint -CEQ $certificates.root.Thumbprint).Count -eq 0) 'Ephemeral trust cleanup failed.'
                        }
                    } finally {
                        if ($trust) { $trust.Dispose() }
                        if ($certificates) { $certificates.root.Dispose() }
                    }
                }
            }
        }
        # Archive bounded, whitelisted observations before deleting private keys.
        foreach ($name in @('events.json','ready.json')) {
            $path = Join-Path $private $name
            if ([IO.File]::Exists($path)) {
                Assert-Condition ((Get-Item -LiteralPath $path).Length -le 128KB) 'Automatic fixture evidence exceeds its bound.'
                Copy-Item -LiteralPath $path -Destination (Join-Path $results ('automatic-' + $name))
            }
        }
        Assert-Unlinked $private
        $entries = @(Get-ChildItem -LiteralPath $private -Force)
        foreach ($entry in $entries) {
            Assert-Condition (-not $entry.PSIsContainer -and $entry.Name -cin @('key.pem','leaf.pem','config.json','ready.json','events.json','events.tmp','armed-1.6.1')) 'Unexpected automatic fixture contents prevent cleanup.'
            Assert-Unlinked $entry.FullName
        }
        Remove-Item -LiteralPath $private -Recurse -Force
    }
}
