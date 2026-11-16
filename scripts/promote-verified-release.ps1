#requires -Version 7.5
param(
    [ValidateSet('native','bridge')][string]$Channel = 'native',
    [string]$SourceRun,
    [string]$PayloadDirectory = 'artifacts/release-input',
    [switch]$DryRun,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ci-environment.ps1')
if (-not $SelfTest) { Assert-HostedRunner }
$repository = Split-Path -Parent $PSScriptRoot

function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-SourceChanges([string[]]$Paths) {
    $allowed = @('README.md','.github/workflows/release.yml','.github/workflows/release-bridge.yml',
        '.github/workflows/ci.yml','scripts/promote-verified-release.ps1','scripts/verify.ps1',
        '.github/workflows/promote-release.yml','docs/releases/1.6.1.md','docs/releases/1.7.0.md',
        '.cursor/skills/net-lights-ship/SKILL.md')
    Require (@($Paths | Where-Object { $_ -cnotin $allowed }).Count -eq 0) 'Product, packaging or test inputs changed after the verified CI; promotion is refused.'
}
function Assert-Run([object]$Run,[string]$Repo) {
    Require ($Run.status -ceq 'completed' -and $Run.conclusion -ceq 'success' -and $Run.path -ceq '.github/workflows/ci.yml' -and $Run.head_repository.full_name -ceq $Repo -and $Run.event -cin @('push','workflow_dispatch')) 'Source must be a successful push/manual full CI run of this repository, not a PR merge build.'
}
function Assert-Jobs([object]$Inventory) {
    Require ($Inventory.total_count -le 100) 'Source CI job inventory exceeds its bound.'
    foreach ($name in @('windows','bridge','motion','upgrade (Direct, v1.6.0)','upgrade (Direct, v1.4.2)','upgrade (Direct, v1.7.0)',
        'upgrade (Bridge, v1.6.0)','upgrade (Bridge, v1.4.2)','upgrade (Automatic, v1.6.0)','upgrade (AutomaticDirect, v1.6.0)')) {
        $matches = @($Inventory.jobs | Where-Object name -CEQ $name)
        Require ($matches.Count -eq 1 -and $matches[0].conclusion -ceq 'success') 'A mandatory source CI job is missing, skipped or failed.'
    }
}
function Assert-AutomaticReceipt([object]$Receipt) {
    Require ($Receipt.status -ceq 'PASS' -and $Receipt.cleanup -ceq 'PASS' -and $Receipt.scenario -ceq 'Automatic' -and $Receipt.releaseTag -ceq 'v1.6.0') 'The installed automatic chain receipt isn''t successful.'
    Require ($Receipt.baseline.sha256 -ieq '73d993416a298690f26891936d3b794179edc62ad86e67a7144d695840c2dd97') 'The automatic chain didn''t use the pinned published baseline.'
    $checks = @($Receipt.checks | Where-Object name -CEQ 'published-baseline-automatic-bridge-native')
    Require ($checks.Count -eq 1 -and $checks[0].status -ceq 'PASS') 'The automatic handoff evidence is missing.'
    $detail = $checks[0].detail
    foreach ($name in @('discovery','httpsDownloadAndRedirects','automaticHandoffs')) { Require ($detail.$name -ceq 'PASS') 'The discovery/download/handoff chain didn''t pass.' }
    foreach ($name in @('externalNativeMonitorSignal','settingsAndBothHistories','exactAppAndAgentDigests')) { Require ($detail.native.$name -ceq 'PASS') 'Monitor, data preservation or binary identity wasn''t verified.' }
}
function Assert-Asset([object]$Asset,[string]$Name,[string]$Sha,[long]$Bytes,[string]$Tag) {
    Require ($Sha -cmatch '^[a-fA-F0-9]{64}$' -and $Bytes -gt 0 -and $Bytes -le 80MB) 'Invalid expected installer identity.'
    Require ($Asset.name -ceq $Name -and $Asset.state -ceq 'uploaded' -and $Asset.size -eq $Bytes -and $Asset.digest -ieq ('sha256:' + $Sha)) 'The GitHub asset doesn''t match the installed-tested installer.'
    Require ($Asset.browser_download_url -ceq ('https://github.com/AryaPaw/net-lights/releases/download/' + $Tag + '/' + $Name)) 'Unexpected public installer URL.'
}
function Resolve-ReleaseId([object[]]$Inventory,[string]$Tag) {
    Require ($Inventory.Count -lt 100) 'Release inventory exceeds its bound.'
    $selected=@($Inventory | Where-Object tag_name -CEQ $Tag)
    Require ($selected.Count -eq 1 -and [string]$selected[0].id -cmatch '^[1-9][0-9]{0,15}$') 'Cannot identify exactly one release ID for this tag.'
    return [long]$selected[0].id
}
function Assert-PreparedAsset([object]$Release,[object]$Asset,[string]$Name,[string]$Sha,[long]$Bytes,[string]$Tag) {
    Require ($Release.tag_name -ceq $Tag) 'Prepared release tag differs from the authorized version.'
    if ($Release.draft) {
        $prefix='https://github.com/AryaPaw/net-lights/releases/tag/'
        Require ($Release.html_url.StartsWith($prefix,[StringComparison]::Ordinal)) 'Unexpected draft release URL.'
        $downloadTag=$Release.html_url.Substring($prefix.Length)
        Require ($downloadTag -ceq $Tag -or $downloadTag -cmatch '^untagged-[a-f0-9]{20,64}$') 'Unexpected draft download tag.'
        Assert-Asset $Asset $Name $Sha $Bytes $downloadTag
    } else { Assert-Asset $Asset $Name $Sha $Bytes $Tag }
}
function Assert-Unlinked([string]$Path) {
    for ($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)) {
        try { $attributes=[IO.File]::GetAttributes($part) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        Require (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Linked promotion inputs aren''t allowed.'
    }
}
function Read-BoundedJson([string]$Path,[long]$Limit = 128KB) {
    Assert-Unlinked $Path
    Require ((Get-Item -LiteralPath $Path).Length -le $Limit) 'Promotion receipt exceeds its bound.'
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
function Assert-File([string]$Path,[string]$Sha,[long]$Limit = 160MB) {
    Assert-Unlinked $Path
    $item=Get-Item -LiteralPath $Path
    Require (-not $item.PSIsContainer -and $item.Length -gt 0 -and $item.Length -le $Limit -and $Sha -cmatch '^[a-fA-F0-9]{64}$') 'Invalid release input.'
    Require ((Get-FileHash -LiteralPath $Path).Hash -ieq $Sha) 'Release bytes differ from the actual installed-chain receipt.'
}
function Negative([scriptblock]$Check) {
    $rejected=$false;try{& $Check}catch{$rejected=$true};Require $rejected 'A promotion negative control accepted an unsafe candidate.'
}
function Get-LatestStableTag([object[]]$Inventory) {
    Require ($Inventory.Count -lt 100) 'Release inventory exceeds its bound.'
    $versions = @{}
    foreach ($release in $Inventory) {
        if ($release.draft -or $release.prerelease) { continue }
        Require ($release.tag_name -cmatch '^v(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') 'A public stable release has an invalid version tag.'
        $version = [Version]$release.tag_name.Substring(1)
        Require ($version.Major -le 65535 -and $version.Minor -le 65535 -and $version.Build -le 65535) 'A stable release version exceeds its bound.'
        Require (-not $versions.ContainsKey($version)) 'Duplicate public stable release version.'
        $versions[$version] = [string]$release.tag_name
    }
    Require ($versions.Count -gt 0) 'No public stable release can be selected as Latest.'
    $greatest = @($versions.Keys | Sort-Object -Descending)[0]
    return $versions[$greatest]
}
function Test-PublishedReleaseVisible([object[]]$Inventory, [object]$Published) {
    Require ($Inventory.Count -lt 100) 'Release inventory exceeds its bound.'
    $matches = @($Inventory | Where-Object tag_name -CEQ $Published.tag_name)
    Require ($matches.Count -le 1) 'Duplicate published release identity.'
    if ($matches.Count -eq 0) { return $false }
    Require ($matches[0].id -eq $Published.id) 'The release list disagrees with the verified published identity.'
    return (-not $matches[0].draft -and -not $matches[0].prerelease)
}
if ($SelfTest) {
    Require (-not $SourceRun -and -not $DryRun) 'SelfTest can''t publish or validate a live release.'
    Assert-SourceChanges @('README.md','scripts/promote-verified-release.ps1')
    Negative { Assert-SourceChanges @('native/core/src/update.rs') }
    Negative { Assert-SourceChanges @('installer/net-lights.iss') }
    Negative { Assert-SourceChanges @('compat/bridge-1.6.1.patch') }
    $source=@{status='completed';conclusion='success';path='.github/workflows/ci.yml';head_repository=@{full_name='AryaPaw/net-lights'};event='workflow_dispatch'}
    Assert-Run $source 'AryaPaw/net-lights'
    $source.event='pull_request';Negative { Assert-Run $source 'AryaPaw/net-lights' }
    $names=@('windows','bridge','motion','upgrade (Direct, v1.6.0)','upgrade (Direct, v1.4.2)','upgrade (Direct, v1.7.0)','upgrade (Bridge, v1.6.0)','upgrade (Bridge, v1.4.2)','upgrade (Automatic, v1.6.0)','upgrade (AutomaticDirect, v1.6.0)')
    $jobs=@{total_count=10;jobs=@($names | ForEach-Object {@{name=$_;conclusion='success'}})}
    Assert-Jobs $jobs
    $jobs.jobs[2].conclusion='skipped';Negative { Assert-Jobs $jobs }
    $jobs.jobs[2].conclusion='success'
    $nativeDirect=@($jobs.jobs | Where-Object name -CEQ 'upgrade (Direct, v1.7.0)')[0]
    $nativeDirect.conclusion='skipped';Negative { Assert-Jobs $jobs }
    $nativeDirect.conclusion='success'
    $automaticDirect=@($jobs.jobs | Where-Object name -CEQ 'upgrade (AutomaticDirect, v1.6.0)')[0]
    $automaticDirect.conclusion='skipped';Negative { Assert-Jobs $jobs }
    $automaticDirect.conclusion='success'
    $jobs.jobs=@($jobs.jobs | Where-Object name -CNE 'upgrade (Direct, v1.7.0)');$jobs.total_count=9
    Negative { Assert-Jobs $jobs }
    $stable=@(@{tag_name='v1.6.1';draft=$false;prerelease=$false},@{tag_name='v1.7.1';draft=$false;prerelease=$false},@{tag_name='v1.10.0';draft=$false;prerelease=$false},@{tag_name='v1.9.0';draft=$false;prerelease=$false},@{tag_name='v99.0.0';draft=$true;prerelease=$false},@{tag_name='v100.0.0';draft=$false;prerelease=$true})
    Require ((Get-LatestStableTag $stable) -ceq 'v1.10.0') 'Latest must be the greatest numeric stable version, independent of publication order.'
    Require ((Get-LatestStableTag @($stable[1],$stable[0])) -ceq 'v1.7.1') 'Republishing the bridge mustn''t demote Latest.'
    Negative { Get-LatestStableTag @() }
    Negative { Get-LatestStableTag @($stable[0],$stable[0]) }
    Negative { Get-LatestStableTag @(@{tag_name='v1.07.1';draft=$false;prerelease=$false}) }
    Negative { Get-LatestStableTag @(@{tag_name='v65536.0.0';draft=$false;prerelease=$false}) }
    $visible=@{id=123;tag_name='v1.7.1';draft=$false;prerelease=$false}
    Require (Test-PublishedReleaseVisible @($visible) $visible) 'The exact published release should be visible.'
    Require (-not (Test-PublishedReleaseVisible @() $visible)) 'A stale list mustn''t confirm the new release.'
    Require (-not (Test-PublishedReleaseVisible @(@{id=123;tag_name='v1.7.1';draft=$true;prerelease=$false}) $visible)) 'A stale draft mustn''t confirm publication.'
    Negative { Test-PublishedReleaseVisible @(@{id=124;tag_name='v1.7.1';draft=$false;prerelease=$false}) $visible }
    Negative { Test-PublishedReleaseVisible @($visible,$visible) $visible }
    $receipt=@{status='PASS';cleanup='PASS';scenario='Automatic';releaseTag='v1.6.0';baseline=@{sha256='73d993416a298690f26891936d3b794179edc62ad86e67a7144d695840c2dd97'};checks=@(@{name='published-baseline-automatic-bridge-native';status='PASS';detail=@{discovery='PASS';httpsDownloadAndRedirects='PASS';automaticHandoffs='PASS';native=@{externalNativeMonitorSignal='PASS';settingsAndBothHistories='PASS';exactAppAndAgentDigests='PASS'}}})}
    Assert-AutomaticReceipt $receipt
    $receipt.checks[0].detail.native.settingsAndBothHistories='FAIL';Negative { Assert-AutomaticReceipt $receipt }
    $receipt.checks[0].detail.native.settingsAndBothHistories='PASS';$receipt.cleanup='NOT RUN';Negative { Assert-AutomaticReceipt $receipt }
    $asset=@{name='NetLights-Setup-win-x64-1.7.0.exe';state='uploaded';size=123;digest=('sha256:'+('a'*64));browser_download_url='https://github.com/AryaPaw/net-lights/releases/download/v1.7.0/NetLights-Setup-win-x64-1.7.0.exe'}
    Assert-Asset $asset $asset.name ('a'*64) 123 'v1.7.0'
    Negative { Assert-Asset $asset $asset.name ('b'*64) 123 'v1.7.0' }
    $asset.digest=$null;Negative { Assert-Asset $asset $asset.name ('a'*64) 123 'v1.7.0' }
    Require ((Resolve-ReleaseId @(@{tag_name='v1.7.0';id=123}) 'v1.7.0') -eq 123) 'Cannot resolve a valid draft ID.'
    Negative { Resolve-ReleaseId @() 'v1.7.0' }
    Negative { Resolve-ReleaseId @(@{tag_name='v1.7.0';id=1},@{tag_name='v1.7.0';id=2}) 'v1.7.0' }
    Negative { Resolve-ReleaseId @(@{tag_name='v1.7.0';id=-1}) 'v1.7.0' }
    $draft=@{tag_name='v1.7.0';draft=$true;html_url='https://github.com/AryaPaw/net-lights/releases/tag/untagged-0123456789abcdef0123'}
    $asset.digest='sha256:'+('a'*64)
    $asset.browser_download_url='https://github.com/AryaPaw/net-lights/releases/download/untagged-0123456789abcdef0123/'+$asset.name
    Assert-PreparedAsset $draft $asset $asset.name ('a'*64) 123 'v1.7.0'
    Negative { Assert-PreparedAsset $draft $asset $asset.name ('b'*64) 123 'v1.7.0' }
    Negative { Assert-PreparedAsset $draft $asset $asset.name ('a'*64) 123 'v1.6.1' }
    $draft.draft=$false;Negative { Assert-PreparedAsset $draft $asset $asset.name ('a'*64) 123 'v1.7.0' }
    $draft.draft=$true;$draft.html_url='https://github.com/other/net-lights/releases/tag/untagged-0123456789abcdef0123'
    Negative { Assert-PreparedAsset $draft $asset $asset.name ('a'*64) 123 'v1.7.0' }
    Write-Output 'RUN - PASS: release source, mandatory CI and exact public asset identity controls. No network, tag or release was changed.'
    return
}

Assert-HostedRunner
Require ($env:GITHUB_REPOSITORY -ceq 'AryaPaw/net-lights') 'Unexpected publication repository.'
$manifest=Get-Content -LiteralPath (Join-Path $repository 'Cargo.toml') -Raw
$nativeVersion=[regex]::Match($manifest,'(?m)^version = "([0-9]+\.[0-9]+\.[0-9]+)"').Groups[1].Value
Require ([bool]$nativeVersion) 'Missing canonical release version.'
$version=if($Channel -ceq 'bridge'){'1.6.1'}else{$nativeVersion}
$tag='v'+$version
$releaseCommit=(& git rev-parse HEAD)
Require ($LASTEXITCODE -eq 0 -and $releaseCommit -cmatch '^[a-f0-9]{40}$') 'Cannot identify the checked-out release commit.'
if (-not $SourceRun) {
    Require ($env:GITHUB_REF_TYPE -ceq 'tag' -and $env:GITHUB_REF_NAME -ceq $tag) 'Publication requires the exact authorized version tag.'
    $annotation=(& git for-each-ref '--format=%(contents)' ('refs/tags/'+$tag)) -join "`n"
    Require ($LASTEXITCODE -eq 0 -and $annotation.Length -le 4096) 'Release tag annotation is unavailable.'
    $matches=[regex]::Matches($annotation,'(?m)^Verified-CI-Run: ([1-9][0-9]{0,15})\s*$')
    Require ($matches.Count -eq 1) 'The annotated tag must identify one verified source CI run.'
    $SourceRun=$matches[0].Groups[1].Value
}
Require ($SourceRun -cmatch '^[1-9][0-9]{0,15}$') 'Invalid verified source run ID.'
if (-not $DryRun) { Require ($env:GITHUB_REF_TYPE -ceq 'tag' -and $env:GITHUB_REF_NAME -ceq $tag) 'A branch dry-run cannot publish.' }
function Api([string]$Path) {
    $json=& gh api ('repos/AryaPaw/net-lights/'+$Path)
    Require ($LASTEXITCODE -eq 0) 'GitHub API request failed; publication is stopped.'
    $json | ConvertFrom-Json
}
$run=Api ('actions/runs/'+$SourceRun)
Assert-Run $run $env:GITHUB_REPOSITORY
Assert-Jobs (Api ('actions/runs/'+$SourceRun+'/jobs?per_page=100'))
& git merge-base --is-ancestor $run.head_sha $env:GITHUB_SHA
Require ($LASTEXITCODE -eq 0) 'Verified source isn''t an ancestor of the release candidate.'
$paths=@(& git diff --name-only $run.head_sha $env:GITHUB_SHA)
Require ($LASTEXITCODE -eq 0) 'Cannot compare verified and release sources.'
Assert-SourceChanges $paths

$inputRoot=[IO.Path]::GetFullPath((Join-Path $repository $PayloadDirectory))
Require ($inputRoot.StartsWith((Join-Path $repository 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'Release inputs must be under repository artifacts.'
$receipt=Read-BoundedJson (Join-Path $inputRoot 'automatic/ci-upgrade-Automatic-v1-6-0/result.json')
Assert-AutomaticReceipt $receipt
Require ($receipt.native.version -ceq $nativeVersion -and $receipt.bridge.version -ceq '1.6.1') 'Tested versions don''t match the release.'
$bridgeReceipt=Read-BoundedJson (Join-Path $inputRoot 'bridge/bridge.json')
foreach($name in @('prepare','build','unit','integration','soak','coverage','publish','installer')) { Require ($bridgeReceipt.$name -ceq 'PASS') 'The compatibility bridge verification is incomplete.' }
Require ($bridgeReceipt.patchSha256 -ieq (Get-FileHash -LiteralPath (Join-Path $repository 'compat/bridge-1.6.1.patch')).Hash -and $bridgeReceipt.installerSha256 -ieq $receipt.bridge.sha256) 'The tested bridge source or installer identity is stale.'
foreach($pair in @(@{directory='native';record=$receipt.native},@{directory='bridge';record=$receipt.bridge})) {
    $directory=Join-Path $inputRoot $pair.directory
    $record=$pair.record
    Assert-File (Join-Path $directory ('setup/NetLights-Setup-win-x64-'+$record.version+'.exe')) $record.sha256 80MB
    Assert-File (Join-Path $directory 'publish/NetLights.exe') $record.appSha256
    Assert-File (Join-Path $directory 'publish/NetLights.UpdateAgent.exe') $record.agentSha256
}
$record=if($Channel -ceq 'bridge'){$receipt.bridge}else{$receipt.native}
$name='NetLights-Setup-win-x64-'+$version+'.exe'
$setup=Join-Path $inputRoot ($Channel+'/setup/'+$name)
$bytes=(Get-Item -LiteralPath $setup).Length
Write-Host ('RUN - PASS: verified source '+$run.head_sha+', CI '+$SourceRun+', exact '+$name+' SHA-256 '+$record.sha256)
if($DryRun){Write-Output 'RUN - PASS: promotion dry-run. No tag, asset or release was changed.';return}

$remoteTag=Api ('git/ref/tags/'+$tag)
if($remoteTag.object.type -ceq 'tag') {
    $tagObject=Api ('git/tags/'+$remoteTag.object.sha)
    Require ($tagObject.tag -ceq $tag -and $tagObject.object.type -ceq 'commit') 'Unexpected annotated release tag shape.'
    $remoteCommit=$tagObject.object.sha
} else { $remoteCommit=$remoteTag.object.sha }
Require ($remoteCommit -ceq $releaseCommit) 'The public tag moved away from the reviewed release commit.'
$notes=Join-Path $repository ('docs/releases/'+$version+'.md')
Assert-Unlinked $notes
Require ((Get-Item -LiteralPath $notes).Length -le 32KB) 'Release notes exceed their bound.'
$releases=@(Api 'releases?per_page=100')
Require ($releases.Count -lt 100) 'Release inventory exceeds its bound.'
if($Channel -ceq 'bridge') {
    $nativePublic=@($releases | Where-Object { $_.tag_name -ceq ('v'+$nativeVersion) -and -not $_.draft -and -not $_.prerelease })
    Require ($nativePublic.Count -eq 1) 'Publish the tested native release before enabling the legacy bridge.'
    $nativeAssets=@($nativePublic[0].assets | Where-Object name -CEQ ('NetLights-Setup-win-x64-'+$nativeVersion+'.exe'))
    Require ($nativeAssets.Count -eq 1) 'Native public asset is absent or duplicated.'
    Assert-Asset $nativeAssets[0] $nativeAssets[0].name $receipt.native.sha256 (Get-Item -LiteralPath (Join-Path $inputRoot ('native/setup/'+$nativeAssets[0].name))).Length ('v'+$nativeVersion)
}
$existing=@($releases | Where-Object tag_name -CEQ $tag)
Require ($existing.Count -le 1) 'Ambiguous release identity.'
if(-not $existing.Count) {
    & gh release create $tag --repo AryaPaw/net-lights --verify-tag --target $releaseCommit --draft --latest=false --title $tag --notes-file (Join-Path $repository ('docs/releases/'+$version+'.md'))
    Require ($LASTEXITCODE -eq 0) 'Draft release creation failed.'
}
$visibilityDeadline=[Diagnostics.Stopwatch]::StartNew()
do {
    $inventory=@(Api 'releases?per_page=100')
    if (@($inventory | Where-Object tag_name -CEQ $tag).Count -gt 0) { break }
    Require ($visibilityDeadline.Elapsed.TotalSeconds -lt 60) 'GitHub hasn''t exposed the created draft in the release inventory.'
    Start-Sleep -Seconds 2
} while ($true)
$releaseId=Resolve-ReleaseId $inventory $tag
$current=Api ('releases/'+$releaseId)
$assets=@($current.assets | Where-Object name -CEQ $name)
Require ($assets.Count -le 1 -and @($current.assets | Where-Object name -CNE $name).Count -eq 0) 'Unexpected release assets; refusing to overwrite or delete them.'
if(-not $assets.Count) {
    Require ([bool]$current.draft) 'A public release without its tested asset won''t be modified.'
    & gh release upload $tag $setup --repo AryaPaw/net-lights
    Require ($LASTEXITCODE -eq 0) 'Tested asset upload failed; draft is retained.'
}
$deadline=[Diagnostics.Stopwatch]::StartNew()
do {
    $current=Api ('releases/'+$releaseId)
    $assets=@($current.assets | Where-Object name -CEQ $name)
    Require ($assets.Count -eq 1) 'Uploaded asset is missing or duplicated.'
    if($assets[0].digest){break}
    Require ($deadline.Elapsed.TotalSeconds -lt 60) 'GitHub hasn''t supplied the mandatory installer digest; draft is retained.'
    Start-Sleep -Seconds 2
} while($true)
Assert-PreparedAsset $current $assets[0] $name $record.sha256 $bytes $tag
& gh release edit $tag --repo AryaPaw/net-lights --draft=false --prerelease=false --latest=false --title $tag --notes-file (Join-Path $repository ('docs/releases/'+$version+'.md'))
Require ($LASTEXITCODE -eq 0) 'Release publication failed.'
$published=Api ('releases/tags/'+$tag)
Require (-not $published.draft -and -not $published.prerelease) 'Release isn''t publicly stable.'
Assert-Asset @($published.assets | Where-Object name -CEQ $name)[0] $name $record.sha256 $bytes $tag
$visibilityDeadline.Restart()
do {
    $inventory=@(Api 'releases?per_page=100')
    if (Test-PublishedReleaseVisible $inventory $published) { break }
    Require ($visibilityDeadline.Elapsed.TotalSeconds -lt 60) 'GitHub hasn''t exposed the verified public release in the inventory; Latest wasn''t changed.'
    Start-Sleep -Seconds 2
} while ($true)
$expectedLatest = Get-LatestStableTag $inventory
& gh release edit $expectedLatest --repo AryaPaw/net-lights --latest=true
Require ($LASTEXITCODE -eq 0) 'Cannot mark the greatest public stable version as Latest.'
$deadline.Restart()
do {
    $publicLatest=Api 'releases/latest'
    if($publicLatest.tag_name -ceq $expectedLatest){break}
    Require ($deadline.Elapsed.TotalSeconds -lt 60) 'Unexpected public latest routing after publication.'
    Start-Sleep -Seconds 2
} while($true)
Write-Output ('RUN - PASS: published '+$tag+' using the exact installed-tested CI installer. Latest='+$publicLatest.tag_name)
