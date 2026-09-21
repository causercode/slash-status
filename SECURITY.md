# Security policy

## Supported versions

Security fixes are provided for the latest published release. Until the first release is published, only the current default branch is supported.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability or credential exposure. Use the repository's **Security** tab to report it privately through a GitHub private vulnerability report.

Include the affected version or commit, Windows version and architecture, reproduction steps, and the expected security impact. Do not include real Codex credentials, OpenCode Go API keys, or unredacted logs.

If private vulnerability reporting has not yet been enabled for the repository, contact the repository owner privately and ask for a secure reporting channel.

## Credential boundaries

/status delegates Codex authentication to the installed Codex CLI. It does not read Codex credential files. OpenCode Go API keys are stored in Windows Credential Manager and are sent only to the fixed OpenCode usage endpoint over HTTPS. Logs are best-effort redacted, but users should still review diagnostic files before sharing them.
