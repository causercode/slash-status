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
        await native.WaitForCountAsync(2);

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

    private sealed class RecordingNative : IExecutionStateNative
    {
        private readonly ConcurrentQueue<ExecutionState> _states = new();
        private readonly ConcurrentQueue<int> _threadIds = new();
        private readonly SemaphoreSlim _changed = new(0);

        public IReadOnlyCollection<ExecutionState> States => _states.ToArray();
        public IReadOnlyCollection<int> ThreadIds => _threadIds.ToArray();

        public bool Set(ExecutionState state)
        {
            _states.Enqueue(state);
            _threadIds.Enqueue(Environment.CurrentManagedThreadId);
            _changed.Release();
            return true;
        }

        public async Task WaitForCountAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await _changed.WaitAsync(TimeSpan.FromSeconds(2));
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
