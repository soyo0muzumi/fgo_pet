using FgoPet.App.Portraits;
using FgoPet.Core.Geometry;
using FgoPet.Core.Portraits;
using Xunit;

namespace FgoPet.App.Tests.Portraits;

public sealed class PortraitSnapshotCacheTests
{
    private static readonly PortraitSelection A = new("pkg", "a");
    private static readonly PortraitSelection B = new("pkg", "b");
    private static readonly PortraitSelection C = new("pkg", "c");

    private static PortraitSnapshot Snapshot(PortraitSelection selection) => new(
        new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>(),
        "body",
        "expr",
        new Dictionary<string, byte[]>(),
        GeometryFixture.CoreMash);

    [Fact]
    public void TryGet_returns_null_for_missing()
    {
        var cache = new PortraitSnapshotCache();
        Assert.Null(cache.TryGet(A));
    }

    [Fact]
    public void Put_and_TryGet_roundtrip_two_snapshots()
    {
        var cache = new PortraitSnapshotCache();
        cache.Put(A, Snapshot(A));
        cache.Put(B, Snapshot(B));

        Assert.NotNull(cache.TryGet(A));
        Assert.NotNull(cache.TryGet(B));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Third_put_evicts_the_oldest_snapshot()
    {
        var cache = new PortraitSnapshotCache();
        cache.Put(A, Snapshot(A));
        cache.Put(B, Snapshot(B));
        cache.Put(C, Snapshot(C));

        Assert.Null(cache.TryGet(A));
        Assert.NotNull(cache.TryGet(B));
        Assert.NotNull(cache.TryGet(C));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Replacing_a_key_makes_it_recent()
    {
        var cache = new PortraitSnapshotCache();
        cache.Put(A, Snapshot(A));
        cache.Put(B, Snapshot(B));
        cache.Put(A, Snapshot(A)); // A becomes most recent
        cache.Put(C, Snapshot(C));

        Assert.NotNull(cache.TryGet(A));
        Assert.Null(cache.TryGet(B));
        Assert.NotNull(cache.TryGet(C));
    }

    [Fact]
    public void Putting_the_same_key_twice_does_not_grow()
    {
        var cache = new PortraitSnapshotCache();
        cache.Put(A, Snapshot(A));
        cache.Put(A, Snapshot(A));

        Assert.Equal(1, cache.Count);
    }
}

internal static class GeometryFixture
{
    public static readonly PortraitSourceGeometry CoreMash = new(303, 603, 13, 0, 256, 240, 151, 360);
}