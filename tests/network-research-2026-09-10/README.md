# Исследование зарубежного пула – 10 сентября 2026

Локальная серия HTTPS HEAD, HTTP/1.1, дедлайн 2 с, без редиректов. Совпадает с основным зондом приложения. Сеть и роутер не менялись.

Папка `tests/network-research-2026-09-06` сохранена как история. Её PASS не отменяет эти замеры.

## Почему меняли пул

В окне «Узлы» провайдер держал 98–99% «стабильно», а зарубежная половина сидела около 61–67% «нестабильно». `world-mozilla` (`firefox-portal-detection.com`) давал около 28% «часто таймаут».

Первая серия (16 кругов, `probe_pool.py`): mozilla и fedora – 15/16 с таймаутом; debian 16/16, но p95 1.57 с у дедлайна 2 с. ripe – 5/16.

## Штатный зарубежный пул

| ID | URL | infrastructureId | confirm 20 кругов |
|---|---|---|---|
| world-cloudflare | https://cp.cloudflare.com/generate_204 | cloudflare | 18/20, p95 0.535 с |
| world-github | https://github.com/robots.txt | github | 19/20, p95 0.800 с |
| world-wikimedia | https://en.wikipedia.org/robots.txt | wikimedia | 19/20, p95 0.557 с |
| world-google | https://go.dev/robots.txt | google | 20/20, p95 0.949 с |
| world-duckduckgo | https://duckduckgo.com/robots.txt | duckduckgo | 20/20, p95 0.604 с |
| world-apache | https://www.apache.org/robots.txt | fastly | 20/20, p95 1.305 с |
| world-arch | https://archlinux.org/ | haproxy | 20/20, p95 0.753 с |

Провайдерские семь точек не менялись.

Не дублировать Cloudflare: `iana.org`, `w3.org`, `ietf.org`, `nodejs.org`, `example.com` в этой сети тоже Cloudflare. `go.dev` – Google Frontend, не gstatic `generate_204`.

Повтор: `python -u tests/network-research-2026-09-10/confirm_pool.py`
