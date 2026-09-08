# Net Lights

[![Build](https://img.shields.io/github/actions/workflow/status/AryaPaw/net-lights/ci.yml?branch=main&logo=github)](https://github.com/AryaPaw/net-lights/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/AryaPaw/net-lights?logo=github)](https://github.com/AryaPaw/net-lights/releases/latest)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPL_3.0-blue.svg)](LICENSE)

A Windows 11 tray app. Left half: HTTPS to provider sites. Right half: HTTPS through your VPN. It does not change VPN, DNS, routing, or certificates.

## Install

Windows 11, 64-bit. No extra .NET runtime.

1. Get **NetLights-Setup-win-x64-*.exe** from [Releases](https://github.com/AryaPaw/net-lights/releases/latest).
2. Run it (no admin). It stays in the tray and starts with Windows.

Double-click the icon for the window. Closing the window does not quit; use **Exit** in the tray menu.

A portable ZIP is also in Releases. Auto-update only works for the installed copy: it checks GitHub once a day and applies the new Setup on Exit.

## Build

[.NET SDK](https://dotnet.microsoft.com/en-us/download) 10. From the repo root, `run-local.cmd` publishes and starts it.

```ps1
dotnet test tests/NetLights.UnitTests/NetLights.UnitTests.csproj -c Release
dotnet publish src/NetLights.App/NetLights.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

[GPL-3.0](LICENSE)
