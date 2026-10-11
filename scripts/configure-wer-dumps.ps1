param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Enable')][switch]$Enable,
    [Parameter(Mandatory = $true, ParameterSetName = 'Disable')][switch]$Disable
)

$ErrorActionPreference = 'Stop'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\NetLights.exe'
if ($Enable) {
    if (Test-Path -LiteralPath $key) { throw 'WER settings for NetLights.exe already exist; leaving them untouched.' }
    $folder = Join-Path $env:LOCALAPPDATA 'NetLights\diagnostics\dumps'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name DumpFolder -Value $folder -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name DumpType -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name DumpCount -Value 2 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name NetLightsConfigured -Value 1 -PropertyType DWord -Force | Out-Null
    Write-Output "WER mini-dumps enabled for NetLights.exe: $folder"
}
else {
    if (-not (Test-Path -LiteralPath $key)) { Write-Output 'WER mini-dumps are not configured.'; exit 0 }
    $owned = (Get-ItemProperty -LiteralPath $key -Name NetLightsConfigured -ErrorAction SilentlyContinue).NetLightsConfigured
    if ($owned -ne 1) { throw 'WER settings were not created by this script; leaving them untouched.' }
    Remove-Item -LiteralPath $key -Recurse -Force
    Write-Output 'WER mini-dump configuration removed. Existing dump files were preserved.'
}
