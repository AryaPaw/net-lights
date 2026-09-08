"""Bounded research only. No router changes, no persistent monitor, no RBC.
Run: python probe_research.py --seconds-per-phase 120
"""
import argparse
import concurrent.futures as cf
import csv
import datetime as dt
import email.utils
import json
import math
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parent
CURL = r'C:\Windows\System32\curl.exe'
TARGETS = [
    ('yandex', 'RU', 'yandex', 'https://yandex.ru/robots.txt', 'HEAD', 200, None),
    ('mail', 'RU', 'vk', 'https://www.mail.ru/robots.txt', 'HEAD', 200, None),
    ('rt', 'RU', 'rostelecom', 'https://www.rt.ru/robots.txt', 'HEAD', 200, None),
    ('selectel', 'RU', 'selectel', 'https://selectel.ru/robots.txt', 'HEAD', 200, None),
    ('beeline', 'RU', 'vimpelcom', 'https://www.beeline.ru/robots.txt', 'HEAD', 200, None),
    ('megafon', 'RU', 'megafon', 'https://moscow.megafon.ru/robots.txt', 'HEAD', 200, None),
    ('timeweb', 'RU', 'hll', 'https://timeweb.com/robots.txt', 'HEAD', 200, None),
    ('mts', 'RU', 'hll', 'https://www.mts.ru/robots.txt', 'HEAD', 200, None),
    ('google', 'VPN', 'google', 'https://connectivitycheck.gstatic.com/generate_204', 'GET', 204, ''),
    ('cloudflare', 'VPN', 'cloudflare', 'https://cp.cloudflare.com/generate_204', 'GET', 204, ''),
    ('mozilla', 'VPN', 'fastly', 'https://firefox-portal-detection.com/generate_204', 'GET', 204, ''),
    ('fedora', 'VPN', 'unc', 'https://fedoraproject.org/static/hotspot.txt', 'GET', 200, 'OK'),
    ('apple', 'VPN', 'akamai', 'https://www.apple.com/library/test/success.html', 'GET', 200, '<HTML><HEAD><TITLE>Success</TITLE></HEAD><BODY>Success</BODY></HTML>'),
    ('kde', 'VPN', 'fastly', 'https://networkcheck.kde.org/', 'GET', 200, 'OK'),
    ('debian', 'VPN', 'utwente', 'https://www.debian.org/', 'HEAD', 200, None),
    ('arch', 'VPN', 'haproxy', 'https://archlinux.org/', 'HEAD', 200, None),
    ('github', 'VPN', 'github', 'https://github.com/robots.txt', 'HEAD', 200, None),
    ('wikimedia', 'VPN', 'wikimedia', 'https://en.wikipedia.org/robots.txt', 'HEAD', 200, None),
]

def probe(target, timeout, phase, seq, scratch):
    name, group, infra, url, method, expected, content = target
    body_path = scratch / f'{seq}.body'
    headers_path = scratch / f'{seq}.headers'
    args = [CURL, '--silent', '--show-error', '--verbose', '--max-time', str(timeout),
            '--connect-timeout', str(timeout), '--output', str(body_path),
            '--dump-header', str(headers_path), '--write-out', '%{json}',
            '--user-agent', 'NetLights-Research/0.1', '--max-redirs', '0']
    if method == 'HEAD':
        args.append('--head')
    else:
        args += ['--max-filesize', '4096']
    args.append(url)
    started = time.monotonic()
    utc = dt.datetime.now(dt.timezone.utc).isoformat()
    try:
        p = subprocess.run(args, capture_output=True, timeout=timeout + 2,
                           creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        raw = p.stdout.decode('utf-8', 'replace')
        try:
            result = json.loads(raw)
        except ValueError:
            result = {}
        exit_code = p.returncode
        trace = p.stderr.decode('utf-8', 'replace')[-16000:]
    except subprocess.TimeoutExpired:
        result, exit_code, trace = {}, 999, 'Outer process deadline exceeded'
    wall = time.monotonic() - started
    body = body_path.read_bytes()[:4096] if body_path.exists() else b''
    headers = headers_path.read_text(errors='replace')[:16000] if headers_path.exists() else ''
    status = int(result.get('http_code', 0))
    content_ok = content is None or body.decode('utf-8', 'replace').strip() == content
    success = exit_code == 0 and status == expected and content_ok
    retry = next((line.split(':', 1)[1].strip() for line in headers.splitlines()
                  if line.lower().startswith('retry-after:')), '')
    row = dict(seq=seq, utc=utc, phase=phase, timeout_s=timeout, name=name,
               group=group, infrastructure=infra, url=url, method=method,
               success=success, code=status, exit_code=exit_code, content_ok=content_ok,
               wall_s=round(wall, 6), total_s=result.get('time_total', 0),
               dns_s=result.get('time_namelookup', 0), tcp_s=result.get('time_connect', 0),
               tls_s=result.get('time_appconnect', 0), first_byte_s=result.get('time_starttransfer', 0),
               remote_ip=result.get('remote_ip', ''), bytes=result.get('size_download', 0),
               retry_after=retry, error=result.get('errormsg', '') or (trace[-1500:] if not success else ''))
    if not success:
        (scratch / f'{seq}-{name}-failure.txt').write_text(trace, encoding='utf-8')
    return row

def percentile(values, q):
    return sorted(values)[max(0, math.ceil(len(values)*q)-1)] if values else None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--seconds-per-phase', type=int, default=120)
    ap.add_argument('--tick-seconds', type=float, default=0.5,
                    help='Global spacing; each group is polled every two ticks')
    ap.add_argument('--phase-only', choices=['baseline-4s','fast-1.5s','balanced-2s'])
    ap.add_argument('--names', help='Comma-separated subset of candidate names')
    ns = ap.parse_args()
    if not 10 <= ns.seconds_per_phase <= 180:
        raise SystemExit('phase duration must be 10..180 seconds')
    if not 0.5 <= ns.tick_seconds <= 5:
        raise SystemExit('tick seconds must be 0.5..5')
    targets_selected = [t for t in TARGETS if not ns.names or t[0] in ns.names.split(',')]
    if any(not any(t[1] == g for t in targets_selected) for g in ('RU','VPN')):
        raise SystemExit('Both groups must contain at least one target')
    run_dir = ROOT / ('run-' + dt.datetime.now().strftime('%Y%m%d-%H%M%S'))
    run_dir.mkdir()
    scratch = Path(tempfile.mkdtemp(prefix='scratch-', dir=run_dir))
    (run_dir / 'targets.json').write_text(json.dumps(targets_selected, indent=2), encoding='utf-8')
    (run_dir / 'settings.json').write_text(json.dumps(vars(ns), indent=2), encoding='utf-8')
    rows, pending, cooldown, errors = [], {}, {}, {}
    seq = 0
    groups = {g: [t for t in targets_selected if t[1] == g] for g in ('RU', 'VPN')}
    cursor = {'RU': 0, 'VPN': 0}
    print(f'RUN_DIR={run_dir}', flush=True)

    def collect():
        for future in list(pending):
            if future.done():
                name = pending.pop(future)
                row = future.result()
                rows.append(row)
                with (scratch / 'samples.jsonl').open('a', encoding='utf-8') as f:
                    f.write(json.dumps(row) + '\n')
                if row['success']:
                    errors[name] = 0
                else:
                    errors[name] = errors.get(name, 0) + 1
                    pause = 0
                    if row['code'] in (403, 429) or row['retry_after']:
                        pause = 900
                        try:
                            pause = max(pause, float(row['retry_after']))
                        except ValueError:
                            try:
                                when = email.utils.parsedate_to_datetime(row['retry_after'])
                                pause = max(pause, when.timestamp()-time.time())
                            except (ValueError, TypeError):
                                pass
                    elif row['code'] and row['code'] not in (200, 204):
                        pause = 60
                    elif errors[name] >= 2:
                        pause = 30
                    cooldown[name] = time.monotonic() + pause
                    print(f'ERROR {name} phase={row["phase"]} code={row["code"]} exit={row["exit_code"]} wall={row["wall_s"]}', flush=True)

    with cf.ThreadPoolExecutor(max_workers=4) as pool:
        for phase, timeout in [('baseline-4s', 4.0), ('fast-1.5s', 1.5), ('balanced-2s', 2.0)]:
            if ns.phase_only and phase != ns.phase_only:
                continue
            print(f'PHASE {phase}', flush=True)
            start = time.monotonic()
            ticks = math.floor(ns.seconds_per_phase / ns.tick_seconds)
            for tick in range(ticks):
                due = start + tick * ns.tick_seconds
                time.sleep(max(0, due-time.monotonic()))
                collect()
                group = 'RU' if tick % 2 == 0 else 'VPN'
                targets = groups[group]
                target = targets[cursor[group] % len(targets)]
                cursor[group] += 1
                # Skipped targets never create catch-up bursts or pressure on remaining hosts.
                if len(pending) < 4 and target[0] not in pending.values() and time.monotonic() >= cooldown.get(target[0], 0):
                    seq += 1
                    pending[pool.submit(probe, target, timeout, phase, seq, scratch)] = target[0]
            while pending:
                collect()
                time.sleep(0.05)
            print(f'PHASE_DONE {phase} cumulative={len(rows)} success={sum(r["success"] for r in rows)}', flush=True)
    rows.sort(key=lambda r: r['seq'])
    with (run_dir / 'samples.csv').open('w', encoding='utf-8-sig', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)
    summary = []
    for target in targets_selected:
        for phase in ('baseline-4s', 'fast-1.5s', 'balanced-2s'):
            records = [r for r in rows if r['name'] == target[0] and r['phase'] == phase]
            good = [r['wall_s'] for r in records if r['success']]
            summary.append(dict(name=target[0], phase=phase, n=len(records),
                                ok=len(good), p50=percentile(good,.5), p95=percentile(good,.95),
                                max_s=max(good) if good else None,
                                errors=[dict(code=r['code'], exit=r['exit_code'], error=r['error']) for r in records if not r['success']]))
    (run_dir / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    archive = run_dir / 'raw-evidence.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for file in scratch.iterdir():
            z.write(file, file.name)
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None
        assert 'samples.jsonl' in z.namelist()
    # Only this newly created temporary directory is removed after verified archival.
    assert scratch.resolve().parent == run_dir.resolve() and scratch.name.startswith('scratch-')
    shutil.rmtree(scratch)
    print(json.dumps(dict(samples=len(rows), success=sum(r['success'] for r in rows),
                          rate_limits=sum(r['code']==429 for r in rows), run_dir=str(run_dir))), flush=True)

if __name__ == '__main__':
    main()
