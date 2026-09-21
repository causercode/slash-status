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
        var changed = false;
        lock (_gate)
        {
            next = update(_current);
            ArgumentNullException.ThrowIfNull(next);
            if (!ReferenceEquals(next, _current) && !Equals(next, _current))
            {
                _current = next;
                changed = true;
            }
            else
            {
                next = _current;
            }
        }

        if (changed)
        {
            Changed?.Invoke(next);
        }

        return next;
    }
}
