# /status

[![Build](https://github.com/causercode/slash-status/actions/workflows/release.yml/badge.svg)](https://github.com/causercode/slash-status/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**/status** is a privacy-conscious Windows tray app for viewing Codex activity and quota, checking authoritative OpenCode Go subscription limits, receiving quota alerts, and temporarily keeping the PC awake.

It runs entirely as the current user. Codex authentication stays with the installed Codex CLI, and /status never reads Codex credential files. An optional OpenCode Go API key is stored in Windows Credential Manager rather than in the app's settings or logs.

> /status is an independent project. It is not affiliated with or endorsed by OpenAI or the OpenCode project. Product names and logos belong to their respective owners.

## Features

- Codex account, rate-limit windows, reset times, and local activity totals
- OpenCode Go rolling five-hour, weekly, and monthly subscription usage
- One-time notifications at 25% and 5% remaining, plus quota-reset notices
- Keep-awake modes for the system or the system and display
- Optional current-user startup, with no administrator privileges required
- Keyboard, Narrator, high-contrast, text-scaling, and mixed-DPI support
- Redacted rolling diagnostic logs

## Requirements

- Windows 10 or 11 on x64 or ARM64
- PowerShell 5.1 or later for development and release scripts
- The Codex CLI is optional; without it, Codex is shown as unavailable
- An OpenCode Go API key is optional; without it, OpenCode Go is shown as not configured
- Building from source requires the .NET SDK selected by [`global.json`](global.json)

The release ZIP is self-contained and does not require a separate .NET installation.

## Install a release

Download the archive for your PC from [GitHub Releases](https://github.com/causercode/slash-status/releases):

- `TokenStatus-win-x64-<version>.zip` for most Windows PCs
- `TokenStatus-win-arm64-<version>.zip` for native Windows on ARM

Extract the ZIP to a user-writable directory such as `%LOCALAPPDATA%\Programs\TokenStatus`, then run `TokenStatus.exe`. The app has no main window; look for its icon in the Windows notification area, including **Show hidden icons**. Left-click the icon to open the status popup and right-click it for the menu.

Each release includes a JSON manifest containing SHA-256 hashes. To verify the downloaded ZIP:

```powershell
$zip = '.\TokenStatus-win-x64-1.0.0.zip'
$manifest = Get-Content '.\TokenStatus-win-x64-1.0.0.manifest.json' -Raw | ConvertFrom-Json
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -eq $manifest.zip.sha256
```

The command should print `True`. When the maintainer has configured Authenticode signing, the archive and manifest omit `-UNSIGNED`; check the extracted `TokenStatus.exe` with `Get-AuthenticodeSignature`. Treat artifacts carrying `-UNSIGNED` as unsigned software, and expect Windows to show an additional warning.

## Configure providers

### Codex

/status finds `codex.exe` in this order:

1. The explicit path saved in **Settings**
2. The standard OpenAI Codex installation below `%LOCALAPPDATA%`
3. The current user's `PATH`

The app starts `codex app-server --listen stdio://` and reuses the CLI's existing authentication. If auto-detection fails, choose the exact `codex.exe` path in Settings. Shell shims such as `.cmd`, `.bat`, and `.ps1` files are deliberately not launched.

### OpenCode Go

Open **Settings**, paste the API key issued for your OpenCode Go subscription, and select **Test** before saving. The key is stored as the per-user generic credential `TokenStatus:OpenCodeGoApiKey` in Windows Credential Manager.

The key is sent only as a bearer credential to the fixed HTTPS endpoint `https://opencode.ai/zen/go/v1/usage`; automatic HTTP redirects are disabled. The same key can authorize model inference, so treat it as a high-value secret and revoke it from the OpenCode console if the computer or Windows account is compromised.

## Build and run from source

Open PowerShell in the repository root:

```powershell
.\scripts\Invoke-DotNet.ps1 --version
.\scripts\Invoke-DotNet.ps1 restore .\TokenStatus.sln
.\scripts\Invoke-DotNet.ps1 build .\TokenStatus.sln --configuration Release --no-restore
.\scripts\Invoke-DotNet.ps1 test .\TokenStatus.sln --configuration Release --no-build
```

The first command should print `10.0.401` or a compatible later patch. The resolver checks an explicitly supplied path, `TOKENSTATUS_DOTNET`, `DOTNET_ROOT`, `PATH`, and standard Program Files installations. If the SDK is elsewhere, provide it explicitly:

```powershell
.\scripts\Invoke-DotNet.ps1 -DotNetPath 'C:\path\to\dotnet.exe' --version
```

Run the tray app in Debug mode:

```powershell
.\scripts\Invoke-DotNet.ps1 run --project .\src\TokenStatus.App\TokenStatus.App.csproj --configuration Debug
```

Only one copy can run at a time. Exit any installed copy from its tray menu before starting a development build. Choose **Exit** from the development copy's tray menu to stop it and return control to the terminal.

Additional checks:

```powershell
.\scripts\Invoke-DotNet.ps1 format .\TokenStatus.sln --verify-no-changes
.\scripts\Invoke-DotNet.ps1 list .\TokenStatus.sln package --vulnerable --include-transitive
.\scripts\Inspect-Accessibility.ps1
```

The accessibility script is read-only and reports only TokenStatus windows. Debug builds also include a popup layout overlay on `Ctrl+Shift+I`.

## VS Code

The workspace includes build, test, and publish tasks under **Terminal → Run Task**. Install the recommended C# Dev Kit extension. If an already-running VS Code process cannot find the selected SDK, close every VS Code window and launch the workspace with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Open-TokenStatus-VSCode.ps1
```

## Create a local release

Create an unsigned x64 development archive:

```powershell
.\scripts\Publish.ps1 -Version 1.0.0 -RuntimeIdentifier win-x64 -AllowDeveloperOverride
```

For ARM64, use `-RuntimeIdentifier win-arm64`. Unsigned output is clearly marked:

```text
artifacts\TokenStatus-win-x64-1.0.0-UNSIGNED.zip
artifacts\TokenStatus-win-x64-1.0.0-UNSIGNED.manifest.json
```

Tagged releases must be built from a clean commit. The GitHub Actions workflow verifies the repository, publishes both architectures, and creates the GitHub Release. When trusted Authenticode signing secrets are configured, it signs the executables; otherwise, release files are clearly marked `UNSIGNED`. See [`docs/GITHUB_SETUP.md`](docs/GITHUB_SETUP.md) for the one-time repository setup.

## Runtime data and security model

- Settings: `%LOCALAPPDATA%\TokenStatus\settings.json`
- Redacted rolling logs: `%LOCALAPPDATA%\TokenStatus\logs`
- OpenCode Go API key: Windows Credential Manager
- Start with Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- Keep-awake: `SetThreadExecutionState` on one dedicated thread

Provider failures preserve the last successful value and mark it stale rather than replacing it with zero. The registry, JSON settings, and Credential Manager are not one transaction; updates are ordered with best-effort rollback, but a process or machine failure can still require manual reconciliation.

## Contributing, security, and license

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for development guidelines. Report vulnerabilities privately as described in [`SECURITY.md`](SECURITY.md).

The source code is available under the [MIT License](LICENSE). Product names and logos remain the property of their respective owners.
