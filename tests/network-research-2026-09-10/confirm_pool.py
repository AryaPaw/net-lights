"""Confirm the chosen foreign pool: HTTPS HEAD, HTTP/1.1, 2s."""
from __future__ import annotations

import json
import statistics
import subprocess
import time
from pathlib import Path

CURL = r"C:\Windows\System32\curl.exe"
DEADLINE = 2.0
ROUNDS = 20
OUT = Path(__file__).resolve().parent / "confirm.json"

TARGETS = [
    ("cloudflare", "https://cp.cloudflare.com/generate_204"),
    ("github", "https://github.com/robots.txt"),
    ("wikimedia", "https://en.wikipedia.org/robots.txt"),
    ("google", "https://go.dev/robots.txt"),
    ("duckduckgo", "https://duckduckgo.com/robots.txt"),
    ("apache", "https://www.apache.org/robots.txt"),
    ("arch", "https://archlinux.org/"),
]


def probe(url: str) -> dict:
    args = [
        CURL, "-sS", "--http1.1", "--head", "--max-redirs", "0",
        "--max-time", str(DEADLINE), "--connect-timeout", str(DEADLINE),
        "--user-agent", "NetLights-PoolCheck/1.0",
        "-o", "NUL", "-w", "%{http_code} %{time_total} %{errormsg}",
        url,
    ]
    try:
        proc = subprocess.run(args, capture_output=True, timeout=DEADLINE + 2, text=True)
        parts = (proc.stdout or "").strip().split(" ", 2)
        code = int(parts[0]) if parts and parts[0].isdigit() else 0
        total = float(parts[1]) if len(parts) > 1 else 0.0
        err = parts[2] if len(parts) > 2 else ""
        ok = proc.returncode == 0 and 200 <= code <= 599
        return {"ok": ok, "code": code, "total": round(total, 4), "err": err}
    except subprocess.TimeoutExpired:
        return {"ok": False, "code": 0, "total": DEADLINE, "err": "process-timeout"}


def main() -> None:
    rows = {name: [] for name, _ in TARGETS}
    for i in range(ROUNDS):
        for name, url in TARGETS:
            result = probe(url)
            rows[name].append(result)
            print(f"{i+1:02d} {name:12} ok={int(result['ok'])} {result['code']:3} {result['total']:.3f}s {result['err'][:40]}", flush=True)
    summary = []
    for name, url in TARGETS:
        samples = rows[name]
        oks = [s["total"] for s in samples if s["ok"]] or [DEADLINE]
        summary.append({
            "name": name,
            "url": url,
            "ok": sum(1 for s in samples if s["ok"]),
            "n": len(samples),
            "p50": round(statistics.median(oks), 3),
            "p95": round(sorted(oks)[max(0, int(len(oks) * 0.95) - 1)], 3),
        })
    OUT.write_text(json.dumps({"rounds": ROUNDS, "summary": summary}, indent=2), encoding="utf-8")
    print("\n=== confirm ===")
    for row in summary:
        print(f"{row['ok']:2}/{row['n']} p50={row['p50']:.3f} p95={row['p95']:.3f}  {row['name']}")


if __name__ == "__main__":
    main()
