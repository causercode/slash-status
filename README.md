# /status

**/status** is a portable Windows tray application for viewing Codex quota/activity and authoritative OpenCode Go subscription limits. It runs as the current user and does not read either tool's credential files.

OpenCode Go's rolling five-hour, weekly, and monthly percentages come from the official account-wide usage API.

## Configure OpenCode Go quota

Open **Settings**, paste the API key issued for your OpenCode Go subscription, and use **Test** before saving. The key is stored as a per-user generic credential in Windows Credential Manager under `TokenStatus:OpenCodeGoApiKey`; it is never written to `settings.json`, logs, process arguments, or OpenCode's credential files. **Clear** schedules the saved credential for removal when settings are saved.

The key is sent only as a bearer credential to the hardcoded HTTPS endpoint `https://opencode.ai/zen/go/v1/usage`. Automatic HTTP redirects are disabled. The same key can authorize model inference, so it should be treated as a high-value secret and revoked from the OpenCode console if the computer or Windows account is compromised.

## Build

The solution targets .NET 10 and Windows Forms. The repository includes a `global.json` pinned to the SDK used for development.

```powershell
$env:DOTNET_ROOT = 'C:\Users\Daniel\.dotnet-tokenstatus-sdk'
$env:Path = "$env:DOTNET_ROOT;$env:Path"
dotnet restore .\TokenStatus.sln
dotnet build .\TokenStatus.sln -c Release --no-restore
dotnet test .\TokenStatus.sln -c Release --no-build
```

The application can be launched from `src\TokenStatus.App\bin\Debug\net10.0-windows\TokenStatus.exe` during development. It starts with a gray tray icon while provider data is loading.

### Debug layout inspector

Debug builds include a layout overlay for the tray popup. Open the popup and press `Ctrl+Shift+I` to toggle it. Hover over a control to see its runtime bounds, margin, and padding. Red outlines show control bounds, yellow outlines show margins, and blue outlines show padded content areas.

The inspector is excluded from Release and published builds. To use it:

```powershell
dotnet run --project .\src\TokenStatus.App\TokenStatus.App.csproj -c Debug
```

## VS Code

The workspace includes `.vscode/settings.json` and `.vscode/tasks.json`. They point C# Dev Kit and C# at the user-local SDK installed at `C:\Users\Daniel\.dotnet-tokenstatus-sdk\dotnet.exe`, and provide build, test, and publish tasks. C# Dev Kit and the C# extension are both required; reload the VS Code window after installing the SDK so the extension host and integrated terminal receive the updated environment.

If an already-running VS Code process still reports that the SDK is missing, save your work, close all VS Code windows, and run `powershell -ExecutionPolicy Bypass -File .\scripts\Open-TokenStatus-VSCode.ps1` from this folder. A full process restart is required when the original VS Code process started before the SDK was installed.

## Portable release

```powershell
dotnet publish .\src\TokenStatus.App\TokenStatus.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:DebugType=None `
  -o .\publish
Compress-Archive -Path .\publish\* -DestinationPath .\artifacts\TokenStatus-win-x64-1.0.0.zip -Force
```

The published executable needs no separately installed .NET runtime. Extract it to a user-writable directory such as `%LOCALAPPDATA%\Programs\TokenStatus`.

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
