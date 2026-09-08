# Contributing

Net Lights is GPL-3.0 software. By contributing you agree that your changes are licensed under GPL-3.0 and that copyright notices are preserved.

## Development

```powershell
dotnet restore NetLights.slnx --locked-mode
dotnet test tests/NetLights.UnitTests/NetLights.UnitTests.csproj -c Release
dotnet test tests/NetLights.IntegrationTests/NetLights.IntegrationTests.csproj -c Release
```

Do not commit `.cursor/`, `artifacts/`, `bin/`, `obj/`, PDB, or ZIP archives.

Keep the HTTPS-only probe model, 7+7 endpoints, limits 4/2/4/120, and 2 second cadence unless the owner explicitly changes them.
