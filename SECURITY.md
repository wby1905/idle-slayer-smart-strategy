# Security Policy

Smart Strategy runs inside the game process. It can automate progression and change client-side effects, so back up saves before trying a development build.

## Supported versions

| Version | Supported |
| --- | --- |
| 1.1.0 (unreleased) | Development build; in-game validation pending |
| 1.0.x | Yes |
| Earlier versions | No |

Update to the newest available release before reporting a security problem.

Refinery purchases use a separate budget journal and stop when a charge cannot be confirmed. Keep that journal intact while investigating an unexpected charge; restarting the game does not clear its pending marker.

## Reporting a vulnerability

Do not include save files, account identifiers, purchase records, proxy credentials or full logs containing personal paths in a public issue. Share only the smallest relevant log section.

If a vulnerability could affect saves or accounts, or cause unintended purchases, report it through the repository's [private security-advisory form](https://github.com/wby1905/idle-slayer-smart-strategy/security/advisories/new). Include the Smart Strategy and Idle Slayer version numbers, dependency versions from the startup compatibility snapshot, and steps to reproduce the problem.
