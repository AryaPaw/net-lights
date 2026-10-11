# Net Lights instructions

## Version proposals

Only propose or change a version when the active task requests a version proposal or release. Prefer patch for small fixes such as tray behavior, copy, or CI; prefer minor for user-visible features. Propose major only when explicitly requested.

If the release version is unspecified, propose an exact number with a brief reason and wait before editing version metadata. An exact version already supplied by the user needs no repeated confirmation. Ordinary fixes, verification, and branch pushes don't require an unsolicited version decision.

## Publication and preserved work

Follow the global Git/publication policy and actual repository verification sources. Authorization persists for the same active task, action, and target unless revoked; a new message doesn't reset it. A branch push alone doesn't authorize version bumps, tags, or releases.

The existing `.cursor/skills/net-lights-ship/SKILL.md` contains project shipping context. Consult it when that workflow applies and verify version-sensitive commands/current CI against the actual files. Preserve unrelated staged/unstaged work, including pre-existing lockfile changes; don't blindly restore files after a build.
