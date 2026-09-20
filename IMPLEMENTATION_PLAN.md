# TokenStatus Base Application Implementation Plan

Status: implementation handoff  
Target: Windows 10/11 x64  
Date: 2026-09-19

## 1. Objective

Build a small, local-only Windows tray application named **TokenStatus** that:

1. Shows authoritative Codex account quota windows and token activity by talking to the installed Codex CLI.
2. Shows authoritative OpenCode Go rolling five-hour, weekly, and monthly limits through the official usage API.
3. Provides a one-click link to the OpenCode Go web usage dashboard.
4. Provides user-controlled Windows keep-awake modes with optional durations.
5. Runs entirely as the current user, requires no administrator privileges, and can be copied between home and work Windows computers.

This repository is currently empty and is not yet a Git repository.

## 2. Base Scope

### Included

- Windows notification-area (system tray) icon.
- Short hover tooltip.
- Click-to-open status panel.
- Right-click tray menu.
- Codex authentication state, plan, rate-limit windows, reset times, and token activity.
- OpenCode Go five-hour, weekly, and monthly used percentages and reset times.
- OpenCode Go dashboard shortcut.
- Manual refresh and scheduled background refresh.
- Last-known-good data and visible stale/error states.
- Keep system awake.
- Keep system and display awake.
- Timed keep-awake sessions.
- Optional start-at-login for the current user.
- Portable, self-contained x64 release ZIP.
- Local settings and redacted rolling logs.
- Automated tests for parsing, orchestration, and keep-awake state.

### Explicitly deferred

- Microsoft Teams/Graph presence or “stay green.”
- Simulated mouse movement or keyboard input.
- Browser automation or scraping the OpenCode Go console.
- Reading OpenCode browser cookies, API-key files, or credential tables. The user supplies the Go key explicitly and it is stored in Windows Credential Manager.
- Cross-device synchronization.
- Cloud backend or telemetry.
- Auto-update.
- Installer/MSIX/MSI.
- Historical charts beyond the values already returned by the CLIs.

## 3. Important Product Semantics

### Codex data is authoritative

Use the documented Codex app-server JSONL protocol. The local app-server reuses the existing Codex CLI authentication and supplies account-side quota information.

Display:

- Authentication state.
- ChatGPT plan when available.
- Every quota bucket returned by `rateLimitsByLimitId`, falling back to `rateLimits` when the multi-bucket view is absent.
- Primary and secondary windows, including `usedPercent`, `windowDurationMins`, and `resetsAt`.
- `ordinaryUsageAllowed` and `rateLimitReachedType`.
- Optional credit balance and reset-credit count when present.
- Lifetime tokens, today's tokens, recent daily tokens, and streak information when returned.

### OpenCode quota is authoritative

OpenCode Go exposes account-wide rolling five-hour, weekly, and monthly usage at `GET https://opencode.ai/zen/go/v1/usage`. Authenticate with the Go API key as a bearer credential. Display the returned `percent`, `status`, and `resetsAt` fields without deriving quota from local spending.

The panel must contain an **Open Go Usage Dashboard** action pointing to:

`https://opencode.ai/workspace`

Remaining percentage is `100 - percent`, clamped for display only. Preserve the exact server percentage in the model.

## 4. Technology Choices

Use:

- C# and .NET 10 LTS.
- Windows Forms (`net10.0-windows`).
- `NotifyIcon` for the tray integration.
- Built-in `System.Text.Json` for JSON.
- Built-in `HttpClient` for the fixed OpenCode Go usage endpoint, with redirects disabled and normal TLS validation.
- `xUnit` for automated tests.
- P/Invoke for the Windows execution-state API.

Avoid:

- Electron, WebView, Node, or Python runtimes.
- Third-party UI frameworks.
- Shell command construction from strings.
- Trimming the published application; WinForms and reflection-heavy serializers are safer with trimming disabled.

The published app must be self-contained, so the destination computer does not need a separately installed .NET runtime.

## 5. Proposed Solution Structure

```text
TokenStatus.sln
Directory.Build.props
README.md
src/
  TokenStatus.App/
    Program.cs
    ApplicationContext.cs
    UI/
      StatusPopupForm.cs
      SettingsForm.cs
      StatusViewModel.cs
      TrayIconRenderer.cs
    Startup/
      CurrentUserStartupManager.cs
    Resources/
  TokenStatus.Core/
    Models/
      AppSnapshot.cs
      ProviderHealth.cs
      CodexSnapshot.cs
      OpenCodeGoQuota.cs
      RateLimitBucket.cs
      RateLimitWindow.cs
      AwakeState.cs
    Services/
      RefreshCoordinator.cs
      SnapshotStore.cs
      OverallHealthCalculator.cs
    Abstractions/
      ICodexUsageClient.cs
      IOpenCodeGoQuotaClient.cs
      IOpenCodeGoCredentialStore.cs
      IAwakeController.cs
      ISettingsStore.cs
      IClock.cs
  TokenStatus.Infrastructure/
    Cli/
      ExecutableResolver.cs
      ProcessRunner.cs
    Codex/
      CodexAppServerClient.cs
      JsonLineRpcConnection.cs
      CodexJsonModels.cs
    OpenCode/
      OpenCodeGoQuotaClient.cs
      WindowsOpenCodeGoCredentialStore.cs
    Awake/
      WindowsAwakeController.cs
      ExecutionStateNative.cs
    Settings/
      JsonSettingsStore.cs
      AppSettings.cs
    Logging/
      RedactingFileLog.cs
tests/
  TokenStatus.Core.Tests/
  TokenStatus.Infrastructure.Tests/
  TokenStatus.TestCli/
```

Keep the UI thin. Protocol parsing, health calculations, timers, and process management must be testable without displaying WinForms controls.

## 6. Core Models and Contracts

Use immutable records wherever practical.

```csharp
public enum ProviderHealth
{
    Loading,
    Healthy,
    Stale,
    NotInstalled,
    NotAuthenticated,
    Unsupported,
    Error
}

public sealed record RateLimitWindow(
    int UsedPercent,
    TimeSpan? Duration,
    DateTimeOffset? ResetsAt);

public sealed record RateLimitBucket(
    string Id,
    string? DisplayName,
    string? PlanType,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    bool? OrdinaryUsageAllowed,
    string? ReachedType);

public sealed record ProviderResult<T>(
    ProviderHealth Health,
    T? Value,
    DateTimeOffset LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? UserFacingError,
    string? DiagnosticCode);
```

Suggested interfaces:

```csharp
public interface ICodexUsageClient : IAsyncDisposable
{
    Task<CodexAccountInfo> GetAccountAsync(CancellationToken cancellationToken);
    Task<CodexRateLimits> GetRateLimitsAsync(CancellationToken cancellationToken);
    Task<CodexTokenUsage> GetTokenUsageAsync(CancellationToken cancellationToken);
}

public interface IOpenCodeGoQuotaClient
{
    Task<OpenCodeGoQuota> GetQuotaAsync(CancellationToken cancellationToken);
}

public interface IAwakeController : IDisposable
{
    AwakeState Current { get; }
    event EventHandler<AwakeState>? Changed;
    void Start(AwakeMode mode, TimeSpan? duration);
    void Stop();
}
```

Do not leak protocol-specific JSON types into `TokenStatus.Core`.

## 7. Codex Integration

### 7.1 Process lifecycle

Resolve the Codex executable in this order:

1. User-configured absolute path.
2. `codex.exe` found in `PATH`.
3. `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe`.

Start one long-lived process for the lifetime of the application:

```text
codex app-server --listen stdio://
```

Use `ProcessStartInfo` with:

- `UseShellExecute = false`
- redirected stdin/stdout/stderr
- `CreateNoWindow = true`
- arguments added through `ArgumentList`

Drain stderr continuously with bounded/redacted logging so the child process cannot deadlock on a full error pipe.

### 7.2 Initialization handshake

Write one JSON object per line. Do not include `jsonrpc`.

First send:

```json
{
  "method": "initialize",
  "id": 0,
  "params": {
    "clientInfo": {
      "name": "token_status",
      "title": "TokenStatus",
      "version": "<application version>"
    },
    "capabilities": {
      "optOutNotificationMethods": [
        "thread/started",
        "item/agentMessage/delta"
      ]
    }
  }
}
```

Wait for the successful response with `id: 0`; only then send:

```json
{"method":"initialized","params":{}}
```

Do not use arbitrary sleeps for the protocol handshake.

### 7.3 Requests

Use monotonically increasing numeric request IDs and a concurrent map of request ID to `TaskCompletionSource<JsonElement>`.

Requests:

```json
{"method":"account/read","id":1,"params":{"refreshToken":false}}
{"method":"account/rateLimits/read","id":2}
{"method":"account/usage/read","id":3}
```

Rules:

- Default request timeout: 15 seconds.
- Parse responses by `id`; ignore unrelated notifications safely.
- Treat a response containing `error` as a failed request.
- Never log raw response lines. They may contain account identifiers or other metadata.
- Discard account IDs after validating that the response belongs to the current account.
- If stdout closes, JSON repeatedly fails to parse, or the process exits, fail pending requests and restart with exponential backoff.
- On application shutdown, close stdin, allow two seconds for exit, then kill the child process tree if necessary.

### 7.4 Parsing rules

- Prefer `rateLimitsByLimitId` when non-null and non-empty.
- Otherwise expose the singular `rateLimits` object as one bucket.
- Do not show the same bucket twice.
- Treat all documented nullable fields as genuinely optional.
- Clamp display percentages to `0..100`, but retain the original value in diagnostics if the server returns an out-of-range value.
- Convert `resetsAt` with `DateTimeOffset.FromUnixTimeSeconds` and display it in local time.
- Convert `windowDurationMins` to `TimeSpan`.
- A missing daily bucket for today means zero only when `dailyUsageBuckets` was successfully returned as a non-null collection. A null collection means “not provided.”
- Do not infer that usage is allowed from percentages. Respect `ordinaryUsageAllowed` when supplied.

### 7.5 Codex refresh schedule

- Account state: at startup and after authentication-related failures.
- Rate limits: at startup, every 2 minutes, and on manual refresh.
- Token usage: at startup, every 10 minutes, and on manual refresh.
- Coalesce overlapping refreshes; never run multiple requests of the same type concurrently.
- Add up to 10% random jitter to scheduled refreshes.

Keep rate-limit and token-activity last-success timestamps separate so one failing endpoint does not erase good data from the other.

## 8. OpenCode Integration

### 8.1 OpenCode Go quota API

Send `GET https://opencode.ai/zen/go/v1/usage` with `Authorization: Bearer <key>`. The key must be supplied explicitly in Settings and stored as a per-user generic credential named `TokenStatus:OpenCodeGoApiKey` in Windows Credential Manager. Never place it in JSON settings, logs, URLs, or command-line arguments.

Accept at most 64 KiB of JSON with bounded depth. Treat `401` as invalid/expired credentials, `403` as no Go entitlement, and `429` as a transient stale state. Disable redirects so the bearer credential cannot be forwarded to another host.

### 8.2 OpenCode refresh schedule

- At startup.
- Every 2 minutes.
- On manual refresh.
- HTTP timeout: 10 seconds.
- No overlapping OpenCode quota requests.

### 8.3 Go dashboard action

Open the hardcoded dashboard URL through the default browser using shell execution. Do not append keys, account IDs, or query parameters.

## 9. Keep-Awake Implementation

P/Invoke `SetThreadExecutionState` from `kernel32.dll`.

```csharp
[Flags]
internal enum ExecutionState : uint
{
    SystemRequired = 0x00000001,
    DisplayRequired = 0x00000002,
    Continuous = 0x80000000
}
```

Modes:

| Mode | Flags |
|---|---|
| Off | `Continuous` |
| Keep system awake | `Continuous | SystemRequired` |
| Keep system and display awake | `Continuous | SystemRequired | DisplayRequired` |

Important: execution state is associated with the calling thread. Implement `WindowsAwakeController` with one dedicated, long-lived background thread. That thread must set, update, and clear the execution state. Do not make a one-time `ES_CONTINUOUS` call from an arbitrary thread-pool thread.

The controller should accept commands through a thread-safe queue or synchronization primitive. It must:

- Track mode, start time, optional expiry, and remaining duration.
- Automatically call the Off flags when the duration expires.
- Call the Off flags from the same dedicated thread during disposal.
- Reassert the requested state after Windows resume if necessary.
- Never use `ES_AWAYMODE_REQUIRED`.
- Never attempt to block explicit sleep, shutdown, lid closure, or workstation locking.

Duration choices:

- 30 minutes
- 1 hour
- 2 hours
- 4 hours
- Until turned off

Do not restore an active keep-awake session automatically after an application or computer restart.

## 10. Refresh Coordinator and State Management

Use a `RefreshCoordinator` owned by the application context.

Requirements:

- Independent cancellation-aware loops for Codex limits, Codex usage, and OpenCode usage.
- `PeriodicTimer` or equivalent async timing; no WinForms UI timer for I/O.
- `SemaphoreSlim` per refresh type to prevent overlap.
- Manual refresh coalesces with an active scheduled refresh.
- Immutable snapshots written to a thread-safe `SnapshotStore`.
- Snapshot-changed event marshalled to the WinForms UI thread with `SynchronizationContext.Post` or `Control.BeginInvoke`.
- Application shutdown cancels loops before disposing provider clients.

Staleness rules:

- Fresh: last success is less than twice the configured interval old.
- Stale: older than twice the configured interval but a prior value exists.
- Error/unavailable: no successful value exists.
- Never replace a last-known-good value with zeros after an error.

## 11. Tray UX

### 11.1 Tray icon

Use four visual states:

- Green: data fresh and no Codex quota at or above 75% used.
- Amber: a quota is at least 75% used, or a provider is stale/degraded.
- Red: a quota is at least 90% used, `ordinaryUsageAllowed == false`, or a reached type is present.
- Gray: initial loading or no providers available.

Quota severity takes precedence over provider health. Keep-awake state should be shown in text/menu state rather than changing the health color.

Ensure icon handles are cloned/disposed correctly if icons are generated at runtime. Prefer embedded `.ico` resources for predictable lifetime management.

### 11.2 Tooltip

Keep `NotifyIcon.Text` at or below 63 characters for compatibility. Example:

```text
Codex 7%/1% | OC $2.91/7d | Awake 58m
```

Use `--` for unavailable values. Do not put error details in the tooltip.

### 11.3 Left-click popup

Create a borderless, non-taskbar WinForms panel approximately 420 logical pixels wide. Position it within the working area of the monitor containing the cursor, near the taskbar/cursor, and correct for per-monitor DPI.

Suggested layout:

```text
TokenStatus                           Refresh

Codex                                      Healthy
  5-hour              7% used          resets 4:24 PM
  Weekly              1% used          resets Friday
  Today               5.9M tokens
  Lifetime            2.4B tokens

OpenCode                                    Healthy
  5-hour              23% used          resets 4:24 PM
  Weekly              41% used          resets Friday
  Monthly             19% used          resets Oct 5

Keep awake                                  Off
  [System] [System + display] [Duration]

Updated 35 seconds ago             Settings   Exit
```

UI rules:

- Show both used and remaining Codex percentages in accessible text/tooltips if progress bars are used.
- Display reset timestamps in local time and provide relative text.
- Null/missing fields display as “Not provided,” not zero.
- Escape closes the popup.
- Deactivate hides the popup unless a child dialog is open.
- Opening the popup must never block on a CLI call; show current snapshot and refresh asynchronously.

### 11.4 Right-click menu

Include:

- Open TokenStatus
- Refresh now
- Keep awake
  - Off
  - System: 30 min / 1 hr / 2 hr / 4 hr / until off
  - System + display: same durations
- Open Go Usage Dashboard
- Start with Windows (checked state)
- Settings
- Exit

### 11.5 Settings dialog

Base settings:

- Codex executable path with Auto-detect/Test actions.
- Masked OpenCode Go API-key field with Test and Clear actions. Never reveal a saved key back into the field.
- Refresh intervals with safe minimums: 30 seconds for rate limits/local stats and 5 minutes for token-history activity.
- Start with Windows.
- Open log folder.

Validate settings before saving. Use defaults when the settings file is missing or corrupt, and preserve the corrupt file with a timestamped suffix for diagnosis.

## 12. Application Lifecycle

Use a custom WinForms `ApplicationContext`; there should be no main window shown at startup.

Startup order:

1. Acquire named mutex `Local\TokenStatus`.
2. If another instance exists, signal it if practical or exit cleanly.
3. Initialize local directories and redacted logging.
4. Load settings.
5. Create tray icon immediately in Loading state.
6. Start the awake controller in Off mode.
7. Resolve CLIs and start refresh loops.
8. Update UI asynchronously as results arrive.

Shutdown order:

1. Disable/hide tray icon.
2. Stop keep-awake on its owning thread.
3. Cancel refresh loops.
4. Dispose Codex app-server connection.
5. Terminate any remaining owned child processes.
6. Flush logs and release the mutex.

Handle unhandled UI-thread and background-task exceptions by writing a redacted diagnostic and exiting cleanly. Do not leave keep-awake enabled or orphan child processes.

## 13. Settings, Files, and Logging

Use:

```text
%LOCALAPPDATA%\TokenStatus\settings.json
%LOCALAPPDATA%\TokenStatus\logs\token-status.log
```

Suggested settings schema:

```json
{
  "schemaVersion": 1,
  "codexExecutablePath": null,
  "codexRateLimitRefreshSeconds": 120,
  "codexUsageRefreshSeconds": 600,
  "openCodeRefreshSeconds": 120,
  "startWithWindows": false
}
```

Logging requirements:

- Maximum approximately 1 MiB per file.
- Keep at most three old files.
- No telemetry.
- Never log authentication tokens, environment-variable contents, raw Codex JSON, OpenCode credential data, account IDs, email addresses, or full session records.
- Log provider, operation, duration, outcome, CLI version, exit code, and stable diagnostic code.
- Cap and sanitize stderr excerpts.

## 14. Start With Windows

Implement opt-in per-user startup through:

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

Value name: `TokenStatus`  
Value: quoted absolute path to the executable.

This requires no administrator rights. The UI must accurately reflect whether the current executable path is registered. Removing the setting must only remove the `TokenStatus` value created by this application.

Do not write to HKLM, install a service, create an elevated scheduled task, or request elevation.

## 15. Security Requirements

- Run only as the current user.
- Never request administrator privileges in the application manifest.
- Never read Codex or OpenCode credential files directly.
- Store the explicitly supplied OpenCode Go key only in Windows Credential Manager for the current user.
- Send the key only to the hardcoded `https://opencode.ai/zen/go/v1/usage` endpoint, with redirects disabled and default TLS certificate validation enabled.
- Never query OpenCode `account`, `credential`, or authentication-related tables.
- Use only a hardcoded read-only aggregate query against `session`.
- Do not accept arbitrary SQL from settings or UI.
- Launch CLIs with `UseShellExecute = false` and `ArgumentList`.
- Do not pass secrets on command lines.
- Do not inherit or print the full environment in diagnostics.
- Kill only child processes created and tracked by this application.
- The dashboard URL must remain hardcoded HTTPS.
- Treat all CLI output as untrusted input: enforce line/response size limits and JSON depth limits.

Suggested maximums:

- JSONL line: 4 MiB.
- OpenCode stdout: 2 MiB.
- stderr retained for diagnostics: 8 KiB per operation.

## 16. Packaging

Release command:

```powershell
dotnet publish .\src\TokenStatus.App\TokenStatus.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:DebugType=None
```

Package the publish output as:

```text
TokenStatus-win-x64-<version>.zip
```

Recommended installation location:

```text
%LOCALAPPDATA%\Programs\TokenStatus\TokenStatus.exe
```

Do not require an installer for the base release. Add code signing later if the executable will be used on managed work computers; no-admin operation does not bypass SmartScreen or employer application-control policy.

## 17. Testing Plan

### 17.1 Unit tests

Codex parsing:

- Singular rate-limit response.
- Multiple `rateLimitsByLimitId` buckets.
- Primary and secondary windows.
- Missing/null optional fields.
- `ordinaryUsageAllowed == false` with low percentages.
- Reset-credit data present and absent.
- Daily usage present, empty, and null.
- Error response and malformed JSON.
- Unix timestamp conversion.

JSON-RPC connection:

- Waits for initialize response before sending `initialized`.
- Routes responses to matching request IDs.
- Ignores notifications.
- Times out requests.
- Fails pending requests on process exit.
- Does not log raw payloads.

OpenCode:

- Parses the one-row aggregate JSON.
- Accepts integer and floating-point cost values.
- Rejects missing fields and unexpected row counts.
- Handles missing CLI, timeout, nonzero exit, malformed JSON, and missing `session` table.
- SQL contains no user-provided values.

Refresh/health:

- Coalesces concurrent refreshes.
- Preserves last-known-good data after failure.
- Transitions from fresh to stale correctly.
- Applies quota thresholds and `ordinaryUsageAllowed` precedence.

Awake controller:

- Sends correct flags for each mode.
- Uses the same logical native-call owner for set and clear.
- Expires timed sessions.
- Stop/dispose always clears the request.
- Repeated Start changes mode/duration without leaking threads.

UI helpers:

- Tooltip never exceeds 63 characters.
- Formatting handles nulls and large token counts.
- Popup placement remains inside each monitor working area.

### 17.2 Test CLI

Create a small `TokenStatus.TestCli` console project that can emulate:

- Codex app-server initialization and account responses.
- Delayed responses.
- Notifications between responses.
- Malformed JSON.
- Early process exit.
- OpenCode Go usage JSON success/failure.

Use it for deterministic infrastructure integration tests instead of requiring real accounts in CI.

### 17.3 Manual acceptance tests

On Windows 10/11:

1. Start from Explorer: no console window and no elevation prompt.
2. Tray icon appears while providers are still loading.
3. Left click opens a correctly positioned panel.
4. Codex signed-in state and quota values match `/status` or `/usage` closely.
5. OpenCode Go quota values match the web dashboard.
6. Disconnect or rename each CLI and verify clear degraded states.
7. Expire/interrupt authentication and verify no secrets appear in logs.
8. Enable each awake mode and confirm Windows does not automatically sleep as applicable.
9. Disable/exit and confirm normal power-idle behavior returns.
10. Test timed expiry and resume-from-sleep behavior.
11. Enable Start with Windows, sign out/in, and verify one tray instance.
12. Move the executable, update startup setting, and verify the old registration is removed.
13. Open the Go dashboard and verify no credentials appear in the URL.
14. Run for several hours and confirm no orphan CLI processes or unbounded memory/log growth.

## 18. Implementation Sequence

### Milestone 1: Shell and vertical slice

- Create solution/projects and common build settings.
- Add application context, single-instance mutex, tray icon, basic popup, exit command.
- Add settings paths and redacted logging.
- Add a fake snapshot provider so the complete UI can be exercised.

Exit criteria: portable debug build starts without a console window and displays fake data in the tray panel.

### Milestone 2: Codex provider

- Implement resolver, JSONL transport, handshake, request routing, parsing, restart/backoff, and tests.
- Wire real rate limits/account/token activity into snapshots and UI.

Exit criteria: the installed Codex CLI produces live quota and activity data without reading credential files.

### Milestone 3: OpenCode provider

- Implement the official Go usage client, Windows Credential Manager storage, parser, and tests.
- Add quota windows and the Go dashboard action.

Exit criteria: five-hour, weekly, and monthly values match the account-wide dashboard.

### Milestone 4: Keep awake

- Implement dedicated-thread controller, duration expiry, menu/UI state, shutdown cleanup, and tests.

Exit criteria: both awake modes work without elevation and are reliably cleared on disable/exit.

### Milestone 5: Hardening and release

- Add refresh jitter/backoff/staleness.
- Add settings UI and current-user startup.
- Complete accessibility, DPI, multiple-monitor, logging, and lifecycle tests.
- Publish self-contained x64 ZIP and test on a clean standard-user Windows profile.

Exit criteria: all acceptance tests pass and the application runs without administrator privileges or external runtime installation.

## 19. Definition of Done

The base application is done when:

- It runs as a single tray application with no console window and no elevation.
- It can be installed by extracting a ZIP into a user-writable folder.
- Codex quotas and token activity are read from the supported app-server protocol.
- OpenCode Go quota displays authoritative account-wide five-hour, weekly, and monthly values when a key is configured.
- The OpenCode Go dashboard opens with one click.
- Provider failures never erase good prior data or freeze the UI.
- Keep-awake modes work, expire correctly, and always clear on exit.
- Start-with-Windows is current-user-only and opt-in.
- No credential files, cookies, private endpoints, mouse simulation, or telemetry are used.
- Automated tests cover the main parsers, state transitions, process failures, and awake lifecycle.
- A self-contained `win-x64` ZIP has been smoke-tested on both a home-like and managed-standard-user Windows profile, subject to workplace application policy.

## 20. Reference Documentation

- Codex app-server protocol: <https://developers.openai.com/codex/app-server>
- Codex developer commands and `/usage`: <https://developers.openai.com/codex/developer-commands?surface=cli>
- OpenCode Go limits and console: <https://dev.opencode.ai/docs/go/>
- OpenCode Go usage endpoint implementation: <https://github.com/anomalyco/opencode/blob/dev/packages/console/app/src/routes/zen/go/v1/usage.ts>
- Windows Credential Manager `CredWrite`: <https://learn.microsoft.com/windows/win32/api/wincred/nf-wincred-credwritew>
- Windows `SetThreadExecutionState`: <https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate>
