# TokenStatus

TokenStatus is a portable Windows tray application for viewing local Codex quota/activity and OpenCode local activity. It runs as the current user and does not read either tool's credential files.

Important: OpenCode values are explicitly **Local activity** from the current computer's seven-day session database. They are not authoritative OpenCode Go quota. The tray panel links to the official Go usage dashboard at <https://opencode.ai/workspace>.

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
- OpenCode data comes from the fixed, read-only aggregate query run through `opencode db ... --format json`.
- CLI failures preserve the last successful value and expose a stale/error state rather than replacing it with zeroes.
- Keep-awake uses `SetThreadExecutionState` on one dedicated thread and always clears state during disable, expiry, and shutdown.
- Settings and redacted rolling logs are stored below `%LOCALAPPDATA%\TokenStatus`.
- Start with Windows uses only `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Test CLI

`tests\TokenStatus.TestCli` provides deterministic Codex app-server/OpenCode command behavior for infrastructure integration tests and can be extended with delayed, malformed, or failing responses.
