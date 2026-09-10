"""HTTPS HEAD probes matching the app: HTTP/1.1, 2s deadline, no redirects."""
from __future__ import annotations

import json
import statistics
import subprocess
import time
from pathlib import Path

CURL = r"C:\Windows\System32\curl.exe"
DEADLINE = 2.0
ROUNDS = 16
OUT = Path(__file__).resolve().parent / "results.json"

TARGETS = [
    ("keep", "cloudflare", "https://cp.cloudflare.com/generate_204"),
    ("keep", "mozilla-portal", "https://firefox-portal-detection.com/generate_204"),
    ("keep", "fedora", "https://fedoraproject.org/static/hotspot.txt"),
    ("keep", "debian", "https://www.debian.org/"),
    ("keep", "arch", "https://archlinux.org/"),
    ("keep", "github", "https://github.com/robots.txt"),
    ("keep", "wikimedia", "https://en.wikipedia.org/robots.txt"),
    ("keep", "yandex", "https://yandex.ru/robots.txt"),
    ("keep", "mail", "https://www.mail.ru/robots.txt"),
    ("cand", "kernel", "https://www.kernel.org/robots.txt"),
    ("cand", "ietf", "https://www.ietf.org/robots.txt"),
    ("cand", "iana", "https://www.iana.org/robots.txt"),
    ("cand", "ripe", "https://www.ripe.net/robots.txt"),
    ("cand", "w3c", "https://www.w3.org/robots.txt"),
    ("cand", "gnu", "https://www.gnu.org/robots.txt"),
    ("cand", "freebsd", "https://www.freebsd.org/robots.txt"),
    ("cand", "openbsd", "https://www.openbsd.org/robots.txt"),
    ("cand", "python", "https://www.python.org/robots.txt"),
    ("cand", "postgresql", "https://www.postgresql.org/robots.txt"),
    ("cand", "openssl", "https://www.openssl.org/robots.txt"),
    ("cand", "apache", "https://www.apache.org/robots.txt"),
    ("cand", "ubuntu", "https://ubuntu.com/robots.txt"),
    ("cand", "mozilla-org", "https://www.mozilla.org/robots.txt"),
    ("cand", "nodejs", "https://nodejs.org/robots.txt"),
    ("cand", "go-dev", "https://go.dev/robots.txt"),
    ("cand", "isc", "https://www.isc.org/robots.txt"),
    ("cand", "example", "https://www.example.com/"),
    ("cand", "oneone", "https://one.one.one.one/"),
    ("cand", "duckduckgo", "https://duckduckgo.com/robots.txt"),
]


def probe(url: str) -> dict:
    args = [
        CURL, "-sS", "--http1.1", "--head", "--max-redirs", "0",
        "--max-time", str(DEADLINE), "--connect-timeout", str(DEADLINE),
        "--user-agent", "NetLights-PoolCheck/1.0",
        "-o", "NUL", "-w", "%{http_code} %{time_total} %{errormsg}",
        url,
    ]
    started = time.perf_counter()
    try:
        proc = subprocess.run(args, capture_output=True, timeout=DEADLINE + 2, text=True)
        wall = time.perf_counter() - started
        parts = (proc.stdout or "").strip().split(" ", 2)
        code = int(parts[0]) if parts and parts[0].isdigit() else 0
        total = float(parts[1]) if len(parts) > 1 else wall
        err = parts[2] if len(parts) > 2 else (proc.stderr or "").strip()
        ok = proc.returncode == 0 and 200 <= code <= 599
        return {"ok": ok, "code": code, "total": round(total, 4), "wall": round(wall, 4), "err": err, "exit": proc.returncode}
    except subprocess.TimeoutExpired:
        return {"ok": False, "code": 0, "total": DEADLINE, "wall": DEADLINE + 2, "err": "process-timeout", "exit": 999}


def main() -> None:
    rows: dict[str, list] = {name: [] for _, name, _ in TARGETS}
    urls = {name: url for _, name, url in TARGETS}
    for i in range(ROUNDS):
        for _, name, url in TARGETS:
            result = probe(url)
            rows[name].append(result)
            print(f"{i+1:02d} {name:16} ok={int(result['ok'])} {result['code']:3} {result['total']:.3f}s {result['err'][:40]}")
    summary = []
    for _, name, url in TARGETS:
        samples = rows[name]
        oks = [s for s in samples if s["ok"]]
        times = [s["total"] for s in oks] or [DEADLINE]
        summary.append({
            "name": name,
            "url": url,
            "ok": sum(1 for s in samples if s["ok"]),
            "n": len(samples),
            "p50": round(statistics.median(times), 3),
            "p95": round(sorted(times)[max(0, int(len(times) * 0.95) - 1)], 3),
            "codes": sorted({s["code"] for s in samples}),
            "errors": sorted({s["err"] for s in samples if s["err"]})[:5],
        })
    summary.sort(key=lambda r: (-r["ok"], r["p95"], r["p50"]))
    OUT.write_text(json.dumps({"rounds": ROUNDS, "deadline": DEADLINE, "summary": summary}, indent=2), encoding="utf-8")
    print("\n=== summary ===")
    for row in summary:
        print(f"{row['ok']:2}/{row['n']} p50={row['p50']:.3f} p95={row['p95']:.3f}  {row['name']:16} {row['url']}")


if __name__ == "__main__":
    main()
