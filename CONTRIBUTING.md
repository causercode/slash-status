# Contributing to /status

Thanks for helping improve /status. Bug reports, accessibility feedback, and focused pull requests are welcome.

## Development requirements

- Windows 10 or 11
- PowerShell 5.1 or later
- The .NET SDK selected by [`global.json`](global.json)
- For Codex integration testing, an installed and authenticated Codex CLI

The Codex CLI and an OpenCode Go API key are optional for building and running the automated test suite.

## Build and test

Open PowerShell in the repository root:

```powershell
.\scripts\Invoke-DotNet.ps1 restore .\TokenStatus.sln
.\scripts\Invoke-DotNet.ps1 build .\TokenStatus.sln --configuration Release --no-restore
.\scripts\Invoke-DotNet.ps1 test .\TokenStatus.sln --configuration Release --no-build
.\scripts\Invoke-DotNet.ps1 format .\TokenStatus.sln --verify-no-changes
.\scripts\Invoke-DotNet.ps1 list .\TokenStatus.sln package --vulnerable --include-transitive
```

If the resolver cannot find the SDK, pass `-DotNetPath 'C:\path\to\dotnet.exe'` before the dotnet arguments.

## Pull requests

- Keep changes focused and include tests for behavior changes.
- Preserve the security boundaries: never read provider credential files, log credentials, or send credentials anywhere except their documented provider endpoint.
- Keep the application usable with keyboard navigation, Windows Narrator, high-contrast themes, increased text size, and mixed-DPI displays.
- Run the complete command sequence above before opening a pull request.
- Do not commit generated `bin`, `obj`, `artifacts`, or `publish` directories.

## Manual UI checks

Run the Debug build, then inspect the tray popup and Settings window at 100% and 200% display scaling. The debug popup includes a layout inspector on `Ctrl+Shift+I`. A read-only UI Automation tree can be printed with:

```powershell
.\scripts\Inspect-Accessibility.ps1
```

## Reporting security issues

Please follow [`SECURITY.md`](SECURITY.md) rather than filing a public issue for a vulnerability.
