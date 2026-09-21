using System.Collections.Concurrent;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Awake;

namespace TokenStatus.Infrastructure.Tests;

public sealed class AwakeControllerTests
{
    [Fact]
    public async Task StartsAndStopsOnOneDedicatedOwnerThread()
    {
        var native = new RecordingNative();
        using var controller = new WindowsAwakeController(native);
        controller.Start(AwakeMode.SystemAndDisplay, TimeSpan.FromSeconds(10));
        await native.WaitForCountAsync(1);
        controller.Stop();
        await native.WaitForCountAsync(1);

        Assert.Contains(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired, native.States);
        Assert.Equal(ExecutionState.Continuous, native.States.Last());
        Assert.Single(native.ThreadIds.Distinct());
    }

    [Fact]
    public async Task TimedSessionExpiresAndClearsExecutionState()
    {
        var native = new RecordingNative();
        using var controller = new WindowsAwakeController(native);
        controller.Start(AwakeMode.System, TimeSpan.FromMilliseconds(80));
        await native.WaitForStateAsync(ExecutionState.Continuous | ExecutionState.SystemRequired);
        await native.WaitForStateAsync(ExecutionState.Continuous, TimeSpan.FromSeconds(2));

        Assert.Equal(AwakeMode.Off, controller.Current.Mode);
    }

    [Fact]
    public async Task FailedActivationPublishesOffStateWithDiagnostic()
    {
        var native = new RecordingNative(_ => false);
        using var controller = new WindowsAwakeController(native);
        var changed = new TaskCompletionSource<AwakeState>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += (_, state) => changed.TrySetResult(state);

        controller.Start(AwakeMode.System, TimeSpan.FromMinutes(1));
        var state = await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AwakeMode.Off, state.Mode);
        Assert.Equal("awake_activation_failed", state.DiagnosticCode);
        Assert.False(state.IsActive);
        Assert.Equal(AwakeMode.Off, controller.Current.Mode);
    }

    [Fact]
    public async Task FailedResumeReassertionClearsThePriorActiveState()
    {
        var native = new RecordingNative(_ => true);
        using var controller = new WindowsAwakeController(native);
        var reassertionFailure = new TaskCompletionSource<AwakeState>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += (_, state) =>
        {
            if (state.DiagnosticCode == "awake_reassert_failed")
            {
                reassertionFailure.TrySetResult(state);
            }
        };

        controller.Start(AwakeMode.SystemAndDisplay, TimeSpan.FromMinutes(1));
        await native.WaitForStateAsync(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
        native.Succeed = false;
        controller.Reassert();

        var state = await reassertionFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AwakeMode.Off, state.Mode);
        Assert.Equal("awake_reassert_failed", state.DiagnosticCode);
        Assert.False(state.IsActive);
    }

    private sealed class RecordingNative(Func<ExecutionState, bool>? shouldSucceed = null) : IExecutionStateNative
    {
        private readonly ConcurrentQueue<ExecutionState> _states = new();
        private readonly ConcurrentQueue<int> _threadIds = new();
        private readonly SemaphoreSlim _changed = new(0);
        private readonly Func<ExecutionState, bool>? _shouldSucceed = shouldSucceed;

        public IReadOnlyCollection<ExecutionState> States => _states.ToArray();
        public IReadOnlyCollection<int> ThreadIds => _threadIds.ToArray();
        public bool Succeed { get; set; } = true;

        public bool Set(ExecutionState state)
        {
            _states.Enqueue(state);
            _threadIds.Enqueue(Environment.CurrentManagedThreadId);
            _changed.Release();
            return Succeed && (_shouldSucceed?.Invoke(state) ?? true);
        }

        public async Task WaitForCountAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _changed.WaitAsync(TimeSpan.FromSeconds(2)));
            }
        }

        public async Task WaitForStateAsync(ExecutionState expected, TimeSpan? timeout = null)
        {
            var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
            while (DateTime.UtcNow < until)
            {
                if (States.Contains(expected))
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Contains(expected, States);
        }
    }
}
