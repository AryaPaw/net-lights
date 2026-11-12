param([string]$OutputDirectory = 'artifacts/publish', [ValidateSet('Debug','Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'Publish needs an empty staging directory; use run-local.ps1 for transactional replacement.' }
    [string[]]$configurationArguments = @(if ($Configuration -eq 'Release') { '--release' })
    & cargo build --workspace --locked --target-dir artifacts/rust-target -j 4 @configurationArguments
    if ($LASTEXITCODE -ne 0) { throw "Native publish build failed ($LASTEXITCODE)" }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $cargoProfileDirectory = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
    Copy-Item -LiteralPath "artifacts/rust-target/x86_64-pc-windows-msvc/$cargoProfileDirectory/netlights.exe" -Destination (Join-Path $output 'NetLights.exe')
    Copy-Item -LiteralPath "artifacts/rust-target/x86_64-pc-windows-msvc/$cargoProfileDirectory/netlights-update-agent.exe" -Destination (Join-Path $output 'NetLights.UpdateAgent.exe')
    Copy-Item -LiteralPath LICENSE -Destination (Join-Path $output 'LICENSE')
    $licenseCollection = Join-Path $root ('artifacts/license-package-' + [Guid]::NewGuid().ToString('N'))
    & (Join-Path $PSScriptRoot 'collect-native-licenses.ps1') -OutputDirectory $licenseCollection -SupplementDirectory (Join-Path $root 'native/licenses')
    $licenseSource = Join-Path $licenseCollection 'licenses'
    $licenseArchive = Join-Path $output 'third-party-licenses.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($licenseSource, $licenseArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    # Verify a real isolated restore before deleting task-owned temporary sources.
    $licenseRestore = Join-Path $licenseCollection 'restored'
    [IO.Compression.ZipFile]::ExtractToDirectory($licenseArchive, $licenseRestore)
    $sourceFiles = @(Get-ChildItem -LiteralPath $licenseSource -Recurse -File)
    $restoredFiles = @(Get-ChildItem -LiteralPath $licenseRestore -Recurse -File)
    if ($sourceFiles.Count -ne $restoredFiles.Count) { throw 'License archive restore count mismatch.' }
    foreach ($sourceFile in $sourceFiles) {
        $relativeName = [IO.Path]::GetRelativePath($licenseSource,$sourceFile.FullName)
        $restoredFile = Join-Path $licenseRestore $relativeName
        if ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $restoredFile -Algorithm SHA256).Hash) { throw 'License archive restore checksum mismatch.' }
    }
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    $resolvedCollection = [IO.Path]::GetFullPath($licenseCollection)
    if (-not $resolvedCollection.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedCollection) -cnotmatch '^license-package-[a-f0-9]{32}$') { throw 'Unexpected license temporary path; retained.' }
    Remove-Item -LiteralPath $resolvedCollection -Recurse -Force
    foreach ($file in @('NetLights.exe', 'NetLights.UpdateAgent.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) { throw "Missing published executable: $file" }
    }
}
finally { Pop-Location }
