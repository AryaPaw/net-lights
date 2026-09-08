# Измерения контрольных адресов

Результат короткого испытания из текущей сети. Это не SLA и не доказательство отсутствия блокировок при круглосуточной работе.

| Адрес | Успехи / запросы | Медиана, с | P95 успешных, с | Максимум успешных, с |
|---|---:|---:|---:|---:|
| `https://yandex.ru/robots.txt` | 54/54 | 0.125 | 0.14 | 0.281 |
| `https://connectivitycheck.gstatic.com/generate_204` | 40/45 | 0.328 | 1.5 | 2.812 |
| `https://www.mail.ru/robots.txt` | 54/54 | 0.094 | 0.109 | 0.109 |
| `https://cp.cloudflare.com/generate_204` | 53/54 | 0.328 | 0.437 | 0.563 |
| `https://www.rt.ru/robots.txt` | 54/54 | 0.265 | 0.281 | 0.282 |
| `https://firefox-portal-detection.com/generate_204` | 54/54 | 0.328 | 0.422 | 0.468 |
| `https://selectel.ru/robots.txt` | 54/54 | 0.125 | 0.14 | 0.14 |
| `https://fedoraproject.org/static/hotspot.txt` | 54/54 | 0.656 | 0.859 | 0.875 |
| `https://www.beeline.ru/robots.txt` | 53/53 | 0.125 | 0.172 | 0.203 |
| `https://www.apple.com/library/test/success.html` | 19/31 | 0.375 | 0.969 | 0.969 |
| `https://moscow.megafon.ru/robots.txt` | 53/53 | 0.234 | 0.265 | 0.281 |
| `https://networkcheck.kde.org/` | 45/45 | 0.328 | 0.375 | 0.5 |
| `https://timeweb.com/robots.txt` | 53/53 | 0.125 | 0.359 | 1.157 |
| `https://www.debian.org/` | 54/54 | 0.609 | 1.016 | 1.609 |
| `https://www.mts.ru/robots.txt` | 45/45 | 0.125 | 0.156 | 0.219 |
| `https://archlinux.org/` | 53/53 | 0.406 | 0.547 | 0.75 |
| `https://github.com/robots.txt` | 8/8 | 0.297 | 0.36 | 0.36 |
| `https://en.wikipedia.org/robots.txt` | 8/8 | 0.328 | 1.375 | 1.375 |

P95 рассчитан методом ближайшего ранга, только по успешным запросам. Ошибки учтены отдельно; малые выборки не характеризуют редкие задержки.

Время включает запуск curl. Каждый запрос создает новое соединение; будущий клиент с повторным использованием соединений нужно проверять отдельно.

## Фазы

- run-20260906-223658, baseline-4s: 227/232 успешных.
- run-20260906-223658, fast-1.5s: 231/236 успешных.
- run-20260906-223658, balanced-2s: 230/238 успешных.
- run-20260906-224329, balanced-2s: 120/120 успешных.

## Ошибки

- 2026-09-06T19:37:11.334526+00:00: apple, baseline-4s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:37:19.337691+00:00: apple, baseline-4s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:37:59.327450+00:00: apple, baseline-4s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:38:07.327878+00:00: apple, baseline-4s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:38:39.334022+00:00: apple, baseline-4s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:38:59.333494+00:00: google, fast-1.5s, curl=28, HTTP=0: Connection timed out after 1513 milliseconds
- 2026-09-06T19:39:31.338946+00:00: google, fast-1.5s, curl=28, HTTP=0: Connection timed out after 1513 milliseconds
- 2026-09-06T19:39:43.333379+00:00: apple, fast-1.5s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:40:23.327737+00:00: apple, fast-1.5s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:40:31.330059+00:00: apple, fast-1.5s, curl=28, HTTP=0: Connection timed out after 1505 milliseconds
- 2026-09-06T19:40:59.330855+00:00: google, balanced-2s, curl=28, HTTP=0: Connection timed out after 2007 milliseconds
- 2026-09-06T19:41:11.325630+00:00: apple, balanced-2s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:41:15.336047+00:00: google, balanced-2s, curl=28, HTTP=0: Connection timed out after 2011 milliseconds
- 2026-09-06T19:41:27.330117+00:00: apple, balanced-2s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:41:40.329073+00:00: cloudflare, balanced-2s, curl=28, HTTP=0: Connection timed out after 2004 milliseconds
- 2026-09-06T19:42:31.331532+00:00: apple, balanced-2s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:42:39.334767+00:00: apple, balanced-2s, curl=35, HTTP=0: schannel: failed to receive handshake, SSL/TLS connection failed
- 2026-09-06T19:42:51.329593+00:00: google, balanced-2s, curl=28, HTTP=0: Connection timed out after 2011 milliseconds
