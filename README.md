# Net Lights

Tray-монитор HTTPS-доступности двух пулов адресов: РФ (слева) и VPN (справа). VPN на роутере принимается как данность. Программа не настраивает VPN, DNS, маршруты и сертификаты Windows.

Лицензия: GPL-3.0, copyright AryaPaw.

## Установка

Установите per-user установщик `NetLights-Setup-win-x64-X.Y.Z.exe` в `%LocalAppData%\Programs\NetLights`. Приложение запускается как `asInvoker` и не требует прав администратора.

Портативный ZIP `NetLights-win-x64-X.Y.Z.zip` тоже публикуется. Автообновление рассчитано на установленную копию и применяется при обычном выходе, без автоматического перезапуска.

До появления Authenticode модель доверия обновлений описана в `SECURITY.md`.

## Сборка

```powershell
dotnet restore NetLights.slnx --locked-mode
dotnet build NetLights.slnx -c Release
dotnet test tests/NetLights.UnitTests/NetLights.UnitTests.csproj -c Release
dotnet test tests/NetLights.IntegrationTests/NetLights.IntegrationTests.csproj -c Release
dotnet publish src/NetLights.App/NetLights.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
dotnet publish src/NetLights.UpdateAgent/NetLights.UpdateAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

Документы: `docs/ACCEPTANCE.md`, `docs/ARCHITECTURE.md`, `docs/USER-GUIDE.md`, `docs/KNOWN-LIMITATIONS.md`. Контракт: `docs/contracts/IMPLEMENTATION-PLAN.md`.
