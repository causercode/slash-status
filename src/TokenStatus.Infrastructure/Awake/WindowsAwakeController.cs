using System.Collections.Concurrent;
using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;

namespace TokenStatus.Infrastructure.Awake;

public sealed class WindowsAwakeController : IAwakeController
{
    private readonly IExecutionStateNative _native;
    private readonly IClock _clock;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly Thread _ownerThread;
    private readonly object _stateGate = new();
    private AwakeState _current = AwakeState.Off;
    private int _disposeRequested;
    private bool _ownerDisposed;

    public WindowsAwakeController(IExecutionStateNative? native = null, IClock? clock = null)
    {
        _native = native ?? new ExecutionStateNative();
        _clock = clock ?? new SystemClock();
        _ownerThread = new Thread(OwnerLoop)
        {
            IsBackground = true,
            Name = "TokenStatus.KeepAwake"
        };
        _ownerThread.Start();
    }

    public AwakeState Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<AwakeState>? Changed;

    public void Start(AwakeMode mode, TimeSpan? duration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        if (mode == AwakeMode.Off)
        {
            Stop();
            return;
        }

        TimeSpan? normalizedDuration = duration is { } value && value > TimeSpan.Zero ? value : null;
        Enqueue(new Command(CommandKind.Start, mode, normalizedDuration));
    }

    public void Stop()
    {
        if (Volatile.Read(ref _disposeRequested) != 0)
        {
            return;
        }

        Enqueue(new Command(CommandKind.Stop, AwakeMode.Off, null));
    }

    public void Reassert()
    {
        if (Volatile.Read(ref _disposeRequested) == 0)
        {
            Enqueue(new Command(CommandKind.Reassert, AwakeMode.Off, null));
        }
    }

    private void Enqueue(Command command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    private void OwnerLoop()
    {
        try
        {
            while (true)
            {
                while (_commands.TryDequeue(out var command))
                {
                    if (command.Kind == CommandKind.Dispose)
                    {
                        ApplyOff();
                        return;
                    }

                    switch (command.Kind)
                    {
                        case CommandKind.Start:
                            ApplyStart(command.Mode, command.Duration);
                            break;
                        case CommandKind.Stop:
                            ApplyOff();
                            break;
                        case CommandKind.Reassert:
                            ApplyCurrentFlags();
                            break;
                    }
                }

                var waitMilliseconds = GetWaitMilliseconds();
                _wake.WaitOne(waitMilliseconds);
                if (Current.ExpiresAt is { } expiresAt && _clock.UtcNow >= expiresAt)
                {
                    ApplyOff();
                }
            }
        }
        finally
        {
            _native.Set(ExecutionState.Continuous);
            SetCurrent(AwakeState.Off);
            _ownerDisposed = true;
            _stopped.Set();
        }
    }

    private int GetWaitMilliseconds()
    {
        var expiresAt = Current.ExpiresAt;
        if (expiresAt is null)
        {
            return Timeout.Infinite;
        }

        var remaining = expiresAt.Value - _clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return 0;
        }

        return (int)Math.Clamp(remaining.TotalMilliseconds, 1, int.MaxValue);
    }

    private void ApplyStart(AwakeMode mode, TimeSpan? duration)
    {
        var now = _clock.UtcNow;
        var state = new AwakeState(mode, now, duration is { } value ? now + value : null);
        _native.Set(GetFlags(mode));
        SetCurrent(state);
    }

    private void ApplyCurrentFlags()
    {
        var mode = Current.Mode;
        _native.Set(GetFlags(mode));
    }

    private void ApplyOff()
    {
        _native.Set(ExecutionState.Continuous);
        SetCurrent(AwakeState.Off);
    }

    private static ExecutionState GetFlags(AwakeMode mode) => mode switch
    {
        AwakeMode.System => ExecutionState.Continuous | ExecutionState.SystemRequired,
        AwakeMode.SystemAndDisplay => ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired,
        _ => ExecutionState.Continuous
    };

    private void SetCurrent(AwakeState state)
    {
        var changed = false;
        lock (_stateGate)
        {
            if (_current != state)
            {
                _current = state;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, state);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        _commands.Enqueue(new Command(CommandKind.Dispose, AwakeMode.Off, null));
        _wake.Set();
        if (Environment.CurrentManagedThreadId != _ownerThread.ManagedThreadId)
        {
            _stopped.Wait(TimeSpan.FromSeconds(2));
        }

        if (!_ownerDisposed && _ownerThread.IsAlive)
        {
            _ownerThread.Join(TimeSpan.FromSeconds(1));
        }

        _wake.Dispose();
        _stopped.Dispose();
    }

    private enum CommandKind
    {
        Start,
        Stop,
        Reassert,
        Dispose
    }

    private readonly record struct Command(CommandKind Kind, AwakeMode Mode, TimeSpan? Duration);
}
