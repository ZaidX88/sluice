# Security policy

Sluice captures other programs' network traffic and runs with high rights: administrator on Windows (WinDivert), and on
Linux root or the capabilities `install.sh` gives it (`cap_net_admin`, `cap_setgid`, …). A bug in how it uses them can
matter more than an ordinary bug, so please report security problems privately.

## Reporting a problem

**Don't open a public issue, pull request or discussion.** Use GitHub's private reporting instead:
[Security → Report a vulnerability](https://github.com/ZaidX88/sluice/security/advisories/new). Only the maintainer
sees the report; the fix can be prepared in a private fork and published with an advisory.

Please include:
- the Sluice version (`Sluice.Cli --version`, or the first line of a log file) and the OS;
- how it is installed (installer or `install.sh`, an unpacked copy, a build from source);
- what an attacker needs (a local account, another program running as the user, network access…) and what they gain;
- the steps to reproduce it, without real server addresses, user names or keys.

If you are working on an issue or pull request when you find it, hold that work and say so in the report.

## What happens next

The maintainer replies in the report, agrees on the fix and on when it becomes public, and credits the reporter in the
advisory unless they would rather not be named.

## Supported versions

Sluice has no release yet. Fixes go into `main` and the next release; once there are releases, the latest one is
supported.
