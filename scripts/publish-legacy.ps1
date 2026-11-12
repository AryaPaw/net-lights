param([string]$OutputDirectory = 'artifacts/publish')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $projects = @('src/NetLights.App/NetLights.App.csproj', 'src/NetLights.UpdateAgent/NetLights.UpdateAgent.csproj')
    foreach ($project in $projects) {
        & dotnet restore $project -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true --locked-mode -maxcpucount:8
        if ($LASTEXITCODE -ne 0) { throw "Locked publish restore failed for $project ($LASTEXITCODE)" }
    }
    foreach ($project in $projects) {
        & dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $output -maxcpucount:8
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project ($LASTEXITCODE)" }
    }
    foreach ($file in @('NetLights.exe', 'NetLights.UpdateAgent.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) { throw "Missing published executable: $file" }
    }
}
finally { Pop-Location }
