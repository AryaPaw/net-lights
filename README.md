# Net Lights

Tray-монитор HTTPS-доступности двух пулов адресов: Провайдер (слева) и VPN (справа). VPN на роутере принимается как данность. Программа не настраивает VPN, DNS, маршруты и сертификаты Windows.

## Запуск

Распакуйте ZIP self-contained `win-x64` и запустите `NetLights.exe`. Установленный .NET runtime не нужен. Повторный запуск в том же сеансе не создает второй экземпляр.

## Сборка

```powershell
dotnet restore NetLights.slnx --locked-mode
dotnet build NetLights.slnx -c Release
dotnet test tests/NetLights.UnitTests/NetLights.UnitTests.csproj -c Release
dotnet test tests/NetLights.IntegrationTests/NetLights.IntegrationTests.csproj -c Release
dotnet publish src/NetLights.App/NetLights.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

Документы приемки: `docs/ACCEPTANCE.md`, `docs/ARCHITECTURE.md`, `docs/USER-GUIDE.md`, `docs/KNOWN-LIMITATIONS.md`.
