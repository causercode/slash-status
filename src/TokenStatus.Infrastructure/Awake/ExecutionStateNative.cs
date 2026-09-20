using System.Runtime.InteropServices;

namespace TokenStatus.Infrastructure.Awake;

[Flags]
public enum ExecutionState : uint
{
    Continuous = 0x80000000,
    SystemRequired = 0x00000001,
    DisplayRequired = 0x00000002
}

public interface IExecutionStateNative
{
    bool Set(ExecutionState state);
}

public sealed class ExecutionStateNative : IExecutionStateNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    public bool Set(ExecutionState state) => SetThreadExecutionState(state) != 0;
}
