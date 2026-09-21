using TokenStatus.Core.Models;
using TokenStatus.Core.Services;

namespace TokenStatus.Core.Tests;

public sealed class SnapshotStoreTests
{
    [Fact]
    public void ReturningTheCurrentSnapshotDoesNotNotify()
    {
        var store = new SnapshotStore(AppSnapshot.Initial(DateTimeOffset.UtcNow));
        var notifications = 0;
        store.Changed += _ => notifications++;

        var current = store.Current;
        var result = store.Update(_ => current);

        Assert.Same(current, result);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void ReturningAnEqualSnapshotDoesNotNotify()
    {
        var initial = AppSnapshot.Initial(DateTimeOffset.UtcNow);
        var store = new SnapshotStore(initial);
        var notifications = 0;
        store.Changed += _ => notifications++;

        var result = store.Update(snapshot => snapshot with { });

        Assert.Equal(initial, result);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void ARealChangeNotifiesExactlyOnceOutsideTheStoreLock()
    {
        var initial = AppSnapshot.Initial(DateTimeOffset.UtcNow);
        var store = new SnapshotStore(initial);
        var notifications = 0;
        AppSnapshot? observed = null;
        store.Changed += snapshot =>
        {
            notifications++;
            observed = store.Current;
            Assert.Same(snapshot, observed);
        };

        var result = store.Update(snapshot => snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(1) });

        Assert.Equal(1, notifications);
        Assert.Same(result, observed);
    }
}
