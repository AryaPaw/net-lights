# Приемка Net Lights

Версия документов: 1.1 (2026-09-07).

| Файл | SHA256 |
|---|---|
| IMPLEMENTATION-PLAN.md | a6d2a1f1a03936ed985df29fb21a0dd675dfc23768381fc7a25c5de7679065fa |
| TEST-MATRIX.md | bfe6b25c193a2124a0b2b7bbec87dc5288c3ef34208919f702a41382a2b65a18 |
| AGENT-PROMPT.md | b9d08ae3cba487c089f70d86efd4f289113f0c1fbe8c3b937e557a626f951d0e |

Исходный git SHA репозитория на момент реализации: `b2fb55d47d931f3eac58ee164c3991a0b84142d2` (до коммита исходников). SDK: 10.0.204. Runtime host: 10.0.8. Windows 11 build 26200.

## Статусы готовности

| Gate | Статус |
|---|---|
| CODE COMPLETE | да, исходники и Release ZIP собраны |
| AUTOMATED VERIFIED | да для T01-T28, T29-T43 (локальные fixtures), короткого soak |
| USER-VISIBLE VERIFIED | частично: процесс запускался 8 с из каталога с пробелами и кириллицей; скриншота живого tray нет |
| AUTONOMOUS SOAK VERIFIED | нет (T58 NOT RUN по решению владельца) |
| OWNER ACCEPTED | не назначается агентом |

## Ledger

ID | Статус | Команда/процедура | build | дата UTC | ожидаемое/наблюдаемое | evidence | ограничение
---|---|---|---|---|---|---|---
T01 | RUN — PASS | `dotnet test tests/NetLights.UnitTests -c Release --filter T01` | local Release | 2026-09-07 | Unknown затем Online | unit TRX | виртуальное время
T02 | RUN — PASS | filter T02 | local Release | 2026-09-07 | Online без confirm-шторма | unit | 
T03 | RUN — PASS | filter T03 | local Release | 2026-09-07 | Online 72 виртуальных часа с одним мертвым адресом | unit 2 мин wall | не реальные 72 ч
T04 | RUN — PASS | filter T04 | local Release | 2026-09-07 | две живые точки дают Online | unit theory | 
T05 | RUN — PASS | filter T05 | local Release | 2026-09-07 | Limited, не Offline | unit | 
T06 | RUN — PASS | GroupStateCalculatorTests | local Release | 2026-09-07 | 4 Unreachable + 3 missing = Unknown | unit | 
T07 | RUN — PASS | GroupStateCalculatorTests | local Release | 2026-09-07 | 7 Unreachable = Offline | unit | 
T08 | RUN — PASS | GroupStateCalculatorTests | local Release | 2026-09-07 | общий CDN = один голос | unit | 
T09 | RUN — PASS | filter T09 | local Release | 2026-09-07 | attemptId побеждает порядок доставки | unit | 
T10 | RUN — PASS | filter T10 | local Release | 2026-09-07 | старый epoch и Cancelled не отказ | unit | 
T11 | RUN — PASS | filter T11 | local Release | 2026-09-07 | TTL снимает зеленый | unit | 
T12 | RUN — PASS | filter T12 | local Release | 2026-09-07 | UTC сдвиг не ломает monotonic TTL | unit SplitTimeProvider | 
T13 | RUN — PASS | T13_Permutations | local Release | 2026-09-07 | инварианты 4^7 | unit | 
T14 | RUN — PASS | filter T14 | local Release | 2026-09-07 | ротация без перекоса групп | unit | 
T15 | RUN — PASS | filter T15 | local Release | 2026-09-07 | окна 4/с и 120/мин | unit | 
T16 | RUN — PASS | filter T16 | local Release | 2026-09-07 | frozen держат physical permit | unit | 
T17 | RUN — PASS | filter T17 | local Release | 2026-09-07 | min interval 8 с | unit | 
T18 | RUN — PASS | filter T18 | local Release | 2026-09-07 | одиночный отказ не стартует confirm | unit | 
T19 | RUN — PASS | filter T19 | local Release | 2026-09-07 | два infra failure = один episode | unit | 
T20 | RUN — PASS | filter T20 | local Release | 2026-09-07 | два успеха останавливают episode | unit | 
T21 | RUN — PASS | filter T21 | local Release | 2026-09-07 | не больше семи новых вызовов | unit | 
T22 | RUN — PASS | filter T22 | local Release | 2026-09-07 | один episode, cooldown | unit | 
T23 | RUN — PASS | filter T23 | local Release | 2026-09-07 | 429 не выдумывает coverage | unit | 
T24 | RUN — PASS | filter T24 | local Release | 2026-09-07 | обе группы Offline <=14 с, шаг 50 мс | unit | виртуальный транспорт
T25 | RUN — PASS | filter T25 | local Release | 2026-09-07 | полное восстановление 3/5 с; частичное 15 с | unit | 
T26 | RUN — PASS | filter T26 | local Release | 2026-09-07 | Offline 72 виртуальных часа без роста очереди | unit | не реальные 24 ч
T27 | RUN — PASS | filter T27 | local Release | 2026-09-07 | Retry-After seconds/date/invalid/huge | unit | 
T28 | RUN — PASS | filter T28 | local Release | 2026-09-07 | internal error = Unknown | unit | 
T29 | RUN — PASS | integration HttpsProbeTests | local Release | 2026-09-07 | HEAD 200/204 Reachable | loopback TLS | 
T30 | RUN — PASS | T30 | local Release | 2026-09-07 | 302 Reachable | loopback | проверка Location ограничена
T31 | RUN — PASS | T31 | local Release | 2026-09-07 | 404/405 Reachable | loopback | 
T32 | RUN — PASS | T32 | local Release | 2026-09-07 | 403/429/503 Reachable+pause | loopback | 
T33 | RUN — PASS | T33 | local Release | 2026-09-07 | TLS hang не Reachable | loopback | 
T34 | RUN — PASS | T34 | local Release | 2026-09-07 | медленные заголовки не Reachable | loopback | 
T35 | RUN — PASS | T35 | local Release | 2026-09-07 | plaintext не Reachable | loopback | 
T36 | RUN — PASS | T36 | local Release | 2026-09-07 | untrusted/expired/wrong name | process-local trust | Windows store не менялся
T37 | RUN — PASS | T37 NXDOMAIN | local Release | 2026-09-07 | .invalid не Reachable | DNS имени | задержка DNS/VM NOT RUN
T38 | RUN — PASS | T38 | local Release | 2026-09-07 | reset/refuse не Reachable | loopback | 
T39 | RUN — PASS | T39 | local Release | 2026-09-07 | huge headers и 100-then-hang | loopback | 
T40 | RUN — PASS | T40 | local Release | 2026-09-07 | память не скачет на HEAD+body | loopback | 
T41 | RUN — PASS | T41 | local Release | 2026-09-07 | отмена не Reachable | loopback | 
T42 | NOT RUN | dual-stack VM | - | 2026-09-07 | фактическая стратегия .NET IPv4/IPv6 | - | нет отдельной VM dual-stack
T43 | RUN — PASS | T43 + конструкция клиента | local Release | 2026-09-07 | UseCookies/UseProxy false, HTTP/1.1 exact | code+ctor | нет долгого leak soak сокетов
T44 | RUN — PASS | Start-Process NetLights.exe 8 с | publish win-x64 | 2026-09-07 | процесс жил, затем завершен | artifacts/publish | нет скриншота tray
T45 | RUN — PASS | UiRendererTests 16 комбинаций | local Release | 2026-09-07 | прозрачный зазор | %TEMP%\net-lights-icons | не живая панель задач
T46 | NOT RUN | DPI 125/150/200% живой tray | - | 2026-09-07 | - | - | нет смены DPI сеанса
T47 | NOT RUN | 10000 смен GDI | - | 2026-09-07 | - | - | нет счетчиков GDI после прогрева
T48 | NOT RUN | 10 повторных запусков | - | 2026-09-07 | mutex в Program.cs | - | сделан один запуск
T49 | NOT RUN | restart Explorer | sandbox | 2026-09-07 | Sandbox запущен, Explorer не рестартовали | WindowsSandboxServer | не трогали рабочий стол владельца
T50 | NOT RUN | sleep/resume реальный | - | 2026-09-07 | epoch API есть | - | сон ПК не меняли
T51 | NOT RUN | Exit при зависшей сети UI | - | 2026-09-07 | DisposeAsync в контексте | - | нет UI-сценария зависания
T52 | NOT RUN | изолированный HKCU | - | 2026-09-07 | Quote() реализован | - | реестр владельца не меняли
T53 | RUN — PASS | validator + LoadPool | local Release | 2026-09-07 | невалидный набор отклоняется | unit+integration | read-only каталог не проверялся отдельно
T54 | NOT RUN | 30 мин чередования | tools/Run-Soak.ps1 | 2026-09-07 | короткий 20 с localhost PASS | soak test | 30 мин wall не ждали
T55 | NOT RUN | ping есть HTTPS нет в UI | ManualDiagnostics | 2026-09-07 | код разделяет ICMP/TCP/HTTPS | - | нет полного UI-прогона
T56 | NOT RUN | 15 мин внешний smoke | - | 2026-09-07 | - | - | публичные сайты не нагружали 15 мин
T57 | NOT RUN | 60 мин resource soak | tools/Run-Soak.ps1 | 2026-09-07 | 20 с localhost PASS | soak | 60 мин не ждали
T58 | NOT RUN | 24 ч soak | tools/Run-Soak.ps1 | 2026-09-07 | - | - | решение владельца, ПК не оставлять включенным
T59 | NOT RUN | чистый профиль без runtime | Windows Sandbox start | 2026-09-07 | Sandbox процесс стартовал, in-sandbox verify нет | WindowsSandboxServer | нет независимого лога запуска exe внутри sandbox
T60 | NOT RUN | GitHub Actions clean checkout | `.github/workflows/ci.yml` | 2026-09-07 | workflow написан | - | CI на GitHub не запускался
T61 | NOT RUN | kill fixture runner | - | 2026-09-07 | - | - | 
T62 | RUN — PASS | сверка контракта и hashes | this file | 2026-09-07 | hashes ТЗ совпали, T58 честно NOT RUN | docs/ACCEPTANCE.md | незавершенные gates перечислены

## Повторение

```powershell
dotnet restore NetLights.slnx --locked-mode
dotnet test tests/NetLights.UnitTests/NetLights.UnitTests.csproj -c Release
dotnet test tests/NetLights.IntegrationTests/NetLights.IntegrationTests.csproj -c Release
dotnet test tests/NetLights.SoakTests/NetLights.SoakTests.csproj -c Release
dotnet publish src/NetLights.App/NetLights.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

Локальный ZIP: `artifacts/NetLights-win-x64.zip`.
