---
name: net-lights-ship
description: >-
  Net Lights (C# / .NET 10 / WinForms tray) local verification, CI, version
  bump, git push, and GitHub Release via tag v*.*.*. Use when running all tests,
  shipping, releasing, tagging, pushing main, fixing CI flakes, or the user
  says прогон, все тесты, релиз, пайплайн, CI, or 1.x.x.
---

# Net Lights ship

This repo already has CI. Do not invent a second pipeline. Local green is required and **not enough** if tests share static process state.

## Always before "tests pass" or a push of app/test code

From the repo root, Release, all three projects. Do not skip soak. Do not stop after `dotnet build`.

```ps1
dotnet test tests/NetLights.UnitTests/NetLights.UnitTests.csproj -c Release
dotnet test tests/NetLights.IntegrationTests/NetLights.IntegrationTests.csproj -c Release
dotnet test tests/NetLights.SoakTests/NetLights.SoakTests.csproj -c Release
```

CI (`.github/workflows/ci.yml`) also runs those three, then a 0.80 coverage gate, then `dotnet publish`. Coverage is collected on unit tests only.

If any fail: fix, re-run the failing project, then re-run all three. Do not push.

## Integration tests vs GitHub runners

xUnit on CI runs test classes **in parallel**. `SettingsStore.RootDirectory` is a **static** path. Tests that assign it and then `Load`/`Save`/`LoadPool` will flake if they run together.

Any test class that writes `SettingsStore.RootDirectory` must be in:

```csharp
[CollectionDefinition("SettingsFiles", DisableParallelization = true)]
public sealed class SettingsFilesCollection;

[Collection("SettingsFiles")]
public sealed class TheTestClass
```

Current members: `GeoCountryTrayTests`, `UiRendererTests`, `LocationHistoryTests`, `StateHistoryAndExportTests`. Add new writers to the same collection. Do not "fix" a flake by asserting less.

Wait for async work (`Letters`, `Visible`), not a side counter that increments before the UI/state updates.

`NotifyIcon` / WinForms tests can still flake on a headless runner. Prefer waiting on observable state with a timeout, not `Task.Delay` only.

## Do not commit

- `TODO.md`
- `docs/images/tray-mock.html` unless README uses it
- `packages.lock.json` dirt from `dotnet publish -r win-x64` (ILLink / `win-x64` graphs). Restore those files. CI publish uses `-p:RestoreLockedMode=false`.

## Local exe

Owner runs `artifacts/publish/NetLights.exe`. After tray/UI/updater/version changes: `scripts/run-local.ps1`, then confirm that process path and `ProductVersion` match `Directory.Build.props`.

## Version

Propose `x.y.Z` (patch / minor / major + one-line why) and **stop**. Edit `Directory.Build.props` only after the user confirms the number.

Default: patch for tray/copy/CI; minor for user-visible features.

## Push

Only if the **current** user message asks to push (`push`, запушь, `git push`, «заливай на GitHub»). Previous turns do not count. No force-push unless they asked.

## Release

Only if the **current** message asks for a release or a tag. Pushing `main` is not a release.

1. User confirmed the version number; it is in `Directory.Build.props`.
2. All three test projects passed locally.
3. Commit the product/test/version files (not the junk list above).
4. `git push origin main`
5. `git tag vX.Y.Z` then `git push origin vX.Y.Z` (tag must match props).
6. `.github/workflows/release.yml` on `v*.*.*` runs test, publish, Inno Setup, GitHub Release with `NetLights-Setup-win-x64-X.Y.Z.exe`. Do not `gh release create` unless the workflow failed and they still want a release.
7. Watch both `ci` (main and/or tag) and `release`. Main CI can fail while the tag CI is green if tests race. Treat main `windows` failure as a ship blocker; fix and push (if they asked) without a new tag unless they asked for another release.

Release notes body is already Russian in `release.yml`. User-facing product text stays Russian.

## After a push

`gh run list --limit 8`. Subscribe or watch until `ci` on `main` is green. If integration fails, read `--log-failed` before guessing.
