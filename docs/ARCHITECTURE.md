# Архитектура

Net Lights состоит из трех сборок.

- `NetLights.Core` владеет состоянием. `MonitorKernel` обрабатывает тики, завершения проб, смену epoch и confirmation в одном последовательном цикле. UI не пишет сетевое состояние.
- `NetLights.Networking` содержит один долгоживущий `HttpClient`/`SocketsHttpHandler`: HTTPS HEAD, HTTP/1.1 exact, без редиректов, cookies, proxy и тела. ICMP/TCP только в ручной диагностике.
- `NetLights.App` это WinForms `ApplicationContext` с `NotifyIcon`, кэшем иконки, окном состояния (только по клику), автозапуском HKCU по умолчанию и автообновлением.
- `NetLights.Updates` проверяет GitHub Releases `AryaPaw/net-lights` так же, как AuthenticatorChooser: ждёт сеть, качает Setup, запускает Inno `/VERYSILENT /FORCECLOSEAPPLICATIONS` и выходит из процесса.

Снимки групп считаются по свежим результатам текущего `networkEpoch`. Голос Reachable идет по `infrastructureId`. Offline требует полного свежего покрытия Unreachable не менее чем по трем инфраструктурам.

Планировщик запускает не более одной обычной пробы на группу каждые 2 с со сдвигом 1 с. Confirmation использует те же лимиты (4 физических, 2 на группу, 4 старта в секунду, 120 в минуту) и не передает слоты другим адресам.
