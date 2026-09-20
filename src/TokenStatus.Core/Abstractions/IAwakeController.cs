using TokenStatus.Core.Models;

namespace TokenStatus.Core.Abstractions;

public interface IAwakeController : IDisposable
{
    AwakeState Current { get; }
    event EventHandler<AwakeState>? Changed;
    void Start(AwakeMode mode, TimeSpan? duration);
    void Stop();
}
