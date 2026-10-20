# Security policy

## Reporting a vulnerability

Report suspected vulnerabilities through [GitHub private vulnerability reporting](https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/security/advisories/new). Do not disclose an unpatched vulnerability in a public issue or pull request.

Include the affected revision, configuration relevant to the issue, expected and actual behavior, and minimal reproduction steps. Use synthetic accounts and disposable data. Do not send production credentials, access tokens, client files, database backups or personal information.

If the private reporting form is unavailable, do not post the vulnerability details publicly. Open a general issue asking maintainers to enable private vulnerability reporting, without describing the exploit or affected deployment.

## Supported revisions

This project is a prerelease. Security fixes target the latest reviewed revision on `main`; there is no separately maintained release branch or guaranteed response time. A maintainer will assess reports and coordinate any disclosure through the private report.

## Deployment considerations

Review the authentication limitations documented in the profiles and the [Launcher guide](docs/launcher.md#login-and-authentication) before exposing a deployment. A launch ticket does not attest client integrity or make every game-login path password-protected. Keep administration and database listeners on a controlled network, and use the authentication and transport protections appropriate to the deployment.

The repository does not distribute game clients or their proprietary resources. Public validation covers the redistributable source and synthetic tests; local-data and native-client checks have separate prerequisites.

Chinese translation: [SECURITY.zh.md](SECURITY.zh.md).
