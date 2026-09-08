# Net Lights

[![Build](https://img.shields.io/github/actions/workflow/status/AryaPaw/net-lights/ci.yml?branch=main&logo=github)](https://github.com/AryaPaw/net-lights/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/AryaPaw/net-lights?logo=github)](https://github.com/AryaPaw/net-lights/releases/latest)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPL_3.0-blue.svg)](LICENSE)

Трей-приложение для Windows 11. Левая половина: HTTPS до сайтов в России. Правая: HTTPS через ваш VPN. Программа не меняет VPN, DNS, маршруты и сертификаты.

## Установка

Нужен Windows 11 x64. Отдельный .NET Runtime не нужен.

1. Скачайте **NetLights-Setup-win-x64-*.exe** со страницы [Releases](https://github.com/AryaPaw/net-lights/releases/latest).
2. Запустите установщик (права администратора не нужны). Программа остаётся в трее и запускается вместе с Windows.

Двойной щелчок по значку открывает окно. Крестик окно прячет, не завершает программу. Выход — из меню значка.

## Обновления

Установленная копия при запуске проверяет GitHub Releases и при выходе ставит новый Setup. Это можно выключить в окне, вкладка «Параметры».

## Сборка

Нужен [.NET SDK](https://dotnet.microsoft.com/en-us/download) 10. Из корня репозитория `run-local.cmd` публикует и запускает программу.

```ps1
dotnet test tests/NetLights.UnitTests/NetLights.UnitTests.csproj -c Release
dotnet publish src/NetLights.App/NetLights.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

[GPL-3.0](LICENSE)
