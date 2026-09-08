# Security Policy

Please report vulnerabilities privately by opening a GitHub security advisory on `AryaPaw/net-lights` or by contacting the copyright holder AryaPaw.

Do not include secrets, tokens, or personal data in public issues.

## Trust model for updates

Until an Authenticode code-signing certificate is available, installed updates are accepted only when all of the following hold:

- the release comes from GitHub Releases of `AryaPaw/net-lights`
- the release is stable (not prerelease) and newer than the running version
- asset URLs are HTTPS on GitHub
- the GitHub asset digest and a locally computed SHA-256 both match

This is a documented limitation. After a publisher certificate exists, publisher verification will become mandatory without changing the update coordinator contract.
