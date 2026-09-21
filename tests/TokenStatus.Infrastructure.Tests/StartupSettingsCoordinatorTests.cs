using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Startup;

namespace TokenStatus.Infrastructure.Tests;

public sealed class StartupSettingsCoordinatorTests
{
    [Fact]
    public void RegistryFalseAndJsonTrueReconcileToUnchecked()
    {
        var startup = new FakeStartupManager { Enabled = false };
        var settings = new FakeSettingsStore();
        var coordinator = new StartupSettingsCoordinator(startup, settings, "app.exe");

        var result = coordinator.Reconcile(new AppSettings { StartWithWindows = true });

        Assert.False(result.StartWithWindows);
    }

    [Fact]
    public void RegistryTrueAndJsonFalseReconcileToChecked()
    {
        var startup = new FakeStartupManager { Enabled = true };
        var settings = new FakeSettingsStore();
        var coordinator = new StartupSettingsCoordinator(startup, settings, "app.exe");

        var result = coordinator.Reconcile(new AppSettings { StartWithWindows = false });

        Assert.True(result.StartWithWindows);
    }

    [Fact]
    public async Task RegistryFailureLeavesSettingsUnchanged()
    {
        var startup = new FakeStartupManager { Enabled = false, ThrowOnSet = true };
        var settings = new FakeSettingsStore();
        var coordinator = new StartupSettingsCoordinator(startup, settings, "app.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(new AppSettings { StartWithWindows = true }, false, CancellationToken.None));

        Assert.Null(settings.Saved);
        Assert.False(startup.Enabled);
    }

    [Fact]
    public async Task SettingsFailureRollsRegistryBackToItsPriorState()
    {
        var startup = new FakeStartupManager { Enabled = false };
        var settings = new FakeSettingsStore { ThrowOnSave = true };
        var coordinator = new StartupSettingsCoordinator(startup, settings, "app.exe");

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.CommitAsync(new AppSettings { StartWithWindows = true }, false, CancellationToken.None));

        Assert.False(startup.Enabled);
        Assert.Equal(new[] { true, false }, startup.SetValues);
        Assert.Null(settings.Saved);
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public bool Enabled { get; set; }
        public bool ThrowOnSet { get; set; }
        public List<bool> SetValues { get; } = [];

        public bool IsEnabled(string executablePath) => Enabled;

        public void SetEnabled(string executablePath, bool enabled)
        {
            SetValues.Add(enabled);
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("registry failure");
            }

            Enabled = enabled;
        }
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public bool ThrowOnSave { get; set; }
        public AppSettings? Saved { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            if (ThrowOnSave)
            {
                throw new IOException("settings failure");
            }

            Saved = settings;
            return Task.CompletedTask;
        }
    }
}
