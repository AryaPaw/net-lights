param([switch]$Check)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'native/assets/brand.svg'
$svg = Get-Content -LiteralPath $source -Raw
[xml]$document = $svg
if ($document.DocumentElement.LocalName -cne 'svg' -or $document.DocumentElement.viewBox -cne '0 0 96 96') {
    throw 'Brand source must be an SVG with the approved 96 x 96 viewBox.'
}
$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge is required to render the canonical SVG.' }

# A separate, headless profile never accesses the user's browser or cursor.
$work = Join-Path $root ('artifacts/brand-assets-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$svgJson = ConvertTo-Json -InputObject $svg -Compress -EscapeHandling EscapeHtml
$html = @"
<!doctype html><meta charset="utf-8"><body><script>
const svg=$svgJson;
const sizes=[16,20,24,32,40,48,64,96,128,256,512];
const image=new Image();
function result(value){const p=document.createElement('pre');p.id='brand-result';p.textContent=JSON.stringify(value);document.body.append(p);}
image.onload=()=>{try{const images={};for(const size of sizes){const c=document.createElement('canvas');c.width=size;c.height=size;c.getContext('2d').drawImage(image,0,0,size,size);images[size]=c.toDataURL('image/png').split(',')[1];}result({images});}catch(e){result({error:e.message});}};
image.onerror=()=>result({error:'Canonical SVG could not be rendered'});
image.src='data:image/svg+xml;base64,'+btoa(unescape(encodeURIComponent(svg)));
</script>
"@
$page = Join-Path $work 'render.html'
[IO.File]::WriteAllText($page,$html,[Text.UTF8Encoding]::new($false))
$arguments = @('--headless=new','--disable-gpu','--disable-extensions','--no-first-run',
    '--disable-background-networking',('--user-data-dir="' + (Join-Path $work 'edge-profile') + '"'),
    '--virtual-time-budget=2000','--dump-dom',([Uri]$page).AbsoluteUri)
$process = Start-Process -FilePath $edge -ArgumentList $arguments -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $work 'dom.html') -RedirectStandardError (Join-Path $work 'edge.log')
if (-not $process.WaitForExit(30000)) {
    $process.Kill($true)
    throw "Owned headless renderer timed out. Evidence retained: $work"
}
if ($process.ExitCode -ne 0) { throw "Brand renderer failed ($($process.ExitCode)). Evidence: $work" }
$dom = Get-Content -LiteralPath (Join-Path $work 'dom.html') -Raw
$match = [regex]::Match($dom,'<pre id="brand-result">([\s\S]*?)</pre>')
if (-not $match.Success) { throw "Brand renderer returned no result. Evidence: $work" }
$result = [Net.WebUtility]::HtmlDecode($match.Groups[1].Value) | ConvertFrom-Json
if ($result.PSObject.Properties.Name -contains 'error') { throw $result.error }

$frames = @{}
foreach ($size in @(16,20,24,32,40,48,64,96,128,256,512)) {
    $bytes = [Convert]::FromBase64String($result.images."$size")
    if ($bytes.Length -lt 24 -or [Convert]::ToHexString($bytes[0..7]) -cne '89504E470D0A1A0A') { throw 'Renderer returned invalid PNG.' }
    $width = [uint32]$bytes[16]*16777216 + [uint32]$bytes[17]*65536 + [uint32]$bytes[18]*256 + $bytes[19]
    $height = [uint32]$bytes[20]*16777216 + [uint32]$bytes[21]*65536 + [uint32]$bytes[22]*256 + $bytes[23]
    if ($width -ne $size -or $height -ne $size) { throw 'PNG frame dimensions differ from the requested size.' }
    $frames[$size] = $bytes
}

$iconSizes = @(16,20,24,32,40,48,64,96,128,256)
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$iconSizes.Count)
    $offset = 6 + 16*$iconSizes.Count
    foreach ($size in $iconSizes) {
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$size].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$size].Length
    }
    foreach ($size in $iconSizes) { $writer.Write([byte[]]$frames[$size]) }
    $writer.Flush()
    $icon = $stream.ToArray()
}
finally { $writer.Dispose(); $stream.Dispose() }

$outputs = [ordered]@{
    'native/assets/app.ico' = $icon
    'src/NetLights.App/Assets/app.ico' = $icon
    'src/NetLights.App/Assets/app.png' = $frames[512]
    'docs/images/mark.png' = $frames[256]
}
foreach ($relative in $outputs.Keys) {
    $path = Join-Path $root $relative
    $bytes = [byte[]]$outputs[$relative]
    if ($Check) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($path))) -cne
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))) {
            throw "Generated branding differs: $relative"
        }
    }
    else {
        $temporary = $path + '.brand-' + [Guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllBytes($temporary,$bytes)
        Move-Item -LiteralPath $temporary -Destination $path -Force
    }
    Write-Host "PASS: $relative"
}
Write-Host "PASS: SVG rendered into 10 transparent ICO frames and both PNG assets. Evidence: $work"
