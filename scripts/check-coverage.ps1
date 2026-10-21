param([string]$ResultsDirectory = 'TestResults/current')

$ErrorActionPreference = 'Stop'
$unitDirectory = Join-Path $ResultsDirectory 'unit'
$integrationDirectory = Join-Path $ResultsDirectory 'integration'
if (-not (Test-Path -LiteralPath $unitDirectory -PathType Container)) { throw "Unit coverage directory not found: $unitDirectory" }
if (-not (Test-Path -LiteralPath $integrationDirectory -PathType Container)) { throw "Integration coverage directory not found: $integrationDirectory" }
$unitFiles = @(Get-ChildItem -LiteralPath $unitDirectory -Recurse -Filter coverage.cobertura.xml)
$integrationFiles = @(Get-ChildItem -LiteralPath $integrationDirectory -Recurse -Filter coverage.cobertura.xml)
if ($unitFiles.Count -eq 0) { throw 'Unit coverage report is missing' }
if ($integrationFiles.Count -eq 0) { throw 'Integration coverage report is missing' }
$files = @($unitFiles) + @($integrationFiles)

# Unit and integration runs instrument the same source separately. Merge line hits
# by source path and line number so code covered by either run counts once.
$linesByAssembly = @{}
foreach ($file in $files) {
    [xml]$report = Get-Content -LiteralPath $file.FullName
    foreach ($package in $report.coverage.packages.package) {
        $assembly = [string]$package.name
        if ($assembly -eq 'NetLights.TestSupport') { continue }
        if (-not $linesByAssembly.ContainsKey($assembly)) { $linesByAssembly[$assembly] = @{} }
        foreach ($class in $package.classes.class) {
            $source = ([string]$class.filename).Replace('/', '\')
            if ($source -match '^(NetLights\.(?:App|Core|Networking|Updates|UpdateAgent)\\)') { $source = 'src\' + $source }
            foreach ($line in $class.SelectNodes('lines/line')) {
                $key = "$source`:$($line.number)"
                $hits = [int]$line.hits
                if (-not $linesByAssembly[$assembly].ContainsKey($key) -or $hits -gt $linesByAssembly[$assembly][$key]) {
                    $linesByAssembly[$assembly][$key] = $hits
                }
            }
        }
    }
}

$floors = @{
    'NetLights.Core' = 0.90
    'NetLights.Networking' = 0.60
    'NetLights.Updates' = 0.75
    # The shipped application is named NetLights.exe/NetLights.dll.
    'NetLights' = 0.60
    'NetLights.UpdateAgent' = 0.80
}
$totalLines = 0
$totalCovered = 0
foreach ($name in $floors.Keys) {
    if (-not $linesByAssembly.ContainsKey($name) -or $linesByAssembly[$name].Count -eq 0) {
        throw "Coverage for $name is missing"
    }
    $lines = @($linesByAssembly[$name].Values)
    $covered = @($lines | Where-Object { $_ -gt 0 }).Count
    $rate = [double]$covered / $lines.Count
    $displayName = if ($name -eq 'NetLights') { 'NetLights.App (NetLights.dll)' } else { $name }
    Write-Host ("{0} line coverage: {1:P2} ({2}/{3})" -f $displayName, $rate, $covered, $lines.Count)
    if ($rate -lt $floors[$name]) { throw "$displayName line coverage $rate is below $($floors[$name])" }
    $totalLines += $lines.Count
    $totalCovered += $covered
}

$overall = [double]$totalCovered / $totalLines
Write-Host ("Critical assemblies combined line coverage: {0:P2}" -f $overall)
if ($overall -lt 0.80) { throw "Critical assemblies combined line coverage $overall is below 0.80" }
