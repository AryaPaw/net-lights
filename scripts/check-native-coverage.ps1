param(
    [string]$CoverageFile,
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputPath,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# LLVM exports executable source lines, including inline Rust unit tests. Remove
# only positively test-gated items, never an entire file containing runtime code.
if (-not ('NetLightsCoverageRust' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
public static class NetLightsCoverageRust {
    static void Blank(char[] a, int start, int end) {
        for (int i=start; i<end; i++) if (a[i]!='\r' && a[i]!='\n') a[i]=' ';
    }
    public static string Code(string s) {
        char[] a=s.ToCharArray();
        for(int i=0;i<s.Length;) {
            int start=i;
            if(i+1<s.Length && s[i]=='/' && s[i+1]=='/') {
                while(i<s.Length && s[i]!='\n') i++; Blank(a,start,i); continue;
            }
            if(i+1<s.Length && s[i]=='/' && s[i+1]=='*') {
                int depth=1; i+=2;
                while(i<s.Length && depth>0) {
                    if(i+1<s.Length && s[i]=='/' && s[i+1]=='*') { depth++; i+=2; }
                    else if(i+1<s.Length && s[i]=='*' && s[i+1]=='/') { depth--; i+=2; }
                    else i++;
                }
                if(depth!=0) throw new Exception("Unterminated Rust comment");
                Blank(a,start,i); continue;
            }
            int raw=(s[i]=='r') ? i : ((s[i]=='b'||s[i]=='c') && i+1<s.Length && s[i+1]=='r' ? i+1 : -1);
            if(raw>=0 && (i==0 || (!Char.IsLetterOrDigit(s[i-1]) && s[i-1]!='_'))) {
                int q=raw+1; while(q<s.Length && s[q]=='#') q++;
                if(q<s.Length && s[q]=='"') {
                    string end="\""+new string('#',q-raw-1);
                    int close=s.IndexOf(end,q+1,StringComparison.Ordinal);
                    if(close<0) throw new Exception("Unterminated raw Rust string");
                    i=close+end.Length; Blank(a,start,i); continue;
                }
            }
            if(s[i]=='"') {
                i++; bool closed=false;
                while(i<s.Length) {
                    if(s[i]=='\\') i=Math.Min(s.Length,i+2);
                    else if(s[i++]=='"') { closed=true; break; }
                }
                if(!closed) throw new Exception("Unterminated Rust string");
                Blank(a,start,i); continue;
            }
            if(s[i]=='\'') {
                int q=i+1;
                if(q<s.Length && s[q]=='\\') {
                    q+=2;
                    if(q<=s.Length && s[q-1]=='u' && q<s.Length && s[q]=='{') {
                        q=s.IndexOf('}',q+1); if(q<0) throw new Exception("Invalid Rust char"); q++;
                    } else if(q<=s.Length && s[q-1]=='x') q+=2;
                } else if(q<s.Length) q+=Char.IsHighSurrogate(s[q]) ? 2 : 1;
                if(q<s.Length && s[q]=='\'') { i=q+1; Blank(a,start,i); continue; }
            }
            i++;
        }
        return new string(a);
    }
    public static HashSet<int> TestLines(string source) {
        string code=Code(source); var lines=new HashSet<int>();
        var pattern=new Regex(@"#\s*\[\s*cfg\s*\((?<cfg>[^\]]*)\)\s*\]");
        foreach(Match m in pattern.Matches(code)) {
            string cfg=Regex.Replace(m.Groups["cfg"].Value,@"\s+","");
            if(cfg!="test" && cfg!="all(test,windows)" && cfg!="all(windows,test)") {
                if(Regex.IsMatch(cfg,@"\btest\b") && cfg!="not(test)")
                    throw new Exception("Review unsupported test cfg: "+cfg);
                continue;
            }
            int i=m.Index+m.Length, round=0,square=0,angle=0,curly=0; bool body=false,done=false;
            for(;i<code.Length;i++) {
                char c=code[i];
                if(c=='(') round++; else if(c==')') round--;
                else if(c=='[') square++; else if(c==']') square--;
                else if(c=='<' && !body) angle++;
                else if(c=='>' && !body && angle>0) angle--;
                else if(c=='{') { curly++; body=true; }
                else if(c=='}') {
                    curly--;
                    if(curly==0 && round==0 && square==0) {
                        int next=i+1; while(next<code.Length && Char.IsWhiteSpace(code[next])) next++;
                        // A struct-pattern match arm has its own braces before
                        // =>; its test-only executable body belongs to the arm.
                        if(next+1<code.Length && code[next]=='=' && code[next+1]=='>') {i=next+1; continue;}
                        i++; done=true; break;
                    }
                }
                // A semicolon cannot occur inside generic arguments except in
                // a nested bracket/block. '<' may also be a comparison, so it
                // mustn't make the parser consume the following runtime item.
                if(curly==0 && round==0 && square==0 && (c==';'||(c==','&&angle==0))) { i++; done=true; break; }
                if(curly==0 && round==0 && square==0 && c==',' && angle>0)
                    throw new Exception("Ambiguous cfg(test) generic/comparison before comma; review required");
                if(round<0||square<0||curly<0) throw new Exception("Unbalanced cfg(test) item");
            }
            if(!done) throw new Exception("Unterminated cfg(test) item");
            int lineStart=code.LastIndexOf('\n',m.Index); lineStart=lineStart<0 ? 0 : lineStart+1;
            int lineEnd=code.IndexOf('\n',i); if(lineEnd<0) lineEnd=code.Length;
            if(code.Substring(lineStart,m.Index-lineStart).Trim().Length!=0 ||
               code.Substring(i,lineEnd-i).Trim().Trim(';',',').Trim().Length!=0)
                throw new Exception("Test and runtime item share a line at offset "+m.Index+"; line coverage cannot separate them");
            int line=1; for(int p=0;p<m.Index;p++) if(code[p]=='\n') line++;
            lines.Add(line);
            for(int p=m.Index;p<i;p++) { lines.Add(line); if(code[p]=='\n') line++; }
        }
        return lines;
    }
}
'@
}

$floors = [ordered]@{ Domain = .90; Networking = .60; Updates = .75; App = .60; Agent = .80 }
function Get-Scope([string]$Relative) {
    $name=$Relative.Replace('\','/')
    switch -Regex ($name) {
        '^native/core/src/(monitor|history|model|countries|locale|lib)\.rs$' { return 'Domain' }
        '^native/core/src/(transport|geo|geoip|geoip_store|diagnostics|traffic)\.rs$' { return 'Networking' }
        '^native/core/src/update\.rs$' { return 'Updates' }
        '^native/core/src/(platform|persistence)\.rs$' { return 'App' }
        '^native/app/src/(main|view|geometry|location_rows|charts|dashboard|fluent|backend|tray|window_os|text)\.rs$' { return 'App' }
        '^native/app/src/(view/(state|settings|locations|diagnostics|about|navigation)|charts/timeline|backend/(journal|export))\.rs$' { return 'App' }
        '^native/update-agent/src/(main|win|snapshot|metadata|marker)\.rs$' { return 'Agent' }
        '^native/update-agent/src/process_tests\.rs$' { return 'Fixture' }
        default { throw "Unclassified runtime source: $name" }
    }
}
function Measure-Coverage([string]$XmlPath,[string]$Sources) {
    $sourceBase=[IO.Path]::GetFullPath($Sources)
    $inventory=@{}
    foreach($directory in @('native/core/src','native/app/src','native/update-agent/src')) {
        $path=Join-Path $sourceBase $directory
        if(-not(Test-Path -LiteralPath $path -PathType Container)) { throw "Runtime source directory missing: $directory" }
        foreach($file in Get-ChildItem -LiteralPath $path -Recurse -Filter '*.rs' -File) {
            if($file.Length -gt 8MB) { throw 'Source file exceeds coverage parsing limit.' }
            $relative=[IO.Path]::GetRelativePath($sourceBase,$file.FullName).Replace('\','/')
            $scope=Get-Scope $relative
            if($scope -eq 'Fixture') { continue }
            $text=[IO.File]::ReadAllText($file.FullName)
            $code=[NetLightsCoverageRust]::Code($text)
            try {$tests=[NetLightsCoverageRust]::TestLines($text)} catch {throw "Cannot classify test scope in $relative`: $($_.Exception.Message)"}
            $runtimeLines=$code -split "`n"
            for($i=0;$i -lt $runtimeLines.Length;$i++) { if($tests.Contains($i+1)) { $runtimeLines[$i]='' } }
            $inventory[$relative]=@{scope=$scope; tests=$tests; length=$runtimeLines.Length; executable=(($runtimeLines -join "`n") -match '\bfn\b'); lines=@{}}
        }
    }
    $input=Get-Item -LiteralPath $XmlPath
    if($input.Length -gt 64MB) { throw 'Coverage XML exceeds 64 MiB.' }
    $settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing=[Xml.DtdProcessing]::Ignore
    $settings.XmlResolver=$null; $settings.MaxCharactersInDocument=64MB
    $reader=[Xml.XmlReader]::Create($input.FullName,$settings)
    try { $xml=[Xml.XmlDocument]::new(); $xml.XmlResolver=$null; $xml.Load($reader) } finally { $reader.Dispose() }
    if($xml.DocumentElement.Name -ne 'coverage') { throw 'Expected Cobertura coverage root.' }
    foreach($class in $xml.SelectNodes('/coverage/packages/package/classes/class')) {
        $filename=[string]$class.filename
        $absolute=[IO.Path]::GetFullPath($filename,$sourceBase)
        $relative=[IO.Path]::GetRelativePath($sourceBase,$absolute).Replace('\','/')
        if($relative -notmatch '^native/(core|app|update-agent)/') { continue }
        if($relative -eq 'native/update-agent/src/process_tests.rs' -or $relative -match '^native/[^/]+/(tests|examples)/' -or $relative -match '^native/[^/]+/build\.rs$') { continue }
        if(-not $inventory.ContainsKey($relative)) { throw "Coverage source isn't a classified runtime file: $relative" }
        $entry=$inventory[$relative]
        foreach($line in $class.SelectNodes('lines/line')) {
            $number=0; $hits=[UInt64]0
            if(-not[int]::TryParse([string]$line.number,[ref]$number) -or $number -lt 1 -or $number -gt $entry.length -or
               -not[UInt64]::TryParse([string]$line.hits,[ref]$hits)) { throw "Invalid coverage line: $relative" }
            if($entry.tests.Contains($number)) { continue }
            if(-not $entry.lines.ContainsKey($number) -or $hits -gt $entry.lines[$number]) { $entry.lines[$number]=$hits }
        }
    }
    $groups=[ordered]@{}
    foreach($name in $floors.Keys) { $groups[$name]=@{lines=0;covered=0;floor=$floors[$name]} }
    foreach($relative in $inventory.Keys) {
        $entry=$inventory[$relative]
        if($entry.executable -and $entry.lines.Count -eq 0) { throw "Executable runtime source absent from report: $relative" }
        $groups[$entry.scope].lines += $entry.lines.Count
        $groups[$entry.scope].covered += @($entry.lines.Values | Where-Object { $_ -gt 0 }).Count
    }
    $failures=[Collections.Generic.List[string]]::new(); $total=0; $covered=0
    foreach($name in $groups.Keys) {
        $group=$groups[$name]
        if($group.lines -eq 0) { throw "Coverage scope is missing: $name" }
        $rate=[double]$group.covered/$group.lines; $group.rate=$rate
        if($rate -lt $group.floor) { $failures.Add("$name below $($group.floor)") }
        $total += $group.lines; $covered += $group.covered
    }
    $combined=[double]$covered/$total
    if($combined -lt .80) { $failures.Add('Combined below 0.80') }
    return [ordered]@{status=$(if($failures.Count){'FAIL'}else{'PASS'}); metric='runtime executable source lines'; groups=$groups; combined=@{lines=$total;covered=$covered;rate=$combined;floor=.80}; failures=@($failures)}
}
function Invoke-SelfTest {
    foreach($scope in @(
        @('native/core/src/locale.rs','Domain'),
        @('native/update-agent/src/marker.rs','Agent'),
        @('native/app/src/text.rs','App'),
        @('native/app/src/dashboard.rs','App'),
        @('native/app/src/charts/timeline.rs','App'),
        @('native/app/src/fluent.rs','App'),
        @('native/app/src/view/about.rs','App'),
        @('native/app/src/view/navigation.rs','App')
    )) {
        if((Get-Scope $scope[0]) -cne $scope[1]) {throw 'A new runtime module escaped its required coverage scope.'}
    }
    $temp=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/coverage-selftest-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    foreach($directory in @('native/core/src','native/app/src','native/update-agent/src')) { New-Item -ItemType Directory -Path (Join-Path $temp $directory) -Force | Out-Null }
    $names=@('native/core/src/monitor.rs','native/core/src/model.rs','native/core/src/history.rs','native/core/src/transport.rs','native/core/src/update.rs','native/core/src/persistence.rs','native/update-agent/src/main.rs')
    foreach($name in $names) {
        $path=Join-Path $temp $name; New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path,((1..20 | ForEach-Object {'pub fn sample() {}'}) -join "`n"))
    }
    function Write-Fixture([hashtable]$Covered=@{},[string]$Missing='', [switch]$Duplicate) {
        $classes=foreach($name in $names) {
            if($name -eq $Missing) {continue}
            $limit=if($Covered.ContainsKey((Get-Scope $name))){$Covered[(Get-Scope $name)]}else{20}
            $lines=(1..20 | ForEach-Object { '<line number="'+$_+'" hits="'+[int]($_ -le $limit)+'" />' }) -join ''
            '<class filename="'+$name+'"><lines>'+$lines+'</lines></class>'
            if($Duplicate) { '<class filename="'+$name+'"><lines><line number="1" hits="0" /></lines></class>' }
        }
        $path=Join-Path $temp 'coverage.xml'
        [IO.File]::WriteAllText($path,('<coverage><packages><package><classes>'+($classes -join '')+'</classes></package></packages></coverage>'))
        return $path
    }
    function Assert-Rejected([scriptblock]$Action,[string]$Description) {
        $rejected=$false
        try { $value=& $Action; $rejected=($null -ne $value -and $value.status -eq 'FAIL') } catch { $rejected=$true }
        if(-not $rejected) { throw "Negative control failed: $Description" }
    }
    function Assert-Throws([scriptblock]$Action,[string]$Description) {
        $rejected=$false
        try { & $Action|Out-Null } catch {$rejected=$true}
        if(-not $rejected) {throw "Negative throw control failed: $Description"}
    }
    $pass=Measure-Coverage (Write-Fixture -Duplicate) $temp
    if($pass.status -ne 'PASS' -or $pass.combined.lines -ne 140 -or $pass.combined.covered -ne 140) {throw 'Duplicate merge positive control failed.'}
    $below=@{Domain=17;Networking=11;Updates=14;App=11;Agent=15}
    foreach($name in $floors.Keys) {
        $failure=Measure-Coverage (Write-Fixture @{$name=$below[$name]}) $temp
        $reason="$name below $($floors[$name])"
        if($failure.status -ne 'FAIL' -or $failure.failures.Count -ne 1 -or $failure.failures[0] -ne $reason -or $failure.combined.rate -le .80) {throw "Independent floor control failed: $name"}
    }
    $combined=Measure-Coverage (Write-Fixture @{Domain=18;Networking=12;Updates=15;App=12;Agent=16}) $temp
    if($combined.failures.Count -ne 1 -or $combined.failures[0] -ne 'Combined below 0.80') {throw 'Combined floor negative control failed.'}
    Assert-Rejected { Measure-Coverage (Write-Fixture -Missing $names[-1]) $temp } 'missing agent'
    $source="pub fn real() {}`n#[cfg(test)]`nmod tests { /* { /* nested */ } */ fn fixture() { let x = r###`" } ] `"###; } }`npub fn after() {}`n"
    $masked=[NetLightsCoverageRust]::TestLines($source)
    if(-not $masked.Contains(3) -or $masked.Contains(4)) { throw 'Rust comments/raw strings scope control failed.' }
    $constant=[NetLightsCoverageRust]::TestLines("#[cfg(test)]`nconst TEST_ONLY: bool = 1 < 2;`npub fn runtime_after() {}`n")
    if(-not $constant.Contains(2) -or $constant.Contains(3)) {throw 'Test comparison consumed following runtime function.'}
    $arm=[NetLightsCoverageRust]::TestLines("#[cfg(test)]`nSelf::Stub { value } => {`n test_only();`n}`nSelf::Real => runtime(),`n")
    if(-not $arm.Contains(3) -or $arm.Contains(5)) {throw 'Test-only struct pattern match body was retained or runtime arm removed.'}
    Assert-Throws { [NetLightsCoverageRust]::TestLines('pub fn real() { #[cfg(test)] test_only(); runtime(); }') } 'mixed test/runtime source line'
    Assert-Throws { [NetLightsCoverageRust]::TestLines("#[cfg(any(test, windows))]`nfn ambiguous() {}") } 'ambiguous test cfg'
    Assert-Throws { [NetLightsCoverageRust]::TestLines("pub fn new()->State {`nState {`n#[cfg(test)]`ntest_flag: 1 < 2,`nruntime_flag: { dangerous() },`n}`n}") } 'comparison field cannot consume runtime field'
    $controlDetected=$false
    try {Assert-Throws {42} 'intentionally nonthrowing action'} catch {$controlDetected=$true}
    if(-not $controlDetected) {throw 'Throw-control accepted a nonthrowing action.'}
    $path=Join-Path $temp $names[0]
    [IO.File]::WriteAllText($path,((1..20|ForEach-Object {'pub fn real() {}'}) -join "`n")+"`n#[cfg(test)]`nmod tests { fn fixture() {} }`n")
    $xml=Write-Fixture @{Domain=0}
    $content=[IO.File]::ReadAllText($xml).Replace('<class filename="native/core/src/monitor.rs"><lines>','<class filename="native/core/src/monitor.rs"><lines><line number="22" hits="99999" />')
    [IO.File]::WriteAllText($xml,$content)
    if((Measure-Coverage $xml $temp).groups.Domain.covered -ne 0) {throw 'Inline test coverage inflated runtime coverage.'}
    $allCovered=Write-Fixture
    if((Measure-Coverage $allCovered $temp).status -ne 'PASS') {throw 'Unknown-file control must start with passing coverage.'}
    [IO.File]::WriteAllText((Join-Path $temp 'native/core/src/unknown.rs'),'pub fn missing() {}')
    $unknownRejected=$false
    try {Measure-Coverage $allCovered $temp|Out-Null} catch {$unknownRejected=$_.Exception.Message -like '*Unclassified runtime source:*unknown.rs*'}
    if(-not $unknownRejected) {throw 'Unclassified file was not rejected independently.'}
    Write-Output "RUN - PASS: coverage self-controls (floors, combined, missing source, duplicate merge, Rust test isolation). Artifacts: $temp"
}
if($SelfTest) { Invoke-SelfTest; return }
if(-not $CoverageFile) { throw 'CoverageFile is required.' }
$result=Measure-Coverage $CoverageFile $SourceRoot
foreach($name in $result.groups.Keys) {
    $group=$result.groups[$name]
    Write-Host ('{0}: {1:P2} ({2}/{3}), minimum {4:P0}' -f $name,$group.rate,$group.covered,$group.lines,$group.floor)
}
Write-Host ('Combined: {0:P2} ({1}/{2}), minimum 80%' -f $result.combined.rate,$result.combined.covered,$result.combined.lines)
if($OutputPath) {
    $stream=[IO.File]::Open($OutputPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes(($result|ConvertTo-Json -Depth 8)); $stream.Write($bytes) } finally { $stream.Dispose() }
}
if($result.status -ne 'PASS') { throw "Native coverage gate FAIL: $($result.failures -join ', ')" }
Write-Output 'RUN - PASS: native runtime coverage gate.'
