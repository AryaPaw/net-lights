# Net Lights

[![Build](https://img.shields.io/github/actions/workflow/status/AryaPaw/net-lights/ci.yml?branch=main&logo=github)](https://github.com/AryaPaw/net-lights/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/AryaPaw/net-lights?logo=github)](https://github.com/AryaPaw/net-lights/releases/latest)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPL_3.0-blue.svg)](LICENSE)

Трей-приложение для Windows 11. Проверяет HTTPS HEAD контрольных адресов: это не весь интернет и не UDP/QUIC. Программа не меняет VPN, DNS, маршруты и сертификаты.

Значок в трее: круг из двух половин. Слева провайдер, справа VPN. Двойной щелчок открывает окно с таблицей узлов, задержками и причинами сбоев.

Цвет каждой половины свой:

- **зелёный** — доступно: не меньше двух независимых HTTPS-подтверждений
- **жёлтый** — ограничено: есть только одно независимое подтверждение
- **красный** — нет ответа: нет сети по сигналу Windows или несколько независимых HTTPS-направлений недоступны
- **серый** — нет свежих данных

Пауза в меню значка красит обе половины в синий.

![Значок Net Lights в трее Windows](docs/images/tray.png)

![Окно Net Lights после двойного щелчка по значку](docs/images/window.png)

## Установка

Нужен Windows 11 x64. Отдельный .NET Runtime не нужен.

1. Скачайте **NetLights-Setup-win-x64-*.exe** со страницы [Releases](https://github.com/AryaPaw/net-lights/releases/latest).
2. Запустите установщик (права администратора не нужны). Программа остаётся в трее и запускается вместе с Windows. Окно само не открывается.

Крестик прячет окно в трей, программу не завершает. Выход — из меню значка.

## Обновления

Установленная копия ждёт сеть, проверяет GitHub Releases, качает Setup, ставит его тихим Inno и снова запускается в трей. Во вкладке «Параметры» можно выключить автообновление или нажать «Проверить обновления» вручную. После сбоя повтор через две минуты.

## Сборка

Нужен [.NET SDK](https://dotnet.microsoft.com/en-us/download) 10. Из корня репозитория `run-local.cmd` публикует и запускает программу.

```ps1
dotnet test tests/NetLights.UnitTests/NetLights.UnitTests.csproj -c Release
dotnet publish src/NetLights.App/NetLights.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

[GPL-3.0](LICENSE)
