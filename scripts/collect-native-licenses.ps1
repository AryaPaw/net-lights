#requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Collect')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Collect')][string]$OutputDirectory,
    [Parameter(ParameterSetName = 'Collect')][string]$SupplementDirectory,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')][switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$script:maximumFileBytes = 1MB
$script:maximumPackageBytes = 8MB
$script:maximumTotalBytes = 64MB
$script:copiedBytes = 0L
$script:copiedFiles = 0
$script:collectionClock = [Diagnostics.Stopwatch]::StartNew()
$script:standardDefinitions = @{
    'https://www.apache.org/licenses/LICENSE-2.0.txt' = @{ choice = 'Apache-2.0'; sha256 = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30' }
    'https://spdx.org/licenses/MIT.txt' = @{ choice = 'MIT'; sha256 = 'c3b1b78bc8bd3ea13aa4bc9778442d16560270afa235006d816e5e88cef24db4' }
}

function Assert-TimeBudget { if ($script:collectionClock.Elapsed.TotalSeconds -gt 300) { throw 'License collection exceeded its 300-second limit.' } }

function Test-NoticeName([string]$Name) {
    return $Name -match '^(LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE|UNLICENSE|AUTHORS|OFL)(?:$|[._-])' -and
        [IO.Path]::GetExtension($Name) -notin @('.svg', '.png', '.jpg', '.ico', '.woff', '.woff2', '.rs', '.c', '.cpp', '.js', '.py')
}

function Assert-Unlinked([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        try {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked path rejected: $current" }
        }
        catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-InRoot([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -and
        -not $target.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path leaves the owned directory.' }
}

function New-OwnedDirectory([string]$Root, [string]$Path) {
    Assert-InRoot $Root $Path
    Assert-Unlinked $Path
    if ([IO.File]::Exists($Path)) { throw 'Expected an owned directory, found a file.' }
    [void][IO.Directory]::CreateDirectory($Path)
    Assert-Unlinked $Path
}

function Write-NewText([string]$Root, [string]$Path, [string]$Text) {
    Assert-InRoot $Root $Path
    Assert-Unlinked $Path
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    if ($bytes.Length -gt 8MB) { throw 'Generated license index exceeds its limit.' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Read-CargoMetadata {
    $cargo = (Get-Command cargo -CommandType Application -ErrorAction Stop).Source
    $start = [Diagnostics.ProcessStartInfo]::new($cargo)
    $start.WorkingDirectory = $repositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($argument in @('metadata', '--locked', '--offline', '--format-version', '1', '--filter-platform', 'x86_64-pc-windows-msvc', '--manifest-path', (Join-Path $repositoryRoot 'Cargo.toml'))) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    try {
        [void]$process.Start()
        $started = $true
        $readers = @($process.StandardOutput, $process.StandardError)
        $buffers = @([char[]]::new(8192), [char[]]::new(4096))
        $texts = @([Text.StringBuilder]::new(), [Text.StringBuilder]::new())
        $limits = @(16MB, 64KB)
        $finished = @($false, $false)
        $tasks = @($readers[0].ReadAsync($buffers[0], 0, $buffers[0].Length), $readers[1].ReadAsync($buffers[1], 0, $buffers[1].Length))
        while (-not ($finished[0] -and $finished[1] -and $process.HasExited)) {
            if ($clock.Elapsed.TotalSeconds -gt 120) { throw 'cargo metadata exceeded 120 seconds; no package code was built.' }
            $progress = $false
            for ($index = 0; $index -lt 2; $index++) {
                if ($finished[$index] -or -not $tasks[$index].IsCompleted) { continue }
                $count = $tasks[$index].GetAwaiter().GetResult()
                if ($count -eq 0) { $finished[$index] = $true }
                else {
                    if ($texts[$index].Length + $count -gt $limits[$index]) { throw 'cargo metadata output exceeded its bounded capture limit.' }
                    [void]$texts[$index].Append($buffers[$index], 0, $count)
                    $tasks[$index] = $readers[$index].ReadAsync($buffers[$index], 0, $buffers[$index].Length)
                }
                $progress = $true
            }
            if (-not $progress) { Start-Sleep -Milliseconds 20 }
        }
        if ($process.ExitCode -ne 0) { throw "cargo metadata failed ($($process.ExitCode)): $($texts[1].ToString())" }
        return $texts[0].ToString() | ConvertFrom-Json -Depth 100
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Find-LicenseFiles([string]$Root, [AllowNull()][string]$DeclaredFile) {
    Assert-Unlinked $Root
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $warnings = [Collections.Generic.List[string]]::new()
    $failures = [Collections.Generic.List[string]]::new()
    if ($DeclaredFile) {
        $declared = [IO.Path]::GetFullPath($DeclaredFile, $Root)
        try {
            Assert-InRoot $Root $declared
            Assert-Unlinked $declared
            if ([IO.Path]::GetRelativePath($Root, $declared).Split([char[]]@('\', '/')).Length -gt 5) { throw 'The declared license file exceeds the four-directory depth limit.' }
            if (-not [IO.File]::Exists($declared)) { throw 'The declared license_file is absent from the published package.' }
            [void]$files.Add($declared)
        }
        catch { $failures.Add('declared-license-file-unavailable-or-rejected-path') }
    }
    $pending = [Collections.Generic.Queue[object]]::new()
    $pending.Enqueue(@{ Path = $Root; Depth = 0; LicenseTree = $false })
    $visited = 0
    while ($pending.Count) {
        $directory = $pending.Dequeue()
        Assert-Unlinked $directory.Path
        foreach ($entry in [IO.DirectoryInfo]::new($directory.Path).EnumerateFileSystemInfos()) {
            Assert-TimeBudget
            if (++$visited -gt 50000) { throw 'Package inventory exceeds 50000 entries.' }
            $relative = [IO.Path]::GetRelativePath($Root, $entry.FullName).Replace('\', '/')
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { if ($warnings.Count -lt 64) { $warnings.Add("skipped-linked-entry:$relative") }; continue }
            if (($entry.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                if ($directory.Depth -lt 4 -and $entry.Name -notin @('.git', '.hg', '.svn', 'target', 'node_modules')) {
                    $pending.Enqueue(@{ Path = $entry.FullName; Depth = $directory.Depth + 1; LicenseTree = ($directory.LicenseTree -or $entry.Name -match '^(licenses?|licences?|notices?)$') })
                    if ($pending.Count -gt 4096) { throw 'Package inventory exceeds its directory limit.' }
                }
            }
            elseif ($directory.LicenseTree -or (Test-NoticeName $entry.Name)) { [void]$files.Add($entry.FullName) }
            if ($files.Count -gt 64) { throw 'Package contains more than 64 notice files.' }
        }
    }
    return @{ Files = @($files | Sort-Object); Warnings = @($warnings.ToArray()); Failures = @($failures.ToArray()) }
}

function Copy-LicenseFile([string]$SourceRoot, [string]$Source, [string]$DestinationRoot) {
    Assert-InRoot $SourceRoot $Source
    Assert-Unlinked $Source
    $relative = [IO.Path]::GetRelativePath($SourceRoot, $Source)
    $destination = Join-Path $DestinationRoot $relative
    New-OwnedDirectory $DestinationRoot ([IO.Path]::GetDirectoryName($destination))
    Assert-Unlinked $destination
    $input = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $output = $null
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        if ($input.Length -gt $script:maximumFileBytes -or $script:copiedFiles -ge 4096) { throw 'License file size or count exceeds its limit.' }
        $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = [byte[]]::new(16384)
        $count = 0L
        while (($read = $input.Read($buffer, 0, $buffer.Length)) -ne 0) {
            Assert-TimeBudget
            $count += $read
            if ($count -gt $script:maximumFileBytes -or $script:copiedBytes + $read -gt $script:maximumTotalBytes) { throw 'Copied license data exceeds its limit.' }
            $output.Write($buffer, 0, $read)
            $hash.AppendData($buffer, 0, $read)
            $script:copiedBytes += $read
        }
        $output.Flush($true)
        $script:copiedFiles++
        return [ordered]@{ path = $relative.Replace('\', '/'); bytes = $count; sha256 = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant() }
    }
    finally { $input.Dispose(); if ($output) { $output.Dispose() }; $hash.Dispose() }
}

function Safe-Repository([AllowNull()][string]$Value) {
    if (-not $Value) { return $null }
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('https', 'http', 'git', 'ssh')) { return $null }
    $builder = [UriBuilder]::new($uri)
    $builder.UserName = ''; $builder.Password = ''; $builder.Query = ''; $builder.Fragment = ''
    return $builder.Uri.AbsoluteUri
}

function Read-BoundedBytes([string]$Path, [long]$Maximum = 1MB) {
    Assert-Unlinked $Path
    $input = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $bytes = [IO.MemoryStream]::new()
    try {
        if ($input.Length -gt $Maximum) { throw 'Proof input exceeds its size limit.' }
        $buffer = [byte[]]::new(16384)
        while (($read = $input.Read($buffer, 0, $buffer.Length)) -ne 0) {
            if ($bytes.Length + $read -gt $Maximum) { throw 'Proof input grew beyond its size limit.' }
            $bytes.Write($buffer, 0, $read)
        }
        return ,$bytes.ToArray()
    }
    finally { $input.Dispose(); $bytes.Dispose() }
}

function Get-LockedPackageChecksum([string]$Name, [string]$Version, [string]$Source) {
    $text = [Text.Encoding]::UTF8.GetString((Read-BoundedBytes (Join-Path $repositoryRoot 'Cargo.lock')))
    $matchingPackages = @([regex]::Split($text, '(?m)^\[\[package\]\]\s*$') | Where-Object {
        $_ -match ('(?m)^name = "' + [regex]::Escape($Name) + '"\s*$') -and
        $_ -match ('(?m)^version = "' + [regex]::Escape($Version) + '"\s*$') -and
        $_ -match ('(?m)^source = "' + [regex]::Escape($Source) + '"\s*$')
    })
    if ($matchingPackages.Count -ne 1 -or $matchingPackages[0] -notmatch '(?m)^checksum = "([a-f0-9]{64})"\s*$') { throw 'Published package has no unique locked registry checksum.' }
    return $Matches[1]
}

function Assert-ArchivedDeclaration([string]$Manifest, [string]$Name, [string]$Version, [string]$PackageChecksum, [string]$DeclarationChecksum) {
    $package = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($Manifest))
    if ($package.Name -cne "$Name-$Version" -or $package.Parent.Parent.Name -cne 'src') { throw 'Published registry package path is inconsistent.' }
    $archive = Join-Path $package.Parent.Parent.Parent.FullName ("cache/$($package.Parent.Name)/$Name-$Version.crate")
    $bytes = Read-BoundedBytes $archive 16MB
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $PackageChecksum) { throw 'Cached registry archive differs from Cargo.lock.' }
    $memory = [IO.MemoryStream]::new($bytes, $false)
    $gzip = [IO.Compression.GZipStream]::new($memory, [IO.Compression.CompressionMode]::Decompress)
    $tar = [System.Formats.Tar.TarReader]::new($gzip)
    try {
        $count = 0; $total = 0L; $found = $false
        while ($entry = $tar.GetNextEntry($false)) {
            Assert-TimeBudget
            if (++$count -gt 1024 -or $entry.Length -gt 16MB -or ($total += $entry.Length) -gt 64MB) { throw 'Published archive proof exceeds its inventory limit.' }
            if ($entry.Name -cne "$Name-$Version/Cargo.toml") { continue }
            if ($found -or $entry.Length -gt 1MB -or -not $entry.DataStream) { throw 'Published manifest entry is invalid or ambiguous.' }
            $found = $true; $data = [IO.MemoryStream]::new()
            try {
                $buffer = [byte[]]::new(8192)
                while (($read = $entry.DataStream.Read($buffer, 0, $buffer.Length)) -ne 0) {
                    if ($data.Length + $read -gt 1MB) { throw 'Published manifest proof exceeds its size limit.' }
                    $data.Write($buffer, 0, $read)
                }
                if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data.ToArray())) -ne $DeclarationChecksum) { throw 'Declaration differs from the checksummed published archive.' }
            }
            finally { $data.Dispose() }
        }
        if (-not $found) { throw 'Published archive contains no exact Cargo.toml declaration.' }
    }
    finally { $tar.Dispose(); $gzip.Dispose(); $memory.Dispose() }
}

function Copy-DeclarationProof($Proof, [string]$Directory, [string]$Destination, [string]$Name, [string]$Version,
    [string]$Spdx, [string[]]$Authors, [string]$Manifest, [string]$PackageChecksum, [long]$AvailableBytes) {
    if (-not $Proof -or $Proof.name -cne $Name -or $Proof.version -cne $Version -or $Proof.spdx -cne $Spdx -or $Spdx -cne 'MIT' -or
        $Proof.sha256 -notmatch '^[a-f0-9]{64}$' -or $Proof.registryPackageChecksum -cne $PackageChecksum -or $PackageChecksum -notmatch '^[a-f0-9]{64}$') { throw 'Standard MIT declaration package, version, license or checksum mismatch.' }
    $declaredAuthors = @($Proof.authors)
    if ($declaredAuthors.Count -ne $Authors.Count) { throw 'Publisher author attribution mismatch.' }
    for ($i = 0; $i -lt $Authors.Count; $i++) { if ($declaredAuthors[$i] -cne $Authors[$i]) { throw 'Publisher author attribution mismatch.' } }
    $current = Read-BoundedBytes $Manifest
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($current)) -ne $Proof.sha256) { throw 'Current published manifest declaration checksum mismatch.' }
    Assert-ArchivedDeclaration $Manifest $Name $Version $PackageChecksum $Proof.sha256
    if ([IO.Path]::IsPathFullyQualified($Proof.relativeFile)) { throw 'Declaration proof path must be relative.' }
    $source = [IO.Path]::GetFullPath($Proof.relativeFile, $Directory)
    Assert-InRoot $Directory $source
    Assert-Unlinked $source
    if (([IO.FileInfo]::new($source)).Length -gt $AvailableBytes) { throw 'Publisher declaration exceeds the remaining package budget.' }
    $copy = Copy-LicenseFile $Directory $source $Destination
    if ($copy.sha256 -ne $Proof.sha256) { throw 'Copied publisher declaration checksum mismatch.' }
    return [ordered]@{ path = $copy.path; sha256 = $copy.sha256; name = $Name; version = $Version; spdx = $Spdx;
        authors = @($Authors); registryPackageChecksum = $PackageChecksum; provenance = 'published-registry-manifest' }
}

function Read-Supplements([string]$Directory) {
    Assert-Unlinked $Directory
    $path = Join-Path $Directory 'manifest.json'
    Assert-Unlinked $path
    if (([IO.FileInfo]::new($path)).Length -gt 1MB) { throw 'License supplement manifest exceeds its limit.' }
    $manifest = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -Depth 12
    if ($manifest.schema -ne 1 -or $manifest.packages.Count -gt 4096) { throw 'Unsupported license supplement manifest.' }
    return $manifest.packages
}

function Copy-Supplements([string]$Directory, [object[]]$Records, [string]$Destination, [long]$AvailableBytes = 8MB,
    [AllowNull()][string]$ExpectedRepository, [AllowNull()][string]$ExpectedCommit, [AllowNull()][string]$Spdx,
    [string]$PackageName, [string]$PackageVersion, [string[]]$Authors, [string]$RegistryManifest, [string]$PackageChecksum) {
    if ($Records.Count -eq 0 -or $Records.Count -gt 16) { throw 'License supplement file count is invalid.' }
    $copied = [Collections.Generic.List[object]]::new()
    $before = $script:copiedBytes
    foreach ($record in $Records) {
        if ($record.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $record.provenance -notin @('pinned-upstream', 'standard-license-text')) { throw 'Invalid license supplement identity.' }
        $uri = [Uri]$record.sourceUrl
        if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Unsafe supplement source URL.' }
        if ($record.provenance -eq 'pinned-upstream') {
            if ($ExpectedCommit -notmatch '^[a-f0-9]{40}$' -or -not $ExpectedRepository) { throw 'Published package has no matching pinned VCS provenance.' }
            $repository = [Uri]$ExpectedRepository
            $parts = $repository.AbsolutePath.Trim('/').Split('/')
            if ($parts.Length -lt 2 -or $repository.Host -ne $(if ($uri.Host -eq 'raw.githubusercontent.com') { 'github.com' } else { $uri.Host })) { throw 'Supplement repository differs from the package publisher.' }
            $prefix = '/' + $parts[0] + '/' + ($parts[1] -replace '\.git$', '') + '/'
            if ($uri.Host -eq 'raw.githubusercontent.com') {
                if (-not $uri.AbsolutePath.StartsWith($prefix + $ExpectedCommit + '/', [StringComparison]::OrdinalIgnoreCase)) { throw 'Upstream supplement must match the package Git commit.' }
            }
            elseif ($uri.Host -eq 'gitlab.redox-os.org') {
                if ($uri.AbsolutePath -notmatch '^/redox-os/seahash/-/raw/[a-f0-9]{40}/.+$' -or -not $uri.AbsolutePath.StartsWith($prefix + '-/raw/' + $ExpectedCommit + '/', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsupported pinned GitLab supplement.' }
            }
            else { throw 'Supplement publisher host is not approved.' }
        }
        else {
            $choices = @($Spdx -split '\s+OR\s+|/' | ForEach-Object { $_.Trim() })
            if (-not $script:standardDefinitions.ContainsKey($uri.AbsoluteUri)) { throw 'Unsupported canonical standard-license source.' }
            $definition = $script:standardDefinitions[$uri.AbsoluteUri]
            if ($record.sha256 -cne $definition.sha256 -or $record.licenseChoice -cne $definition.choice -or $definition.choice -notin $choices -or
                ($definition.choice -ceq 'MIT' -and ($Spdx -cne 'MIT' -or $record.licenseChoice -cne 'MIT'))) { throw 'Canonical license definition differs from the published license choice.' }
        }
        if ([IO.Path]::IsPathFullyQualified($record.relativeFile)) { throw 'Supplement path must be relative.' }
        $source = [IO.Path]::GetFullPath($record.relativeFile, $Directory)
        Assert-InRoot $Directory $source; Assert-Unlinked $source
        $length = ([IO.FileInfo]::new($source)).Length
        if ($length -gt $script:maximumFileBytes -or $script:copiedBytes - $before + $length -gt $AvailableBytes) { throw 'License supplement exceeds its size limit.' }
        $file = Copy-LicenseFile $Directory $source $Destination
        if ($file.sha256 -ne $record.sha256) { throw 'License supplement changed during copying.' }
        $file['provenance'] = $record.provenance; $file['sourceUrl'] = $uri.AbsoluteUri
        if ($record.provenance -ceq 'standard-license-text' -and $definition.choice -ceq 'MIT') {
            $file['declarationProof'] = Copy-DeclarationProof $record.declaration $Directory $Destination $PackageName $PackageVersion $Spdx $Authors $RegistryManifest $PackageChecksum ($AvailableBytes - ($script:copiedBytes - $before))
        }
        $copied.Add($file)
    }
    return @($copied.ToArray())
}

function Invoke-CollectorSelfTest {
    function Assert-CollectorRejection([scriptblock]$Action, [string]$ExpectedMessage) {
        $failure = $null
        try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
        if (-not $failure -or $failure -notlike "*$ExpectedMessage*") { throw "Self-test: expected refusal '$ExpectedMessage', got '$failure'." }
    }
    $fixture = Join-Path $repositoryRoot ('artifacts/license-collector-test-' + [Guid]::NewGuid().ToString('N'))
    New-OwnedDirectory $repositoryRoot $fixture
    $source = Join-Path $fixture 'source'; $destination = Join-Path $fixture 'output'
    New-OwnedDirectory $fixture $source; New-OwnedDirectory $fixture $destination
    try {
        Write-NewText $fixture (Join-Path $source 'LICENSE') 'fixture-license'
        Write-NewText $fixture (Join-Path $source 'LICENSE-LUCIDE') 'fixture-asset-attribution'
        Write-NewText $fixture (Join-Path $source 'LEGAL.txt') 'fixture-declared-license'
        Write-NewText $fixture (Join-Path $source 'copyright.svg') '<svg>not a legal notice</svg>'
        $deep = Join-Path $source 'a/b/c/d/e'; New-OwnedDirectory $fixture $deep
        Write-NewText $fixture (Join-Path $deep 'LICENSE') 'outside-depth'
        $found = Find-LicenseFiles $source 'LEGAL.txt'
        if ($found.Files.Count -ne 3) { throw 'Self-test: license discovery or depth bound failed.' }
        foreach ($file in $found.Files) { [void](Copy-LicenseFile $source $file $destination) }
        if ([IO.File]::ReadAllText((Join-Path $destination 'LICENSE')) -ne 'fixture-license') { throw 'Self-test: notice bytes changed.' }
        $refused = $false
        try { [void](Copy-LicenseFile $source (Join-Path $source 'LICENSE') $destination) } catch { $refused = $true }
        if (-not $refused) { throw 'Self-test: existing output was overwritten.' }
        if ((Find-LicenseFiles $source '../foreign-license').Failures.Count -ne 1) { throw 'Self-test: declared path escaped its package.' }
        if ((Find-LicenseFiles $source 'a/b/c/d/e/LICENSE').Failures.Count -ne 1) { throw 'Self-test: declared license bypassed the depth limit.' }
        $file = [IO.File]::OpenWrite((Join-Path $source 'COPYING')); try { $file.SetLength(1MB + 1) } finally { $file.Dispose() }
        $refused = $false
        try { [void](Copy-LicenseFile $source (Join-Path $source 'COPYING') $destination) } catch { $refused = $true }
        if (-not $refused) { throw 'Self-test: oversized notice was copied.' }
        $supplementDestination = Join-Path $fixture 'supplement-copy'; New-OwnedDirectory $fixture $supplementDestination
        $record = @{ relativeFile = 'LICENSE'; sha256 = (Get-FileHash -LiteralPath (Join-Path $source 'LICENSE') -Algorithm SHA256).Hash; sourceUrl = ('https://raw.githubusercontent.com/owner/repo/' + ('a' * 40) + '/LICENSE'); provenance = 'pinned-upstream' }
        $copied = @(Copy-Supplements $source @($record) $supplementDestination 8MB 'https://github.com/owner/repo' ('a' * 40) 'MIT')
        if ($copied.Count -ne 1 -or $copied[0].provenance -ne 'pinned-upstream') { throw 'Self-test: supplement provenance was lost.' }
        $record.sha256 = '0' * 64; $refused = $false
        $badDestination = Join-Path $fixture 'bad-supplement'; New-OwnedDirectory $fixture $badDestination
        try { [void](Copy-Supplements $source @($record) $badDestination 8MB 'https://github.com/owner/repo' ('a' * 40) 'MIT') } catch { $refused = $true }
        if (-not $refused) { throw 'Self-test: altered supplement was trusted.' }
        $record.sha256 = (Get-FileHash -LiteralPath (Join-Path $source 'LICENSE') -Algorithm SHA256).Hash
        $refused = $false
        try { [void](Copy-Supplements $source @($record) $badDestination 8MB 'https://github.com/owner/repo' ('b' * 40) 'MIT') } catch { $refused = $true }
        if (-not $refused) { throw 'Self-test: supplement from a different published commit was trusted.' }
        $standardDestination = Join-Path $fixture 'standard-copy'; New-OwnedDirectory $fixture $standardDestination
        $standardRoot = Join-Path $source 'standard'; New-OwnedDirectory $fixture $standardRoot
        foreach ($definition in @('Apache-2.0.txt', 'MIT.txt')) {
            [IO.File]::Copy((Join-Path $repositoryRoot "native/licenses/standard/$definition"), (Join-Path $standardRoot $definition), $false)
        }
        $record = @{ relativeFile = 'standard/Apache-2.0.txt'; sha256 = $script:standardDefinitions['https://www.apache.org/licenses/LICENSE-2.0.txt'].sha256; provenance = 'standard-license-text'; sourceUrl = 'https://www.apache.org/licenses/LICENSE-2.0.txt'; licenseChoice = 'Apache-2.0' }
        $refused = $false
        try { [void](Copy-Supplements $source @($record) $standardDestination 8MB $null $null 'MIT') } catch { $refused = $true }
        if (-not $refused) { throw 'Self-test: Apache text cannot replace an MIT-only publisher license.' }
        $standard = @(Copy-Supplements $source @($record) $standardDestination 8MB $null $null 'MIT/Apache-2.0')
        if ($standard.Count -ne 1 -or $standard[0].provenance -ne 'standard-license-text') { throw 'Self-test: standard-license provenance was lost.' }
        $name = 'fixture'; $version = '1.0.0'; $authors = @('Published Author <public@example.invalid>')
        $registryRoot = Join-Path $fixture 'registry'
        $registryManifest = Join-Path $registryRoot "src/test-index/$name-$version/Cargo.toml"
        $declarationFile = "declarations/$name-$version/Cargo.toml"
        New-OwnedDirectory $fixture ([IO.Path]::GetDirectoryName($registryManifest))
        New-OwnedDirectory $fixture ([IO.Path]::GetDirectoryName((Join-Path $source $declarationFile)))
        $declarationText = '[package]' + "`n" + 'name = "fixture"' + "`n" + 'version = "1.0.0"' + "`n" + 'license = "MIT"' + "`n" + 'authors = ["Published Author <public@example.invalid>"]' + "`n"
        Write-NewText $fixture $registryManifest $declarationText
        [IO.File]::Copy($registryManifest, (Join-Path $source $declarationFile), $false)
        $archive = Join-Path $registryRoot "cache/test-index/$name-$version.crate"
        New-OwnedDirectory $fixture ([IO.Path]::GetDirectoryName($archive))
        $archiveStream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew)
        $gzip = [IO.Compression.GZipStream]::new($archiveStream, [IO.Compression.CompressionLevel]::SmallestSize)
        $writer = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Ustar, $true)
        $tarEntry = [System.Formats.Tar.UstarTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, "$name-$version/Cargo.toml")
        $data = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($declarationText), $false)
        try { $tarEntry.DataStream = $data; $writer.WriteEntry($tarEntry) }
        finally { $writer.Dispose(); $gzip.Dispose(); $archiveStream.Dispose(); $data.Dispose() }
        $packageChecksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $proof = @{ name = $name; version = $version; spdx = 'MIT'; authors = $authors; relativeFile = $declarationFile; sha256 = (Get-FileHash -LiteralPath $registryManifest -Algorithm SHA256).Hash.ToLowerInvariant(); registryPackageChecksum = $packageChecksum }
        $mitRecord = @{ relativeFile = 'standard/MIT.txt'; sha256 = $script:standardDefinitions['https://spdx.org/licenses/MIT.txt'].sha256; provenance = 'standard-license-text'; sourceUrl = 'https://spdx.org/licenses/MIT.txt'; licenseChoice = 'MIT'; declaration = $proof }
        $mitDestination = Join-Path $fixture 'mit-copy'; New-OwnedDirectory $fixture $mitDestination
        $mit = @(Copy-Supplements $source @($mitRecord) $mitDestination 8MB $null $null 'MIT' $name $version $authors $registryManifest $packageChecksum)
        if ($mit.Count -ne 1 -or $mit[0].declarationProof.authors[0] -cne $authors[0] -or $mit[0].declarationProof.registryPackageChecksum -cne $packageChecksum -or
            [IO.File]::ReadAllText((Join-Path $mitDestination $declarationFile)) -cne $declarationText) { throw 'Self-test: exact published MIT declaration or attribution was lost.' }
        foreach ($spdx in @('Apache-2.0', 'Zlib', 'Unknown', 'MIT OR Apache-2.0')) {
            Assert-CollectorRejection { Copy-Supplements $source @($mitRecord) $mitDestination 8MB $null $null $spdx $name $version $authors $registryManifest $packageChecksum } 'differs from the published license choice'
        }
        $invalid = $mitRecord.Clone(); $invalid.licenseChoice = 'Apache-2.0'
        Assert-CollectorRejection { Copy-Supplements $source @($invalid) $mitDestination 8MB $null $null 'MIT' $name $version $authors $registryManifest $packageChecksum } 'differs from the published license choice'
        $invalid = $mitRecord.Clone(); $invalid.sha256 = '0' * 64
        Assert-CollectorRejection { Copy-Supplements $source @($invalid) $mitDestination 8MB $null $null 'MIT' $name $version $authors $registryManifest $packageChecksum } 'differs from the published license choice'
        foreach ($field in @('name', 'version', 'spdx', 'registryPackageChecksum')) {
            $invalid = $proof.Clone(); $invalid[$field] = if ($field -eq 'registryPackageChecksum') { '0' * 64 } else { 'wrong' }
            Assert-CollectorRejection { Copy-DeclarationProof $invalid $source $mitDestination $name $version 'MIT' $authors $registryManifest $packageChecksum 8MB } 'package, version, license or checksum mismatch'
        }
        $invalid = $proof.Clone(); $invalid.authors = @('Unrelated Author')
        Assert-CollectorRejection { Copy-DeclarationProof $invalid $source $mitDestination $name $version 'MIT' $authors $registryManifest $packageChecksum 8MB } 'author attribution mismatch'
        $invalid = $proof.Clone(); $invalid.sha256 = '0' * 64
        Assert-CollectorRejection { Copy-DeclarationProof $invalid $source $mitDestination $name $version 'MIT' $authors $registryManifest $packageChecksum 8MB } 'declaration checksum mismatch'
        $invalid = $proof.Clone(); $invalid.registryPackageChecksum = '0' * 64
        Assert-CollectorRejection { Copy-DeclarationProof $invalid $source $mitDestination $name $version 'MIT' $authors $registryManifest ('0' * 64) 8MB } 'archive differs from Cargo.lock'
        Assert-CollectorRejection { Assert-ArchivedDeclaration $registryManifest $name $version $packageChecksum ('0' * 64) } 'differs from the checksummed published archive'
        Write-Output 'License collector fixture checks PASS. Cargo was not mocked or invoked.'
    }
    finally {
        Assert-InRoot (Join-Path $repositoryRoot 'artifacts') $fixture
        Assert-Unlinked $fixture
        foreach ($entry in Get-ChildItem -LiteralPath $fixture -Recurse -Force) { Assert-Unlinked $entry.FullName }
        [IO.Directory]::Delete($fixture, $true)
    }
}

if ($SelfTest) { Invoke-CollectorSelfTest; return }

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repositoryRoot)
Assert-Unlinked $outputRoot
$licenseRoot = Join-Path $outputRoot 'licenses'
Assert-Unlinked $licenseRoot
if ([IO.File]::Exists($licenseRoot) -or [IO.Directory]::Exists($licenseRoot)) { throw 'The licenses destination already exists; no files were overwritten.' }
New-OwnedDirectory $outputRoot $licenseRoot
$entries = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
try {
    $supplements = @{}
    if ($SupplementDirectory) { $SupplementDirectory = [IO.Path]::GetFullPath($SupplementDirectory, $repositoryRoot); $supplements = Read-Supplements $SupplementDirectory }
    $metadata = Read-CargoMetadata
    $packages = @{}; $nodes = @{}
    foreach ($package in $metadata.packages) { $packages[$package.id] = $package }
    foreach ($node in $metadata.resolve.nodes) { $nodes[$node.id] = $node }
    $members = [Collections.Generic.HashSet[string]]::new([string[]]$metadata.workspace_members, [StringComparer]::Ordinal)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($member in $metadata.workspace_members) { $queue.Enqueue($member) }
    while ($queue.Count) {
        Assert-TimeBudget
        $id = $queue.Dequeue()
        if (-not $seen.Add($id)) { continue }
        if ($seen.Count -gt 4096 -or -not $nodes.ContainsKey($id) -or -not $packages.ContainsKey($id)) { throw 'Resolved dependency graph is incomplete or exceeds its limit.' }
        foreach ($dependency in $nodes[$id].dependencies) { $queue.Enqueue($dependency) }
        if ($members.Contains($id)) { continue }
        $package = $packages[$id]
        $name = [string]$package.name; $version = [string]$package.version
        if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.+-]{0,127}$' -or $version -notmatch '^[A-Za-z0-9][A-Za-z0-9_.+-]{0,127}$') { throw 'Invalid package name or version.' }
        $entry = [ordered]@{ name = $name; version = $version; spdx = $package.license; repository = (Safe-Repository $package.repository); status = 'missing-source-license'; directory = "$name-$version"; files = @(); reasons = @(); warnings = @(); supplementFiles = @(); registryLicenseTextMissing = $false }
        if ($entry.directory.Length -gt 120) { throw 'Package directory name exceeds its limit.' }
        try {
            if (-not $package.source -or $package.source -notmatch '^(registry|sparse)\+') { throw 'unsupported-non-registry-source' }
            if (-not $package.license -and -not $package.license_file) { throw 'manifest-does-not-declare-license' }
            $sourceRoot = [IO.Path]::GetDirectoryName([string]$package.manifest_path)
            $found = Find-LicenseFiles $sourceRoot $package.license_file
            $entry.reasons = $found.Failures; $entry.warnings = $found.Warnings
            $destination = Join-Path $licenseRoot $entry.directory
            New-OwnedDirectory $licenseRoot $destination
            $before = $script:copiedBytes
            $copied = [Collections.Generic.List[object]]::new()
            foreach ($file in $found.Files) {
                if ($script:copiedBytes - $before + ([IO.FileInfo]::new($file).Length) -gt $script:maximumPackageBytes) { throw 'package-notices-exceed-size-limit' }
                $copied.Add((Copy-LicenseFile $sourceRoot $file $destination))
            }
            $entry.files = @($copied.ToArray())
            $mainLicense = @($found.Files | Where-Object { $base = [IO.Path]::GetFileName($_); $base -match '^(LICENSE|LICENCE|COPYING|UNLICENSE|OFL)(?:$|[._-])' -and ($base -notmatch 'LUCIDE' -or $name -match 'lucide') })
            $entry.registryLicenseTextMissing = $mainLicense.Count -eq 0
            if ($entry.registryLicenseTextMissing -and $supplements.ContainsKey("$name@$version")) {
                $supplementDestination = Join-Path $destination 'verified-supplements'
                New-OwnedDirectory $licenseRoot $supplementDestination
                $commit = $null; $vcs = Join-Path $sourceRoot '.cargo_vcs_info.json'
                Assert-Unlinked $vcs
                if ([IO.File]::Exists($vcs)) {
                    if (([IO.FileInfo]::new($vcs)).Length -gt 4096) { throw 'Package VCS metadata exceeds its limit.' }
                    $commit = ([IO.File]::ReadAllText($vcs) | ConvertFrom-Json -Depth 8).git.sha1
                }
                $publisher = if ($package.repository) { $package.repository } else { $package.homepage }
                $checksum = if ($package.license -ceq 'MIT') { Get-LockedPackageChecksum $name $version $package.source } else { $null }
                $entry.supplementFiles = @(Copy-Supplements $SupplementDirectory @($supplements["$name@$version"]) $supplementDestination ($script:maximumPackageBytes - ($script:copiedBytes - $before)) $publisher $commit $package.license $name $version @($package.authors) $package.manifest_path $checksum)
                $entry.warnings += 'publisher-omitted-package-license-text; supplement-provenance-recorded-separately'
            }
            elseif ($entry.registryLicenseTextMissing) { $entry.reasons += 'publisher-included-no-package-license-text; SPDX-is-manifest-metadata-only' }
            if ($entry.reasons.Count -eq 0) { $entry.status = $(if ($entry.registryLicenseTextMissing) { 'verified-supplement-collected' } else { 'source-license-files-collected' }) }
        }
        catch { $entry.reasons += 'notice-collection-failed'; $entry.warnings += $_.Exception.GetType().Name }
        if ($entry.status -notin @('source-license-files-collected', 'verified-supplement-collected')) { $errors.Add("$name $version") }
        $entries.Add($entry)
    }
}
catch { $errors.Add('metadata-or-graph-collection-failed'); Write-Warning $_.Exception.Message }

$index = [ordered]@{ schema = 1; generatedUtc = [DateTimeOffset]::UtcNow.ToString('o'); target = 'x86_64-pc-windows-msvc'; status = $(if ($errors.Count) { 'incomplete' } else { 'source-notices-collected' });
    method = 'cargo metadata --locked --offline --format-version 1 --filter-platform x86_64-pc-windows-msvc'; includesBuildAndDevDependencies = $true; workspaceLicenseHandledSeparately = $true;
    copiedFileCount = $script:copiedFiles; copiedBytes = $script:copiedBytes; errors = @($errors.ToArray()); packages = @($entries.ToArray() | Sort-Object { $_['name'] }, { $_['version'] }) }
Write-NewText $licenseRoot (Join-Path $licenseRoot 'index.json') ($index | ConvertTo-Json -Depth 12)
$notice = [Text.StringBuilder]::new()
[void]$notice.AppendLine('Net Lights: уведомления о сторонних компонентах')
[void]$notice.AppendLine('Собраны файлы лицензий, авторских прав и уведомлений из локального Cargo registry и явно указанных дополнительных источников. Их происхождение и контрольные суммы записаны в index.json.')
[void]$notice.AppendLine('Сам сборщик работает офлайн и не запускает сборку. Дополнительные тексты лицензий подготовлены отдельно по указанным адресам источников.')
[void]$notice.AppendLine('Перечень содержит разрешённый граф Windows, включая зависимости сборки и тестов. Лицензия самого Net Lights поставляется отдельно в LICENSE.')
[void]$notice.AppendLine('SPDX и адрес репозитория получены из манифестов издателей. Наличие файлов не является заключением о выполнении всех юридических требований.')
if ($errors.Count) { [void]$notice.AppendLine('Сбор НЕПОЛНЫЙ: отдельные издатели не включили текст лицензии или сбор завершился с ошибкой. Причины указаны в index.json. Такой результат нельзя объявлять полным комплектом уведомлений.') }
foreach ($entry in $index.packages) {
    [void]$notice.AppendLine()
    [void]$notice.AppendLine("$($entry.name) $($entry.version)")
    [void]$notice.AppendLine("SPDX: $($entry.spdx)")
    [void]$notice.AppendLine("Repository: $($entry.repository)")
    [void]$notice.AppendLine("Status: $($entry.status)")
    foreach ($file in $entry.files) { [void]$notice.AppendLine("  $($entry.directory)/$($file.path)") }
    foreach ($file in $entry.supplementFiles) {
        [void]$notice.AppendLine("  $($entry.directory)/verified-supplements/$($file.path) [$($file.provenance)] $($file.sourceUrl)")
        if ($file.Contains('declarationProof')) {
            [void]$notice.AppendLine('  Издатель указал MIT в опубликованном Cargo.toml, но не вложил LICENSE. Приложен канонический стандартный текст; поля copyright не заполнялись от имени издателя.')
            [void]$notice.AppendLine("  Декларация издателя: $($entry.directory)/verified-supplements/$($file.declarationProof.path)")
            [void]$notice.AppendLine("  Авторы, указанные издателем (не утверждение о правообладателях): $($file.declarationProof.authors -join '; ')")
        }
    }
}
Write-NewText $licenseRoot (Join-Path $licenseRoot 'THIRD-PARTY-NOTICES.txt') $notice.ToString()
Write-Output "Collected $($entries.Count) dependency records, $script:copiedFiles source files, $script:copiedBytes bytes. Index: $licenseRoot/index.json"
if ($errors.Count) { throw "Third-party notice collection is incomplete for $($errors.Count) entries: $($errors -join ', ')" }
