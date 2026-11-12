param([string]$InputPath, [switch]$SelfTest)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ResourceVerdict([object[]]$Samples) {
    if ($Samples.Count -lt 60) { return @{status='INCONCLUSIVE'; reason='At least 60 samples are required.'} }
    $start = [DateTimeOffset]::Parse($Samples[0].utc)
    $end = [DateTimeOffset]::Parse($Samples[-1].utc)
    if (($end-$start).TotalMinutes -lt 59) { return @{status='INCONCLUSIVE'; reason='An hour-long run is required.'} }
    $previous = $start
    $identity = [string]$Samples[0].pid
    foreach ($sample in $Samples) {
        $timestamp = [DateTimeOffset]::Parse($sample.utc)
        if ([string]$sample.pid -ne $identity) { throw 'Process changed during the resource run.' }
        if ($timestamp -lt $previous -or ($timestamp-$previous).TotalMinutes -gt 2) { throw 'Resource evidence has a clock reversal or sampling gap.' }
        $previous = $timestamp
    }
    $warm = @($Samples | Where-Object { ([DateTimeOffset]::Parse($_.utc)-$start).TotalMinutes -ge 10 })
    if ($warm.Count -lt 40) { return @{status='INCONCLUSIVE'; reason='Not enough samples after warmup.'} }
    $metrics = @{}
    $failed = $false
    foreach ($entry in @(@('privateBytes',8MB),@('handles',15),@('gdi',3),@('userObjects',3))) {
        $name = [string]$entry[0]
        $tolerance = [double]$entry[1]
        $values = @($warm | ForEach-Object { [double]::Parse([string]$_.$name,[Globalization.CultureInfo]::InvariantCulture) })
        if (@($values | Where-Object { -not [double]::IsFinite($_) -or $_ -lt 0 }).Count) { throw "Invalid $name sample" }
        $times = @($warm | ForEach-Object { ([DateTimeOffset]::Parse($_.utc)-$start).TotalMinutes })
        $meanX = ($times | Measure-Object -Average).Average
        $meanY = ($values | Measure-Object -Average).Average
        $xx = 0.0; $xy = 0.0
        for ($i=0;$i -lt $values.Count;$i++) { $xx += [Math]::Pow($times[$i]-$meanX,2); $xy += ($times[$i]-$meanX)*($values[$i]-$meanY) }
        if ($xx -le 0) { throw 'Sample timestamps must advance.' }
        $slope = $xy/$xx
        $sse = 0.0
        for ($i=0;$i -lt $values.Count;$i++) { $sse += [Math]::Pow($values[$i]-($meanY+$slope*($times[$i]-$meanX)),2) }
        $se = [Math]::Sqrt($sse/($values.Count-2)/$xx)
        $quarter = [Math]::Max(1,[int][Math]::Floor($values.Count/4))
        $first = ($values[0..($quarter-1)] | Measure-Object -Average).Average
        $last = ($values[($values.Count-$quarter)..($values.Count-1)] | Measure-Object -Average).Average
        $growth = $last-$first
        $growing = $slope-1.96*$se -gt 0 -and $growth -gt $tolerance
        $failed = $failed -or $growing
        $metrics[$name] = @{firstMean=$first; lastMean=$last; delta=$growth; slopePerMinute=$slope; slopeLower95=$slope-1.96*$se; tolerance=$tolerance; growing=$growing}
    }
    return @{status=$(if ($failed) {'FAIL'} else {'PASS'}); minutes=($end-$start).TotalMinutes; afterWarmup=$warm.Count; metrics=$metrics}
}

if ($SelfTest) {
    $origin = [DateTimeOffset]'2026-01-01T00:00:00Z'
    $stable = @(0..60 | ForEach-Object { @{utc=$origin.AddMinutes($_).ToString('o'); pid=1; privateBytes=100MB; handles=100; gdi=30; userObjects=40} })
    if ((Get-ResourceVerdict $stable).status -ne 'PASS') { throw 'Stable negative control failed.' }
    $leaking = @(0..60 | ForEach-Object { @{utc=$origin.AddMinutes($_).ToString('o'); pid=1; privateBytes=100MB+$_*1MB; handles=100+$_; gdi=30+$_; userObjects=40+$_} })
    if ((Get-ResourceVerdict $leaking).status -ne 'FAIL') { throw 'Resource-growth positive control was missed.' }
    if ((Get-ResourceVerdict $stable[0..20]).status -ne 'INCONCLUSIVE') { throw 'Short smoke was incorrectly accepted.' }
    foreach ($invalidCase in @('identity','gap','nonfinite')) {
        $invalid = @($stable | ForEach-Object { $_.Clone() })
        switch ($invalidCase) {
            'identity' { $invalid[-1].pid = 2 }
            'gap' { $invalid[-1].utc = $origin.AddMinutes(65).ToString('o') }
            'nonfinite' { $invalid[-1].privateBytes = 'NaN' }
        }
        $rejected = $false
        try { $null = Get-ResourceVerdict $invalid } catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid evidence was accepted: $invalidCase" }
    }
    Write-Output 'Resource trend gate controls PASS'
    return
}
if (-not $InputPath) { throw 'InputPath is required.' }
$verdict = Get-ResourceVerdict @(Import-Csv -LiteralPath $InputPath)
$verdict | ConvertTo-Json -Depth 6 | Tee-Object -FilePath ($InputPath + '.result.json')
if ($verdict.status -eq 'FAIL') { exit 1 }
if ($verdict.status -eq 'INCONCLUSIVE') { exit 2 }
