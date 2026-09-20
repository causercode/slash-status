using TokenStatus.Core.Models;

namespace TokenStatus.Core.Services;

public sealed class SnapshotStore
{
    private readonly object _gate = new();
    private AppSnapshot _current;

    public SnapshotStore(AppSnapshot initial)
    {
        _current = initial;
    }

    public event Action<AppSnapshot>? Changed;

    public AppSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public AppSnapshot Update(Func<AppSnapshot, AppSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        AppSnapshot next;
        lock (_gate)
        {
            next = update(_current);
            _current = next;
        }

        Changed?.Invoke(next);
        return next;
    }
}
