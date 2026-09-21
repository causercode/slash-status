# TokenStatus P1 Remediation Plan for Luna

Status: implementation-ready handoff  
Prepared: 2026-09-20  
Target: `master` at or after `063c978`  
Scope: all P1 findings from the full application review

## 1. Objective

Resolve the eight P1 findings without changing TokenStatus provider semantics or adding unrelated product features. The completed work must preserve:

- The current-user, no-elevation execution model.
- The fixed OpenCode Go HTTPS endpoint and Credential Manager storage.
- The Codex app-server JSONL protocol.
- Last-known-good provider values on refresh failure.
- The existing WinForms visual design unless this plan explicitly calls for a UI change.

Luna should implement this plan in the phases below, keep commits narrowly scoped, and run the full verification matrix after every phase.

## 2. Baseline and Working Rules

Before editing:

1. Run `git status --short` and confirm there are no unexpected changes. This handoff file may remain untracked until the user commits it; Luna must not overwrite unrelated user work.
2. Record the starting commit with `git rev-parse HEAD`.
3. Run the baseline commands below and retain the results in the implementation summary.
4. Do not modify or delete the user's `%LOCALAPPDATA%\TokenStatus` data or Credential Manager entry during automated tests.
5. Use fakes and temporary directories for settings, credentials, logs, registry-like behavior, and process output.
6. Do not add a third-party runtime dependency unless a standard-library implementation is demonstrably impractical.

Baseline commands:

```powershell
$sdk = 'C:\Users\Daniel\.dotnet-tokenstatus-sdk\dotnet.exe'
& $sdk build .\TokenStatus.sln --configuration Release
& $sdk test .\TokenStatus.sln --configuration Release --no-build
& $sdk format .\TokenStatus.sln --verify-no-changes
& $sdk list .\TokenStatus.sln package --vulnerable --include-transitive
```

Expected baseline: clean build, 27 passing tests, clean formatting, and no known vulnerable packages.

## 3. Phase A: Runtime and Security Hardening

### P1.1 — Make Codex executable resolution trustworthy

Problem: automatic resolution currently selects the first `codex.exe` on `PATH` before the known OpenAI installation path. The selected path is then executed directly.

Primary files:

- `src/TokenStatus.Infrastructure/Cli/ExecutableResolver.cs`
- `src/TokenStatus.Infrastructure/Codex/CodexAppServerClient.cs`
- `src/TokenStatus.App/UI/SettingsForm.cs`
- New or existing infrastructure tests

Implementation:

1. Introduce a resolution result that contains both the absolute path and its provenance, for example `Configured`, `KnownInstall`, or `Path`.
2. Resolve candidates in this order:
   - A valid, fully qualified user-configured `.exe` path.
   - The known `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe` installation.
   - A `codex.exe` found on `PATH` as a compatibility fallback.
3. Continue rejecting `.cmd`, `.bat`, and `.ps1` shims. Reject directories, relative paths, and non-`.exe` configured paths.
4. Canonicalize the selected path with `Path.GetFullPath` before it reaches `ProcessStartInfo`.
5. Show the automatically resolved absolute path and provenance in Settings. A PATH fallback must be visibly identified; do not silently present it as the known OpenAI installation.
6. Do not hard-fail unsigned user-configured executables in this iteration. Signature enforcement would break legitimate alternate installations. Structure the result so publisher verification can be added later.

Tests:

- Configured valid executable wins over all automatic candidates.
- Known installation wins over PATH.
- PATH is used only when configured and known-install candidates are absent.
- Relative paths and shell shims are rejected.
- Malformed PATH entries do not prevent later valid entries from resolving.
- The process start request receives the exact canonical path and an argument list, never a constructed shell command.

Acceptance criteria:

- A writable PATH entry cannot shadow an existing known OpenAI installation.
- The user can determine exactly which executable TokenStatus will launch.
- Existing custom-path users remain supported.

### P1.2 — Surface `SetThreadExecutionState` failures

Problem: the native API returns success/failure, but the controller publishes an active keep-awake state even when the call failed.

Primary files:

- `src/TokenStatus.Core/Models/AwakeState.cs`
- `src/TokenStatus.Infrastructure/Awake/WindowsAwakeController.cs`
- `src/TokenStatus.Infrastructure/Awake/ExecutionStateNative.cs`
- `src/TokenStatus.App/UI/StatusViewModel.cs`
- `src/TokenStatus.App/UI/StatusPopupForm.cs`
- `tests/TokenStatus.Infrastructure.Tests/AwakeControllerTests.cs`

Implementation:

1. Extend the awake state contract so a failed activation or reassertion can be represented without claiming that keep-awake is active. Prefer a small diagnostic/status field over throwing across the dedicated owner thread.
2. In `ApplyStart`, call the native API before publishing the requested active state. On failure:
   - Keep the effective mode `Off`.
   - Publish a user-facing failure state.
   - Preserve a stable diagnostic code suitable for logging/tests.
3. Treat a failed resume reassertion the same way: clear the active state and tell the user it could not be restored.
4. Cleanup calls must still attempt `ExecutionState.Continuous`; cleanup failure may be logged but must not block shutdown.
5. Display a concise error in the popup and make it available to screen readers. Do not show “System” or “System + display” as active after failure.
6. Correct the existing test helper so timeout results are asserted and it does not wait for more events than the test produces.

Tests:

- Successful System and System+Display activation publishes the requested state.
- Failed activation remains Off and publishes the expected error/diagnostic.
- Failed reassertion after resume clears the prior active state.
- Expiry and explicit Stop still clear the native execution state.
- Dispose always attempts cleanup.
- No awake test passes after an ignored timeout.

Acceptance criteria:

- UI state always reflects the effective native state.
- Native failure is visible and does not crash the owner thread or application.

### P1.3 — Bound JSONL input while reading

Problem: `ReadLineAsync` allocates the complete line before enforcing `MaximumLineCharacters`, allowing a faulty or substituted CLI to cause excessive allocation.

Primary files:

- `src/TokenStatus.Infrastructure/Codex/JsonLineRpcConnection.cs`
- New focused bounded-reader tests
- `tests/TokenStatus.Infrastructure.Tests/JsonLineRpcConnectionTests.cs`
- `tests/TokenStatus.TestCli/Program.cs`

Implementation:

1. Add a stateful bounded asynchronous line reader with an internal reusable character buffer.
2. Detect `\n`, handle optional `\r`, retain characters read beyond the newline for the next call, and stop once the configured maximum is exceeded.
3. Throw `InvalidDataException` immediately on an oversized line and reset the Codex connection through the existing failure path.
4. Preserve cancellation and EOF behavior.
5. Do not implement the bound by awaiting one character at a time; use buffered reads and retain unread characters.
6. Use the bounded reader for every stdout JSONL line, including initialization.

Tests:

- Reads normal LF and CRLF messages.
- Reads multiple messages returned in a single underlying buffer.
- Handles a message split across many reads.
- Rejects at `maximum + 1` without waiting for EOF.
- Handles EOF with and without a final newline.
- Cancellation interrupts a pending read.
- An oversized TestCli response fails the request and does not leave a live orphan process.

Acceptance criteria:

- No code path calls `ReadLineAsync` for untrusted app-server stdout.
- Memory usage is bounded by the configured line limit plus a small fixed buffer.

### P1.4 — Make logging best-effort and non-fatal

Problem: log directory creation, rotation, and append operations can throw. A logging failure can mask the original provider error or prevent startup.

Primary files:

- `src/TokenStatus.Infrastructure/Logging/IRedactedLog.cs`
- `src/TokenStatus.Infrastructure/Logging/RedactingFileLog.cs`
- New `NullRedactedLog.cs` or equivalent
- `src/TokenStatus.App/ApplicationContext.cs`
- New logging tests

Implementation:

1. Add a safe factory that attempts to create the file logger and falls back to a no-op logger when the directory is unavailable.
2. Catch expected filesystem failures around rotation and append operations, including `IOException`, `UnauthorizedAccessException`, and relevant path/security exceptions.
3. After a persistent write failure, disable further file writes for that logger instance to avoid repeated exception overhead.
4. Never recursively log a logging failure.
5. Preserve current redaction, size cap, file rotation, and disposal behavior when storage is healthy.
6. Keep the log-folder UI action understandable when file logging is unavailable: disable it or show a clear non-secret error.

Tests:

- Constructor/factory falls back when the log directory cannot be created.
- Append and rotation failures do not escape `Information` or `Error`.
- A logger disables repeated writes after persistent failure.
- Healthy logging still rotates and redacts bearer tokens, API keys, emails, and account IDs.

Acceptance criteria:

- No provider, refresh, startup, or shutdown path can fail solely because diagnostic logging failed.

## 4. Phase B: State, Refresh, and UI Lifecycle

### P1.5 — Eliminate no-op snapshot notifications and repeated UI reconstruction

Problem: staleness checks publish unchanged snapshots. Every event recreates the tray icon and all quota rows, including while a previously opened popup is hidden.

Primary files:

- `src/TokenStatus.Core/Services/SnapshotStore.cs`
- `src/TokenStatus.Core/Services/RefreshCoordinator.cs`
- `src/TokenStatus.App/ApplicationContext.cs`
- `src/TokenStatus.App/UI/StatusPopupForm.cs`
- `src/TokenStatus.App/UI/VerticalStackLayout.cs`
- Core and UI-adjacent tests

Implementation:

1. Change `SnapshotStore.Update` so it does not assign or emit `Changed` when the update returns the same snapshot reference or an equal snapshot.
2. Ensure `MarkStaleAsync` returns the original snapshot when no field changes. Add tests that prove it emits no event before the stale threshold.
3. Track the last tray health in `ApplicationContext`. Recreate/dispose the icon only when health changes.
4. Do not update a hidden popup unless needed for notification state. `ShowPopup` already refreshes from `_snapshots.Current` before display.
5. When dynamic quota rows are removed, explicitly dispose their controls before clearing the collection.
6. Where practical, compare the previous and next provider result and rebuild only the changed section. This is secondary to eliminating no-op events and must not complicate correctness.
7. Preserve quota notification evaluation on every real snapshot change.

Tests:

- Returning the existing snapshot emits zero events.
- Returning an equal snapshot emits zero events.
- A real field change emits exactly one event outside the store lock.
- Healthy data does not generate a staleness event before the threshold.
- Data crossing the threshold generates exactly one stale transition.
- Repeated snapshots with unchanged overall health do not create a new tray icon; isolate icon selection behind a testable collaborator if necessary.
- Clearing dynamic rows disposes the removed controls.

Acceptance criteria:

- Normal polling produces one UI update per real provider result, not an additional no-op staleness update.
- A hidden popup performs no repeated layout reconstruction.
- Tray icons are replaced only when health color changes.

### P1.6 — Make Settings async operations lifecycle-safe

Problem: connection testing and saving can outlive a closed form. Test uses `CancellationToken.None`; closing during an await can resume against disposed controls.

Primary files:

- `src/TokenStatus.App/UI/SettingsForm.cs`
- `src/TokenStatus.App/ApplicationContext.cs`
- New settings form/lifecycle tests where practical

Implementation:

1. Give `SettingsForm` a lifetime `CancellationTokenSource` and cancel it during disposal/form close.
2. Pass the lifetime token into OpenCode Go credential testing.
3. Treat cancellation as a quiet user action; do not show “could not be reached.”
4. While testing:
   - Disable Test and conflicting credential controls.
   - Allow Cancel/close; cancellation must terminate the request and prevent post-disposal UI access.
5. While committing Save:
   - Disable the form's mutable controls, Cancel button, and system close action.
   - Restore controls if save fails.
   - Do not allow a half-finished commit to be abandoned by closing the window.
6. Guard all post-await UI updates with the form lifetime and disposal state.
7. Keep event handlers thin; move test/save workflows into awaitable private methods that can be tested directly.

Tests:

- Closing during Test cancels the provider call and produces no message box or disposed-control access.
- Cancellation is not logged or displayed as a connection failure.
- Save prevents closing until commit succeeds or fails.
- Controls are restored after a failed save.
- Double-clicking Test or Save cannot start concurrent operations.

Acceptance criteria:

- No asynchronous settings continuation touches a disposed form.
- The user receives deterministic feedback for success, failure, and cancellation.

### P1.7 — Use one authoritative Start-with-Windows state

Problem: the tray menu reads the registry while the Settings checkbox reads the JSON setting. External registry changes can make the two disagree, and saving an unrelated setting can unexpectedly change startup behavior.

Primary files:

- `src/TokenStatus.App/ApplicationContext.cs`
- `src/TokenStatus.App/UI/SettingsForm.cs`
- `src/TokenStatus.Infrastructure/Startup/CurrentUserStartupManager.cs`
- New startup-state tests

Implementation:

1. Treat the current-user Run registry entry as the effective source of truth.
2. Add a small startup abstraction if necessary so application coordination can be unit tested without writing the real registry.
3. On initialization and immediately before opening Settings, reconcile `_settings.StartWithWindows` with the effective registry state.
4. Pass the reconciled state to `SettingsForm` so its checkbox and the tray menu always agree.
5. Centralize startup changes in one method used by both the tray toggle and Settings save.
6. Make JSON/registry updates failure-safe:
   - Capture the previous effective startup state.
   - Apply the requested registry state.
   - Persist normalized settings.
   - If settings persistence fails, attempt to restore the previous registry state and retain the prior in-memory settings.
   - Report a clear failure if rollback also fails.
7. Do not let OpenCode credential changes occur before settings/startup validation. Order the save so a startup failure cannot silently delete or replace a valid credential.
8. Document the remaining cross-resource atomicity limit; Windows registry, JSON, and Credential Manager do not share a transaction.

Tests:

- Registry false/JSON true opens Settings unchecked.
- Registry true/JSON false opens Settings checked.
- Tray and Settings changes use the same coordinator method.
- Registry failure leaves JSON and in-memory state unchanged.
- Settings write failure rolls the registry back to its prior state.
- Credential mutation does not occur when startup/settings validation fails.

Acceptance criteria:

- Tray menu, Settings, registry, and in-memory state agree after startup and after every successful save.
- Saving an unrelated setting never changes an externally modified startup state unless the user changes the checkbox.

## 5. Phase C: Release Integrity

### P1.8 — Produce verifiable releases from clean source

Problem: the current executable is unsigned, its embedded source revision does not match HEAD, and publishing does not require a clean tree or passing tests. The script also hardcodes one developer's SDK location.

Primary files:

- `scripts/Publish.ps1`
- `Directory.Build.props`
- `README.md`
- New `.github/workflows/release.yml` if GitHub Actions is the selected CI
- Optional release verification script/tests

Implementation:

1. Make SDK selection portable:
   - Accept an optional `-DotNetPath`.
   - Otherwise resolve `dotnet` from PATH.
   - Validate that the selected SDK satisfies `global.json`.
2. Refuse a release publish when tracked files are dirty or HEAD is not the requested release commit/tag. Permit an explicit developer-only override that cannot be used by CI.
3. Derive assembly/file/informational versions and `SourceRevisionId` from the release version and exact HEAD commit. Remove `https://local.invalid/TokenStatus` metadata.
4. Before publish, run restore, Release build, all tests, formatting verification, and NuGet vulnerability audit. Stop immediately on failure.
5. Publish into a clean, versioned staging directory and create the ZIP only after validation succeeds.
6. Generate SHA-256 hashes for the executable and ZIP in a release manifest containing version, commit, RID, SDK version, and UTC build time.
7. Add Authenticode signing support using a certificate thumbprint or secure CI-provided certificate:
   - Sign the executable before zipping.
   - Timestamp it with a configured trusted timestamp service.
   - Verify the signature after signing and fail on any non-valid status.
8. Never create or ship a self-signed certificate as a substitute for production signing.
9. Add a `-RequireSigning` release mode. Official CI/tag releases must enable it. Local unsigned development publishing may remain available but must be labeled `UNSIGNED` and placed outside official artifact naming.
10. Add CI for pushes/PRs to build and test. Tagged release jobs must publish only from the tag commit, require signing secrets, and upload the ZIP plus manifest.
11. Update README release instructions and document certificate provisioning as an external prerequisite.

Tests/verification:

- Dirty-tree publish fails.
- Failing tests, formatting, or vulnerability audit prevent artifact creation.
- Embedded informational version contains the exact release commit.
- ZIP contains only the intended executable and release metadata.
- Manifest hashes match freshly calculated hashes.
- Official release mode fails when signing material is absent.
- Signed release reports `Valid` from `Get-AuthenticodeSignature` after timestamping.

External dependency:

Production completion requires a trusted code-signing certificate and secure CI secret provisioning. Luna must implement and test the signing path but must report the official signing gate as blocked until real signing material is supplied. Do not weaken the gate.

Acceptance criteria:

- Every official artifact is traceable to one clean commit, tested before packaging, hash-verifiable, signed, and timestamped.
- No machine-specific absolute SDK path remains in publishing or documented release commands.

## 6. Cross-Cutting Test Additions

Add focused tests rather than one broad integration fixture. At minimum, the final suite must cover:

- Executable resolution order and provenance.
- Bounded JSONL framing, cancellation, oversize rejection, and process cleanup.
- Keep-awake native failure and resume reassertion.
- Snapshot event suppression and stale transitions.
- Logger fallback, redaction, rotation, and write failure.
- Settings cancellation and non-reentrancy.
- Startup reconciliation and rollback.
- Release script preflight behavior where it can be tested safely.

Do not use the real Credential Manager, HKCU Run key, user settings directory, or production API endpoint in automated tests.

## 7. Commit Sequence

Use one reviewable commit per item unless a small prerequisite abstraction must be paired with its consumer:

1. `Harden Codex executable resolution`
2. `Report keep-awake activation failures`
3. `Bound Codex JSONL input while reading`
4. `Make file logging non-fatal`
5. `Suppress redundant snapshot and UI updates`
6. `Make settings async workflows lifecycle-safe`
7. `Reconcile start-with-Windows state`
8. `Harden and sign the release pipeline`
9. `Update remediation documentation and final verification`

Each commit must build and pass its relevant focused tests. The final branch must pass the complete verification matrix.

## 8. Final Verification Matrix

Automated:

```powershell
$sdk = 'C:\Users\Daniel\.dotnet-tokenstatus-sdk\dotnet.exe'
& $sdk restore .\TokenStatus.sln
& $sdk build .\TokenStatus.sln --configuration Release --no-restore
& $sdk test .\TokenStatus.sln --configuration Release --no-build
& $sdk format .\TokenStatus.sln --verify-no-changes
& $sdk list .\TokenStatus.sln package --vulnerable --include-transitive
```

Manual Windows checks:

1. Known OpenAI Codex installation wins over a test PATH shadow; configured custom path still wins when explicitly selected.
2. Simulated native keep-awake failure shows an error and never shows an active mode.
3. Leave the popup hidden through several refresh cycles and confirm stable GDI, USER, handle, and private-memory counts.
4. Close Settings during an API-key test and confirm clean cancellation with no later dialog.
5. Modify the startup Run entry externally, reopen Settings, and confirm the checkbox reflects the registry.
6. Make the log directory temporarily unavailable and confirm the app still starts and refreshes.
7. Feed an oversized app-server line and confirm bounded failure, provider degradation, and child-process cleanup.
8. Run the official release flow from a clean tag and verify version, commit, hashes, signature, timestamp, ZIP contents, and clean-machine startup.

## 9. Definition of Done

This remediation is complete when:

- All eight P1 items meet their acceptance criteria.
- Release build has zero warnings and all tests pass.
- Formatting and vulnerability checks pass.
- No new secret is written to settings, logs, URLs, command-line arguments, or test output.
- Runtime logs remain best-effort and redacted.
- Idle behavior shows no redundant snapshot events, tray icon replacements, or hidden-popup reconstruction.
- Effective keep-awake and startup states always match what the UI reports.
- Official release tooling enforces a clean, tested, traceable, hash-verifiable, signed build.
- Luna provides a final implementation summary listing changed files, new tests, command results, manual checks, and any external signing blocker.
