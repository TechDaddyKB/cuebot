# Security policy

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Report it privately through GitHub: open the repository's **Security** tab and choose
[**Report a vulnerability**](https://github.com/TechDaddyKB/cuebot/security/advisories/new). Only the maintainers can
see the report. If you cannot use GitHub, email **support@techdaddydigital.com** with "Cuebot security" in the subject.

Please include:

- what is affected (the Cuebot version from Settings > About, and Windows or Linux),
- how to reproduce it, and what an attacker could do,
- any proof of concept, logs or screenshots, with your own tokens and keys removed.

We will confirm receipt within 7 days, keep you updated while we work on a fix, and credit you in the release notes
unless you would rather stay anonymous. Please give us a reasonable time to release a fix before you disclose the issue
publicly.

## Supported versions

Cuebot is in testing. Security fixes go into the next release; there are no separate maintenance branches yet. Always
use the latest release from the [releases page](https://github.com/TechDaddyKB/cuebot/releases).

## Scope

In scope: the Cuebot desktop app, its local servers (WebSocket, HTTP, UDP), the Stream Deck plugin and the webhook relay
in this repository. Problems in Twitch, YouTube, Kick, OBS or any other service Cuebot connects to should be reported to
that service.
