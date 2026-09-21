# TokenStatus UI and Accessibility Implementation Plan

Status: implementation-ready handoff for Luna  
Prepared: 2026-09-20  
Target: after P1 hardening commit `0f8a7a4` and the SDK/README follow-up  
Platforms: Windows 10/11 x64, WinForms, .NET 10

## 1. Goal

Make /status easier to understand at a glance and fully usable with keyboard navigation, Windows Narrator, high-contrast themes, increased text size, and mixed-DPI multi-monitor setups.

This is an incremental WinForms improvement, not a visual-framework rewrite. Preserve the tray-first product model, current provider behavior, dark/light visual identity, and portable single-file release.

## 2. Current Strengths to Preserve

- Clear provider cards and concise quota percentages.
- Visible remaining quota plus reset time.
- Dark and light theme support.
- Accessible names already present on several custom controls.
- Keyboard-focusable native buttons and settings fields.
- Screen-reader-readable percentage text next to every visual quota bar.
- Debug-only layout inspector for visual diagnosis.
- Compact notification-area workflow with no permanent main window.

## 3. Confirmed Problems

1. The popup window is borderless and does not expose a useful window title through UI Automation.
2. Provider health is communicated mainly through color and a small icon. Healthy/stale/error should also be visible text.
3. The custom quota bar declares a progress role, but live UI Automation did not expose it as a progress-bar element.
4. Keep-awake controls look like separate mode and duration selectors, but every button is an immediate command. Duration buttons silently switch back to System mode.
5. Refresh has no in-progress, completion, or failure feedback.
6. The displayed update age and keep-awake countdown only change when another snapshot arrives.
7. Settings are a flat grid. Refresh interval values have no visible units, and provider setup is mixed with application behavior.
8. Custom theme colors ignore Windows High Contrast.
9. Fixed form dimensions, fixed row heights, and fixed label maximum widths are vulnerable to clipping at increased text size and high DPI.
10. Settings and popup focus behavior have not been systematically verified with Narrator, keyboard-only navigation, mixed DPI, or multiple monitors.

## 4. Design Principles

- Never use color as the only carrier of meaning.
- Prefer native WinForms controls and standard accessibility behavior.
- Every actionable control must have a useful accessible name, role, state, and keyboard path.
- Dynamic status changes must be announced without repeatedly stealing focus.
- Use plain language: “remaining,” “resets,” “refresh interval,” and explicit units.
- Preserve a compact flyout, but allow scrolling and growth when text or DPI requires it.
- A user must be able to complete every workflow without a mouse.
- High Contrast takes precedence over brand colors.
- Avoid custom accessibility providers when a native control can provide the same experience.

## 5. Phase A — Clarify the Popup Interaction Model

### A1. Give the popup a real accessible identity

Primary files:

- `src/TokenStatus.App/UI/StatusPopupForm.cs`
- `src/TokenStatus.App/AppBrand.cs`

Implementation:

1. Set the form `Text`, `AccessibleName`, and `AccessibleDescription` to concise, stable values such as “/status usage and keep-awake status.”
2. Verify that the top-level UI Automation window exposes that name even with `FormBorderStyle.None`.
3. Set a deliberate initial focus when the popup opens. Prefer the Refresh button or the first provider heading; do not leave focus on an unnamed container.
4. Preserve Escape-to-close and normal flyout deactivation behavior, but do not hide while a child dialog, context menu, tooltip interaction, or accessibility action legitimately owns focus.
5. Restore focus predictably to the tray workflow after Settings closes.

Acceptance criteria:

- Narrator announces the popup name once when opened.
- The top-level UIA window has a non-empty name and Window role.
- Escape closes the popup, Tab starts at a predictable control, and no focus trap exists.

### A2. Add visible provider health text

Primary files:

- `src/TokenStatus.App/UI/ProviderHealthIndicator.cs`
- `src/TokenStatus.App/UI/StatusPopupForm.cs`
- `src/TokenStatus.App/UI/StatusViewModel.cs`
- `src/TokenStatus.App/UI/WindowsTheme.cs`

Implementation:

1. Replace the icon-only health treatment with a compact status badge containing text: Loading, Healthy, Stale, Not configured, Not authenticated, Unsupported, or Error.
2. Keep the current icon/color as a secondary cue.
3. Give the badge an accessible name containing provider and state, for example “Codex status: Healthy.”
4. When state changes while the popup is visible, raise a polite accessibility notification or update a dedicated live-status control. Do not move keyboard focus.
5. Ensure error details remain readable and are associated with the provider card.

Acceptance criteria:

- Every provider state can be understood in grayscale and without seeing the icon.
- Narrator reads provider name, current state, quota values, and any error in a sensible order.

### A3. Make manual refresh observable

Primary files:

- `src/TokenStatus.App/ApplicationContext.cs`
- `src/TokenStatus.App/UI/StatusPopupForm.cs`

Implementation:

1. Change the popup refresh callback from fire-and-forget `Action` to an awaitable operation that returns completion/failure information.
2. While refreshing:
   - Disable the Refresh button.
   - Change its label to “Refreshing…” without changing its position.
   - Expose the busy state through accessibility properties.
3. On completion, restore the button and update a non-modal status line such as “Updated just now.”
4. On failure, keep last-known-good values, show a concise status message, and announce the failure politely.
5. Prevent concurrent manual refresh commands.

Acceptance criteria:

- Mouse, keyboard, and screen-reader users receive equivalent refresh feedback.
- Repeated activation cannot create parallel manual refresh workflows.

### A4. Keep relative time accurate while visible

Primary files:

- `src/TokenStatus.App/UI/StatusPopupForm.cs`
- `src/TokenStatus.App/UI/StatusViewModel.cs`

Implementation:

1. Add one WinForms timer owned by the popup and enabled only while visible.
2. Update the “Updated … ago” label and active keep-awake remaining time at an appropriate cadence, no faster than once per 30 seconds.
3. Stop and dispose the timer when hidden/disposed.
4. Do not rebuild provider quota controls for a time-label-only update.
5. Avoid announcing every timer tick to assistive technology.

Acceptance criteria:

- Visible relative times do not remain stale for multiple minutes.
- Timer activity stops when the popup is hidden.

## 6. Phase B — Redesign Keep Awake as One Coherent Form

### B1. Separate mode selection from duration selection

Primary files:

- `src/TokenStatus.App/UI/StatusPopupForm.cs`
- `src/TokenStatus.App/UI/StatusViewModel.cs`
- `src/TokenStatus.Core/Models/AwakeState.cs`

Recommended interaction:

- Mode group: Off, System, System + display.
- Duration group, enabled only for active modes: 30 minutes, 1 hour, 2 hours, 4 hours, Until turned off.
- One explicit Apply button.
- A current-state line such as “Active: System + display · 1h 42m remaining.”

Implementation:

1. Use native RadioButton controls inside labeled/grouped containers so selection and relationships are exposed automatically.
2. Selecting Off disables duration choices. Selecting an active mode enables them.
3. Initialize selections from the current awake state when the popup opens.
4. Applying a duration must preserve the selected mode; it must never silently switch System + display to System.
5. After Apply, keep focus stable and announce the effective state.
6. If native activation fails, show the P1 error state inline and leave the controls reflecting Off.
7. Keep the tray menu's hierarchical quick actions; ensure its labels match popup terminology exactly.

Tests:

- Every mode/duration pair invokes the expected `AwakeMode` and `TimeSpan?`.
- Off does not carry a stale duration.
- System + display plus 2 hours remains System + display.
- Native activation failure restores Off and exposes the error.
- Radio groups have correct accessible names and selected states.

Acceptance criteria:

- A first-time user can predict the effect before applying it.
- Mode and duration are keyboard-selectable using standard arrow-key behavior.

## 7. Phase C — Improve Quota Semantics and Information Hierarchy

### C1. Make quota bars accessible

Primary files:

- `src/TokenStatus.App/UI/QuotaUsageView.cs`

Implementation options, in preference order:

1. Use a native ProgressBar if it can meet theme and contrast requirements without owner-drawing accessibility regressions.
2. Otherwise keep the visual custom bar but override its accessibility object so UI Automation exposes:
   - ProgressBar role.
   - Provider/window-specific name, such as “Codex five-hour quota remaining.”
   - Current value from 0 to 100.
   - Read-only state.
3. Mark purely decorative child graphics as non-accessible so Narrator does not repeat information.
4. Keep the visible percentage label; accessibility must not depend solely on the bar.
5. Rename the existing ambiguous accessible phrase “usage remaining” to “quota remaining.”

Acceptance criteria:

- Accessibility Insights and UI Automation expose each bar as a progress indicator with the correct name and value.
- Narrator does not read duplicate unlabeled panes for one quota window.

### C2. Improve scan order and card summaries

Primary files:

- `src/TokenStatus.App/UI/StatusPopupForm.cs`
- `src/TokenStatus.App/UI/SectionCard.cs`
- `src/TokenStatus.App/UI/VerticalStackLayout.cs`

Implementation:

1. Use this reading order inside each provider card:
   - Provider name and health.
   - Quota window name and percent remaining.
   - Reset time.
   - Token activity or provider-specific details.
   - Error/help action.
2. Assign explicit `TabIndex` only to actionable controls; static text and decorative graphics must not enter the tab order.
3. Avoid duplicate accessible names inherited from nested TableLayoutPanel containers.
4. If a provider is not configured, replace “Not provided” repetition with one clear explanation and an actionable “Open Settings” button or link.
5. Use consistent provider naming: “OpenCode Go” everywhere rather than mixing “OpenCode,” “OC,” and “Go” in visible UI where space permits.

Acceptance criteria:

- UIA reading order matches visual order.
- Provider cards have no repeated or unnamed accessibility elements.

## 8. Phase D — Reorganize Settings

### D1. Group settings by purpose

Primary files:

- `src/TokenStatus.App/UI/SettingsForm.cs`
- `src/TokenStatus.App/ApplicationContext.cs`

Recommended sections:

1. Providers
   - Codex executable, resolved path/provenance, Auto, Test.
   - OpenCode Go API key, configured state, Test, Clear.
2. Refresh intervals
   - Codex quota.
   - Token activity.
   - OpenCode Go quota.
3. Notifications and startup
   - Quota notifications.
   - Start with Windows.
4. Diagnostics
   - Open log folder.

Implementation:

1. Use accessible GroupBox controls or equivalent labeled sections.
2. Add visible units to every interval, preferably “seconds” or a friendly duration selector. Labels should say “refresh interval,” not just “refresh.”
3. Associate every field label with its input and verify the accessible name through UI Automation.
4. Display Codex resolution provenance below the field without turning the automatically selected path into misleading “Configured path” provenance when Auto is clicked.
5. Add concise help text explaining that a blank Codex path means automatic detection.
6. Add a “Show API key” checkbox with an accessible checked state. Default remains masked.
7. Display API-key state as “Configured,” “Will be removed when saved,” or “Not configured.”
8. Use `CenterParent` or explicit monitor-aware positioning tied to the invoking tray/popup screen.
9. Preserve the P1 cancellation, non-reentrancy, startup reconciliation, and save rollback behavior.

Acceptance criteria:

- A user can distinguish provider setup, refresh timing, behavior, and diagnostics without reading every row.
- Every field has visible units or a self-describing choice.
- Settings remain fully operable at 200% display scaling and increased system text size.

### D2. Improve keyboard behavior and shortcuts

Primary files:

- `src/TokenStatus.App/UI/SettingsForm.cs`
- `src/TokenStatus.App/UI/StatusPopupForm.cs`

Implementation:

1. Define a deliberate tab order following visual order.
2. Add unique mnemonic labels to primary actions where they do not clutter the flyout: Refresh, Settings, Test, Save, Cancel, and Apply.
3. Keep Enter mapped to the appropriate primary action and Escape mapped to close/cancel only when a save is not committing.
4. Ensure disabled controls are skipped and focus moves to the next logical element.
5. After validation errors, focus the field that needs correction and announce the error.

Acceptance criteria:

- All workflows can be completed with Tab, Shift+Tab, arrows, Space, Enter, and Escape.
- There are no duplicate mnemonics within one window.

## 9. Phase E — Theme, Contrast, DPI, and Text Scaling

### E1. Support Windows High Contrast

Primary files:

- `src/TokenStatus.App/UI/WindowsTheme.cs`
- All custom-painted controls

Implementation:

1. Check `SystemInformation.HighContrast` before applying light/dark palettes.
2. In High Contrast, use `SystemColors` for backgrounds, text, borders, highlights, errors, and progress indicators.
3. Do not force dark title bars, rounded-region styling, or brand tinting when those interfere with High Contrast.
4. Reapply the theme when Windows sends relevant preference/display changes.
5. Ensure selected, focused, disabled, stale, and error states remain distinguishable.

Acceptance criteria:

- Popup, context menu, settings, custom icons, bars, and focus indicators remain readable in Windows High Contrast themes.
- No essential state depends on brand colors.

### E2. Remove clipping assumptions

Primary files:

- `src/TokenStatus.App/UI/StatusPopupForm.cs`
- `src/TokenStatus.App/UI/SettingsForm.cs`
- `src/TokenStatus.App/UI/QuotaUsageView.cs`
- `src/TokenStatus.App/UI/LayoutMetrics.cs`

Implementation:

1. Replace fixed row heights with AutoSize plus minimum sizes where practical.
2. Calculate popup width and maximum label width from the current DPI and working area rather than fixed physical assumptions.
3. Permit wrapping for error/help text and long provider names.
4. Keep controls reachable through scrolling when the screen is too short.
5. Handle `DpiChanged` by relayout and repositioning within the destination monitor's working area.
6. Test mixed-DPI monitor moves while the popup and Settings are open.
7. Reuse/dispose Font and graphics resources correctly during DPI/theme changes.

Acceptance criteria:

- No clipping or overlap at 100%, 150%, 200%, or 225% display scaling.
- No clipping with Windows text size increased to 200%.
- The popup remains fully within the active monitor's working area.

### E3. Verify contrast and focus visibility

Implementation:

1. Measure text/background color pairs and target WCAG 2.2 AA contrast as a practical desktop baseline:
   - 4.5:1 for normal text.
   - 3:1 for large text, graphical controls, and focus indicators.
2. Add an obvious keyboard focus rectangle to custom or flat-styled controls.
3. Validate light, dark, and High Contrast palettes separately.
4. Document the measured combinations in the final implementation summary.

## 10. Phase F — Accessibility Verification and Regression Tests

### F1. Add WinForms UI tests

Recommended project:

- `tests/TokenStatus.App.Tests/TokenStatus.App.Tests.csproj`, targeting `net10.0-windows`

Implementation:

1. Run form/control tests on an STA thread.
2. Build deterministic snapshots for loading, healthy, stale, not configured, authentication failure, quota critical, and keep-awake failure states.
3. Verify:
   - Every enabled focusable control has a non-empty accessible name.
   - Expected roles and states are exposed.
   - Tab order is unique and follows visual order.
   - Provider status text is present for every health enum value.
   - Keep-awake mode/duration mapping is correct.
   - Progress controls expose names and values.
   - High Contrast selects system colors.
   - Forms lay out without overlapping control bounds at representative DPI/font settings where automation permits.
4. Keep tests independent of real credentials, registry, provider processes, and network access.

### F2. Add a manual UIA inspection script

Recommended file:

- `scripts/Inspect-Accessibility.ps1`

The script should enumerate only TokenStatus windows and report each element's name, role, focusability, enabled state, offscreen state, and bounds. It must be read-only and must not invoke destructive actions.

Use it to catch:

- Empty names on focusable controls.
- Duplicate container names.
- Controls outside the window bounds.
- Missing progress values.
- Unexpected focus order.

### F3. Required manual test matrix

Run on a Release candidate:

1. Keyboard only, no mouse.
2. Narrator browse and focus modes.
3. Accessibility Insights FastPass and tab-stop inspection.
4. Light theme.
5. Dark theme.
6. At least one Windows High Contrast theme.
7. 100%, 150%, 200%, and 225% display scaling.
8. Windows text size at 100%, 150%, and 200%.
9. Two monitors with different DPI settings.
10. Loading, healthy, stale, not configured, authentication error, quota critical, and keep-awake failure states.
11. Settings test cancellation, failed save, and successful save.
12. Popup opened from the notification area and keyboard notification-area navigation.

Capture screenshots and the UIA inspection output for each distinct layout/theme combination. Do not treat screenshots alone as accessibility evidence.

## 11. Suggested Commit Sequence

1. `Clarify popup identity, provider health, and refresh feedback`
2. `Redesign keep-awake mode and duration controls`
3. `Expose quota progress through UI Automation`
4. `Reorganize settings and add interval units`
5. `Add keyboard order, mnemonics, and focus recovery`
6. `Support High Contrast and resilient DPI layout`
7. `Add WinForms accessibility tests and inspection tooling`
8. `Document and verify the UI accessibility matrix`

Each commit must build, pass focused tests, and leave the popup usable before moving to the next phase.

## 12. Final Verification Commands

```powershell
.\scripts\Invoke-DotNet.ps1 restore .\TokenStatus.sln
.\scripts\Invoke-DotNet.ps1 build .\TokenStatus.sln --configuration Release --no-restore
.\scripts\Invoke-DotNet.ps1 test .\TokenStatus.sln --configuration Release --no-build
.\scripts\Invoke-DotNet.ps1 format .\TokenStatus.sln --verify-no-changes
.\scripts\Invoke-DotNet.ps1 list .\TokenStatus.sln package --vulnerable --include-transitive
.\scripts\Publish.ps1 -Version 1.0.0 -AllowDeveloperOverride
```

## 13. Definition of Done

The UI/accessibility work is complete when:

- Popup and Settings expose useful window names through UI Automation.
- All functionality is available by keyboard without traps or hidden actions.
- Narrator reads provider, health, quota, reset, error, keep-awake, and refresh states in a logical order.
- Provider health and errors never rely on color alone.
- Quota bars expose correct progress names and values.
- Keep-awake mode and duration cannot contradict each other.
- Refresh operations provide accessible busy, success, and failure feedback.
- Light, dark, and High Contrast modes are readable with visible focus.
- No clipping occurs across the required DPI and text-size matrix.
- Automated UI tests, Accessibility Insights, Narrator, UIA inspection, and the full existing test suite pass.
- Luna's final summary includes before/after screenshots, accessibility inspection output, contrast measurements, test results, manual matrix results, and any consciously deferred limitation.
