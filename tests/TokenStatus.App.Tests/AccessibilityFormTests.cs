using System.Reflection;
using System.Windows.Forms;
using TokenStatus.App.UI;
using TokenStatus.Core.Models;
using Xunit;

namespace TokenStatus.App.Tests;

public sealed class AccessibilityFormTests
{
    [Fact]
    public void PopupExposesIdentityNamedFocusableControlsAndProgressValues()
    {
        RunOnSta(() =>
        {
            using var form = CreateForm();
            form.CreateControl();

            Assert.Equal("/status usage and keep-awake status", form.AccessibleName);
            Assert.Equal(AccessibleRole.Window, form.AccessibleRole);

            var focusable = Descendants(form)
                .Where(control => control.TabStop && control.Enabled)
                .ToArray();
            Assert.NotEmpty(focusable);
            Assert.All(focusable, control => Assert.False(string.IsNullOrWhiteSpace(control.AccessibleName)));

            var progress = Descendants(form)
                .OfType<ProgressBar>()
                .Single(control => control.AccessibleName == "5-hour limit quota remaining");
            Assert.Equal(ProgressBarStyle.Continuous, progress.Style);
            Assert.Equal(76, progress.Value);
            Assert.Equal(AccessibleRole.ProgressBar, progress.AccessibleRole);
            Assert.Contains("quota remaining", progress.AccessibleName, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void KeepAwakeApplyPreservesSystemAndDisplayModeWhenDurationChanges()
    {
        RunOnSta(() =>
        {
            AwakeMode? appliedMode = null;
            TimeSpan? appliedDuration = null;
            using var form = new StatusPopupForm(
                CreateSnapshot(),
                () => Task.FromResult(new RefreshOutcome(true, "Updated just now.")),
                () => { },
                (mode, duration) =>
                {
                    appliedMode = mode;
                    appliedDuration = duration;
                },
                () => { });

            var modeRadio = Get<RadioButton>(form, "_awakeSystemAndDisplayRadio");
            var durationRadio = Get<RadioButton>(form, "_awakeTwoHoursRadio");
            modeRadio.Checked = true;
            durationRadio.Checked = true;
            typeof(StatusPopupForm)
                .GetMethod("ApplyAwakeSelection", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(form, null);

            Assert.Equal(AwakeMode.SystemAndDisplay, appliedMode);
            Assert.Equal(TimeSpan.FromHours(2), appliedDuration);
            Assert.Equal(FlatStyle.Standard, modeRadio.FlatStyle);
            Assert.Equal(FlatStyle.Standard, durationRadio.FlatStyle);
            Assert.True(modeRadio.UseVisualStyleBackColor);
            Assert.True(durationRadio.UseVisualStyleBackColor);
        });
    }

    [Fact]
    public void KeepAwakeDurationHeadingRemainsReadableWhenChoicesAreUnavailable()
    {
        RunOnSta(() =>
        {
            using var form = CreateForm();
            form.CreateControl();

            var durationGroup = Get<GroupBox>(form, "_awakeDurationGroup");
            Assert.True(durationGroup.Enabled);
            Assert.Contains("Choose System", durationGroup.AccessibleDescription, StringComparison.Ordinal);
            Assert.False(Get<RadioButton>(form, "_awakeThirtyMinutesRadio").Enabled);
            Assert.False(Get<RadioButton>(form, "_awakeUntilOffRadio").Enabled);
        });
    }

    [Fact]
    public void SettingsUsesNamedGroupsAndVisibleIntervalUnits()
    {
        RunOnSta(() =>
        {
            using var form = CreateSettingsForm(openCodeGoApiKeyConfigured: false);
            form.CreateControl();

            var groups = Descendants(form)
                .OfType<GroupBox>()
                .Select(group => group.Text.Replace("&", string.Empty, StringComparison.Ordinal))
                .ToArray();
            Assert.Contains("Providers", groups);
            Assert.Contains("Refresh intervals", groups);
            Assert.Contains("Notifications and startup", groups);
            Assert.Contains("Diagnostics", groups);
            Assert.Equal(3, Descendants(form).Count(control => control is Label label && label.Text == "seconds"));
        });
    }

    [Fact]
    public void SettingsInputsAndActionsKeepReadableSpacingWithoutOverlap()
    {
        RunOnSta(() =>
        {
            using var form = CreateSettingsForm(openCodeGoApiKeyConfigured: true);
            form.Show();
            Application.DoEvents();

            var controls = Descendants(form).ToArray();
            var pathHint = controls
                .OfType<Label>()
                .Single(control => control.AccessibleName == "Codex executable resolution");
            var apiKey = controls
                .OfType<TextBox>()
                .Single(control => control.AccessibleName == "OpenCode Go API key");

            var hintBounds = form.RectangleToClient(pathHint.RectangleToScreen(pathHint.ClientRectangle));
            var apiKeyBounds = form.RectangleToClient(apiKey.RectangleToScreen(apiKey.ClientRectangle));
            Assert.False(hintBounds.IntersectsWith(apiKeyBounds));

            var contentPadding = apiKey.GetType().GetProperty("HorizontalContentPadding");
            Assert.NotNull(contentPadding);
            Assert.True((int)contentPadding.GetValue(apiKey)! >= 8);

            Assert.All(controls.OfType<Button>(), button =>
            {
                var label = button.Text.Replace("&", string.Empty, StringComparison.Ordinal);
                var labelWidth = TextRenderer.MeasureText(label, button.Font).Width;
                Assert.True(
                    button.ClientSize.Width >= labelWidth,
                    $"Button '{label}' is narrower than its rendered label.");
            });
        });
    }

    private static StatusPopupForm CreateForm()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = AppSnapshot.Initial(now) with
        {
            CodexRateLimits = new ProviderResult<CodexRateLimits>(
                ProviderHealth.Healthy,
                new CodexRateLimits(
                [
                    new RateLimitBucket(
                        "codex",
                        null,
                        null,
                        new RateLimitWindow(24, TimeSpan.FromHours(5), now.AddHours(2)),
                        null,
                        true,
                        null)
                ],
                null),
                now,
                now,
                null,
                null),
            OpenCodeGoQuota = new ProviderResult<OpenCodeGoQuota>(
                ProviderHealth.Healthy,
                new OpenCodeGoQuota(
                    new OpenCodeQuotaWindow(24, now.AddHours(2), false),
                    new OpenCodeQuotaWindow(88, now.AddDays(2), false),
                    new OpenCodeQuotaWindow(91, now.AddDays(14), false)),
                now,
                now,
                null,
                null)
        };

        return new StatusPopupForm(
            snapshot,
            () => Task.FromResult(new RefreshOutcome(true, "Updated just now.")),
            () => { },
            (_, _) => { },
            () => { });
    }

    private static SettingsForm CreateSettingsForm(bool openCodeGoApiKeyConfigured) => new(
        new AppSettings(),
        openCodeGoApiKeyConfigured,
        (_, _) => Task.FromResult(new OpenCodeGoQuota(
            new OpenCodeQuotaWindow(10, DateTimeOffset.UtcNow.AddHours(1), false),
            new OpenCodeQuotaWindow(20, DateTimeOffset.UtcNow.AddDays(1), false),
            new OpenCodeQuotaWindow(30, DateTimeOffset.UtcNow.AddDays(10), false))),
        (_, _, _) => Task.CompletedTask,
        string.Empty);

    private static T Get<T>(Control root, string fieldName)
        where T : Control
    {
        var field = typeof(StatusPopupForm).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {fieldName}.");
        return (T)(field.GetValue(root) ?? throw new InvalidOperationException($"Field {fieldName} is null."));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new AggregateException("STA UI test failed.", failure);
        }
    }

    private static AppSnapshot CreateSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return AppSnapshot.Initial(now);
    }
}
