# Dependabot

GitHub's Dependabot is enabled on this repo. It does not fix Net Lights product bugs. It opens pull requests when NuGet packages or GitHub Actions versions change, and when a known CVE is in the dependency graph.

Most of our NuGet packages are **test and CI** tools. They are not shipped inside `NetLights.exe`. Routine major upgrades are optional and often break lockfiles.

## Policy

- Version updates: monthly, one grouped PR per ecosystem, **minor and patch only**.
- Semver-major bumps are ignored by Dependabot. Do them only on purpose, with lockfile and CI updates.
- Security / CVE PRs: review the advisory and merge if CI passes.
- Do not merge Dependabot PRs just to clear email.
- After a NuGet change, update `Directory.Packages.props` and every affected `packages.lock.json`. CI restore uses `--locked-mode`.
- `dotnet publish -r win-x64` injects ILLink. CI publish uses `-p:RestoreLockedMode=false`.

Config: `.github/dependabot.yml`.
