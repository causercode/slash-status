# /status

**/status** is a portable Windows tray application for viewing Codex quota/activity and authoritative OpenCode Go subscription limits. It runs as the current user and does not read either tool's credential files.

## Build and run from source

Use these steps if you want to edit or test the source code. If you only want to use the app, skip to [Use a portable release](#use-a-portable-release); a published release does not require the .NET SDK.

Open PowerShell in the repository folder and run:

```powershell
.\scripts\Invoke-DotNet.ps1 --version
.\scripts\Invoke-DotNet.ps1 restore .\TokenStatus.sln
.\scripts\Invoke-DotNet.ps1 build .\TokenStatus.sln --configuration Release --no-restore
.\scripts\Invoke-DotNet.ps1 test .\TokenStatus.sln --configuration Release --no-build
```

The first command should print `10.0.401` or a compatible later patch. The helper checks the project-specific SDK location used on the original development computer, `DOTNET_ROOT`, `TOKENSTATUS_DOTNET`, and normal `PATH` installations. This avoids accidentally selecting a runtime-only `dotnet.exe`.

If your SDK is installed somewhere else, specify it explicitly:

```powershell
.\scripts\Invoke-DotNet.ps1 -DotNetPath 'C:\path\to\dotnet.exe' --version
```

If no compatible SDK is found, install the .NET 10 SDK selected by [`global.json`](global.json), then rerun the commands. Installing only the .NET runtime is not enough to build the solution.

### Run the tray app

Only one copy of /status can run at a time. If a published copy is already running, right-click its notification-area icon and choose **Exit** first. Then run:

```powershell
.\scripts\Invoke-DotNet.ps1 run --project .\src\TokenStatus.App\TokenStatus.App.csproj --configuration Debug
```

The app has no main window or console UI. Look for its icon in the Windows notification area, including the **Show hidden icons** overflow. Left-click the icon to open the status popup; right-click it to open the menu. Choose **Exit** from that menu to stop the app and return control to the terminal.

### Optional developer checks

These are also run by the release pipeline:

```powershell
.\scripts\Invoke-DotNet.ps1 format .\TokenStatus.sln --verify-no-changes
.\scripts\Invoke-DotNet.ps1 list .\TokenStatus.sln package --vulnerable --include-transitive
```

### Debug layout inspector

Debug builds include a layout overlay for the tray popup. Open the popup and press `Ctrl+Shift+I` to toggle it. Hover over a control to see its runtime bounds, margin, and padding. Red outlines show control bounds, yellow outlines show margins, and blue outlines show padded content areas. The inspector is not included in Release builds.

## Requirements and optional providers

- Windows 10 or 11 x64 with PowerShell.
- Building from source requires the .NET 10 SDK selected by `global.json`.
- A portable published release is self-contained and does not require .NET.
- The Codex CLI is optional. Without it, the app still runs and reports Codex as unavailable.
- An OpenCode Go API key is optional. Without it, the OpenCode card explains that configuration is required.

## Configure OpenCode Go quota

Open **Settings**, paste the API key issued for your OpenCode Go subscription, and use **Test** before saving. OpenCode Go's rolling five-hour, weekly, and monthly percentages come from the official account-wide usage API.

The key is stored as a per-user generic credential in Windows Credential Manager under `TokenStatus:OpenCodeGoApiKey`; it is never written to `settings.json`, logs, process arguments, or OpenCode's credential files. **Clear** schedules the saved credential for removal when settings are saved.

The key is sent only as a bearer credential to the hardcoded HTTPS endpoint `https://opencode.ai/zen/go/v1/usage`. Automatic HTTP redirects are disabled. The same key can authorize model inference, so it should be treated as a high-value secret and revoked from the OpenCode console if the computer or Windows account is compromised.

## VS Code

The workspace includes build, test, and publish tasks under **Terminal → Run Task**. The build and test tasks use the same SDK resolver as the PowerShell commands above.

C# Dev Kit and the C# extension are both required. If an already-running VS Code process cannot find the SDK, save your work, close every VS Code window, and launch this workspace with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Open-TokenStatus-VSCode.ps1
```

The launcher resolves the compatible SDK before starting VS Code and adds that SDK to the new process's environment.

## Use a portable release

A portable release is a self-contained, single-file `win-x64` build. Extract its ZIP into a user-writable directory such as `%LOCALAPPDATA%\Programs\TokenStatus`, then run `TokenStatus.exe`. No .NET installation is needed.

To create an unsigned local development release from source:

```powershell
.\scripts\Publish.ps1 -Version 1.0.0 -AllowDeveloperOverride
```

The script runs restore, build, tests, formatting, and the package vulnerability audit before publishing. Output is clearly marked unsigned:

```text
artifacts\TokenStatus-win-x64-1.0.0-UNSIGNED.zip
artifacts\TokenStatus-win-x64-1.0.0-UNSIGNED.manifest.json
```

To force a specific SDK:

```powershell
.\scripts\Publish.ps1 -Version 1.0.0 -AllowDeveloperOverride -DotNetPath 'C:\path\to\dotnet.exe'
```

Official releases must run from a clean tag commit and require a trusted Authenticode certificate. Provision the certificate in the current-user `Cert:\CurrentUser\My` store (or import it in CI), then pass its thumbprint:

```powershell
$version = '1.0.0'
$tag = "v$version"
$commit = (git rev-parse HEAD)
.\scripts\Publish.ps1 `
  -Version $version `
  -ReleaseTag $tag `
  -ReleaseCommit $commit `
  -RequireSigning `
  -CertificateThumbprint '<trusted-certificate-thumbprint>'
```

The tag must point at the commit being published, and the version must match the tag when it uses the `v<version>` form. The script validates the selected SDK against `global.json`, runs restore/build/test/format/vulnerability checks, publishes to a clean staging directory, signs before packaging, and writes the ZIP plus a SHA-256 release manifest under `artifacts`. Do not create or use a self-signed certificate for an official release.

The GitHub Actions workflow in `.github\workflows\release.yml` performs the same verification and signed publish for `v*` tags. Configure `TOKENSTATUS_SIGNING_CERTIFICATE_BASE64`, `TOKENSTATUS_SIGNING_CERTIFICATE_PASSWORD`, and `TOKENSTATUS_SIGNING_CERTIFICATE_THUMBPRINT`; `TOKENSTATUS_TIMESTAMP_SERVER` is optional. The Windows registry, JSON settings, and Credential Manager are not one transaction; the application uses ordered updates and best-effort rollback, but a process or machine failure can still require manual reconciliation.


## Runtime behavior

- Codex data comes from the documented `codex app-server --listen stdio://` JSONL protocol and reuses the CLI's existing authentication.
- OpenCode Go quota comes from the official account-wide usage endpoint using the API key explicitly supplied in Settings.
- CLI failures preserve the last successful value and expose a stale/error state rather than replacing it with zeroes.
- Keep-awake uses `SetThreadExecutionState` on one dedicated thread and always clears state during disable, expiry, and shutdown.
- Quota notifications use Windows notification-area alerts. They fire once when a Codex or OpenCode Go window crosses 25% or 5% remaining, and again when the window resets. They can be disabled in Settings.
- Debug builds add **Test notification (Debug)** to the tray menu, with previews for each notification type.
- Settings and redacted rolling logs are stored below `%LOCALAPPDATA%\TokenStatus`.
- Start with Windows uses only `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Test CLI

`tests\TokenStatus.TestCli` provides deterministic Codex app-server behavior for infrastructure integration tests and can be extended with delayed, malformed, or failing responses.
