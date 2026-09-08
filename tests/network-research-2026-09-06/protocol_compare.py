"""Two bounded rounds; diagnoses protocols, never changes routes or TLS trust."""
import csv
import datetime as dt
import ipaddress
import json
from pathlib import Path
import re
import shutil
import socket
import subprocess
import tempfile
import time
import zipfile

ROOT=Path(__file__).resolve().parent
NAMES={'selectel':'https://selectel.ru/robots.txt',
       'yandex':'https://yandex.ru/robots.txt',
       'mozilla':'https://firefox-portal-detection.com/generate_204',
       'cloudflare':'https://cp.cloudflare.com/generate_204',
       'google':'https://connectivitycheck.gstatic.com/generate_204',
       'apple':'https://www.apple.com/library/test/success.html'}
flags=getattr(subprocess,'CREATE_NO_WINDOW',0)
run=ROOT/('protocols-'+dt.datetime.now().strftime('%Y%m%d-%H%M%S'))
run.mkdir()
scratch=Path(tempfile.mkdtemp(prefix='scratch-',dir=run))
rows=[]
started=time.monotonic()
for round_no in (1,2):
    for name,url in NAMES.items():
        assert time.monotonic()-started<170, 'Total research deadline'
        stamp=dt.datetime.now(dt.timezone.utc).isoformat()
        p=subprocess.run([r'C:\Windows\System32\curl.exe','--head','--silent','--show-error','--verbose',
            '--max-time','2','--connect-timeout','2','--output','NUL','--write-out','%{json}',url],
            capture_output=True,timeout=5,creationflags=flags)
        result=json.loads(p.stdout.decode('utf-8','replace'))
        trace=p.stderr.decode('utf-8','replace')[-16000:]
        (scratch/f'{round_no}-{name}.txt').write_text(trace,encoding='utf-8')
        attempted=re.search(r'Trying ([0-9.]+):443',trace)
        ip=result.get('remote_ip') or (attempted.group(1) if attempted else '')
        row=dict(utc=stamp,round=round_no,name=name,url=url,ip=ip,
                 https_code=result.get('http_code'),https_exit=p.returncode,
                 https_seconds=result.get('time_total'),https_error=result.get('errormsg',''),
                 https_reachable=200<=int(result.get('http_code',0))<=599 and p.returncode==0)
        if ip:
            ipaddress.ip_address(ip)
            command=f'$p=[System.Net.NetworkInformation.Ping]::new(); try {{$r=$p.Send("{ip}",1500); @{{status=$r.Status.ToString();ms=$r.RoundtripTime}}|ConvertTo-Json -Compress}} finally {{$p.Dispose()}}'
            try:
                ping=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-Command',command],capture_output=True,timeout=5,creationflags=flags)
                ping_result=json.loads(ping.stdout.decode('utf-8','replace'))
                row.update(icmp_status=ping_result['status'],icmp_ms=ping_result['ms'])
            except Exception as exc:
                row.update(icmp_status='diagnostic-error',icmp_ms=None,icmp_error=str(exc)[:250])
            before=time.monotonic()
            try:
                with socket.create_connection((ip,443),timeout=2):
                    row['tcp_status']='connected'
            except OSError as exc:
                row['tcp_status']=type(exc).__name__+': '+str(exc)[:150]
            row['tcp_seconds']=round(time.monotonic()-before,4)
        rows.append(row)
        print(json.dumps(row),flush=True)
        time.sleep(.5)
fields=list(dict.fromkeys(k for r in rows for k in r))
with (run/'samples.csv').open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=fields);writer.writeheader();writer.writerows(rows)
(run/'samples.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
archive=run/'traces.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
    for file in scratch.iterdir():z.write(file,file.name)
with zipfile.ZipFile(archive) as z:assert z.testzip() is None
assert scratch.resolve().parent==run.resolve() and scratch.name.startswith('scratch-')
shutil.rmtree(scratch)
print('COMPLETE '+str(run),flush=True)
