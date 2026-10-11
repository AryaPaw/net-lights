param(
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) ('net-lights-diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip'))
)

$ErrorActionPreference = 'Stop'
$deadline = (Get-Date).AddSeconds(30)
$source = Join-Path $env:LOCALAPPDATA 'NetLights'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$temporary = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('NetLights-diagnostics-' + [guid]::NewGuid().ToString('N'))))
if (-not $temporary.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary directory.' }
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$completed = $false
try {
    $inputs = @(
        @{ Path = (Join-Path $source 'diagnostics'); Filter = 'runs-*.jsonl'; Limit = 7 },
        @{ Path = (Join-Path $source 'updates'); Filter = 'phases.jsonl'; Limit = 1 },
        @{ Path = (Join-Path $source 'updates'); Filter = 'result.json'; Limit = 1 }
    )
    foreach ($group in $inputs) {
        if ((Get-Date) -gt $deadline) { throw 'Collection timed out.' }
        if (-not (Test-Path -LiteralPath $group.Path)) { continue }
        $files = @(Get-ChildItem -LiteralPath $group.Path -Filter $group.Filter -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First $group.Limit)
        foreach ($file in $files) {
            if ((Get-Date) -gt $deadline) { throw 'Collection timed out.' }
            $target = Join-Path $temporary ($file.Directory.Name + '-' + $file.Name)
            Get-Content -LiteralPath $file.FullName -Tail 2000 -ErrorAction Stop | Set-Content -LiteralPath $target -Encoding utf8
        }
    }
    $destination = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($temporary, $destination)
    if (-not (Test-Path -LiteralPath $destination) -or (Get-Item -LiteralPath $destination).Length -eq 0) { throw 'Archive is empty.' }
    $completed = $true
    Write-Output $destination
}
finally {
    if ($completed -and (Test-Path -LiteralPath $temporary)) {
        $resolved = [IO.Path]::GetFullPath($temporary)
        if ($resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
