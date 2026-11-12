param(
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
. (Join-Path $PSScriptRoot 'local-publish-transaction.ps1')

$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$staging = Join-Path $artifacts ('publish-next-' + [Guid]::NewGuid().ToString('N'))
$localExe = Join-Path $publish 'NetLights.exe'
$null = Assert-LocalPublishDirectory $artifacts $staging '^publish-next-[a-f0-9]{32}$'
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    & (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $staging
    [xml]$props=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
    $version=[string]$props.Project.PropertyGroup.Version
    $candidateHash=(Get-FileHash -LiteralPath (Join-Path $staging 'NetLights.exe')).Hash
    $null=Assert-LocalPublishDirectory $artifacts $staging '^publish-next-[a-f0-9]{32}$'
    foreach($name in @('NetLights.exe','NetLights.UpdateAgent.exe')) {
        $prepared=Get-Item -LiteralPath (Join-Path $staging $name) -Force
        if($prepared.PSIsContainer -or $prepared.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Prepared local image must be an unlinked file.'}
        if((([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $staging $name)).ProductVersion -split '\+')[0]) -cne $version) {throw 'Prepared local version differs from the project version.'}
    }
    $previousHash=if(Test-Path -LiteralPath $localExe) {(Get-FileHash -LiteralPath $localExe).Hash}else{$null}
    $previousVersion=if($previousHash) {([Diagnostics.FileVersionInfo]::GetVersionInfo($localExe).ProductVersion -split '\+')[0]}else{$null}

    $localProcesses = @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [string]::Equals($_.Path, $localExe, [System.StringComparison]::OrdinalIgnoreCase) })
    if ($localProcesses.Count -gt 0) {
        foreach ($process in $localProcesses) {
            Stop-OwnedLocalProcess @{Process=$process;Executable=$localExe;ProfileDirectory=$null}
        }
    }

    $candidateOwner=@{}; $previousOwner=@{}
    $receipt=Complete-LocalPublishTransaction $artifacts $staging $publish -NoStart:$NoStart -ConfirmCandidate {
        param($path)
        Start-VerifiedLocalProcess (Join-Path $path 'NetLights.exe') $candidateHash $version $candidateOwner -Show | Out-Null
    } -StopCandidate { Stop-OwnedLocalProcess $candidateOwner } -RestorePrevious {
        param($path)
        Start-VerifiedLocalProcess (Join-Path $path 'NetLights.exe') $previousHash $previousVersion $previousOwner -Show | Out-Null
    }
    if($NoStart) {Write-Host "NOT RUN: запуск и health не запрашивались. Предыдущая программа сохранена: $($receipt.backupPath)"}
    else {Write-Host "PASS: локальная версия $version подтвердила работу монитора и устойчивость процесса."}
}
finally {
    if(Test-Path -LiteralPath $staging) {Write-Warning "Local staging retained at $staging. Published data wasn't replaced."}
}
