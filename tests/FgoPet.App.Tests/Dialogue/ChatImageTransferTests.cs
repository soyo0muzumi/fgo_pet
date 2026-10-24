using FgoPet.App.Dialogue;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ChatImageTransferTests
{
    [Fact]
    public void Replay_tracking_is_bounded_and_reopens_capacity_after_lifetime()
    {
        var clock = new Clock(); using var transfer = new ChatImageTransfer(clock);
        for (var i = 0; i < 127; i++) transfer.Append(Guid.NewGuid().ToString("N"), "image/png", 0, "AQID", true);
        var current = Guid.NewGuid().ToString("N");
        transfer.Append(current, "image/png", 0, "AQID", false);
        Assert.Throws<InvalidOperationException>(() => transfer.Append(Guid.NewGuid().ToString("N"), "image/png", 0, "AQID", true));
        Assert.Equal(new byte[] { 1, 2, 3, 1, 2, 3 }, transfer.Append(current, "image/png", 3, "AQID", true));

        clock.Now += TimeSpan.FromSeconds(60);
        Assert.Equal(new byte[] { 1, 2, 3 }, transfer.Append(Guid.NewGuid().ToString("N"), "image/png", 0, "AQID", true));
    }

    [Fact]
    public void Expired_transfer_cannot_finish_or_restart_immediately()
    {
        var clock = new Clock(); using var transfer = new ChatImageTransfer(clock);
        var id = Guid.NewGuid().ToString("N");
        transfer.Append(id, "image/png", 0, "AQID", false);
        clock.Now += TimeSpan.FromSeconds(60);

        Assert.False(transfer.Active);
        Assert.Throws<InvalidOperationException>(() => transfer.Append(id, "image/png", 3, "AQID", true));
        Assert.Throws<InvalidOperationException>(() => transfer.Append(id, "image/png", 0, "AQID", true));
        Assert.False(transfer.Active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Superseded_transfer_cannot_discard_newer_transfer(int staleOffset)
    {
        using var transfer = new ChatImageTransfer();
        var oldId = Guid.NewGuid().ToString("N");
        var newId = Guid.NewGuid().ToString("N");
        transfer.Append(oldId, "image/png", 0, "AQID", false);
        transfer.Append(newId, "image/png", 0, "BAU=", false);

        Assert.Throws<InvalidOperationException>(() => transfer.Append(oldId, "image/png", staleOffset, "Bg==", true));

        Assert.True(transfer.Active);
        Assert.Equal(new byte[] { 4, 5, 6 }, transfer.Append(newId, "image/png", 2, "Bg==", true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completed_or_cancelled_transfer_id_cannot_restart(bool complete)
    {
        using var transfer = new ChatImageTransfer();
        var id = Guid.NewGuid().ToString("N");
        transfer.Append(id, "image/png", 0, "AQID", complete);
        transfer.Clear();

        Assert.Throws<InvalidOperationException>(() => transfer.Append(id, "image/png", 0, "AQID", true));
        Assert.False(transfer.Active);
    }

    [Fact]
    public void Disposed_transfer_cannot_be_reopened_by_late_first_chunk()
    {
        var transfer = new ChatImageTransfer();
        transfer.Dispose();

        Assert.Throws<InvalidOperationException>(() => transfer.Append(Guid.NewGuid().ToString("N"), "image/png", 0, "AQID", true));
        Assert.False(transfer.Active);
    }

    [Fact]
    public void Chunks_are_ordered_bounded_and_only_released_on_completion()
    {
        using var transfer = new ChatImageTransfer();
        var id = Guid.NewGuid().ToString("N");
        Assert.Null(transfer.Append(id, "image/png", 0, Convert.ToBase64String([1, 2, 3]), false));
        var result = transfer.Append(id, "image/png", 3, Convert.ToBase64String([4, 5]), true);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result);
        Assert.False(transfer.Active);
    }

    [Theory]
    [InlineData("image/svg+xml", 3)]
    [InlineData("image/png", 4)]
    public void Wrong_type_or_offset_discards_transfer(string mime, int offset)
    {
        using var transfer = new ChatImageTransfer();
        var id = Guid.NewGuid().ToString("N");
        transfer.Append(id, "image/png", 0, "AQID", false);
        Assert.Throws<InvalidOperationException>(() => transfer.Append(id, mime, offset, "BAU=", true));
        Assert.False(transfer.Active);
    }

    [Fact]
    public void Clearing_session_prevents_old_chunks_from_finishing()
    {
        using var transfer = new ChatImageTransfer();
        var id = Guid.NewGuid().ToString("N");
        transfer.Append(id, "image/png", 0, "AQID", false);
        transfer.Clear();
        Assert.Throws<InvalidOperationException>(() => transfer.Append(id, "image/png", 3, "BAU=", true));
        Assert.Throws<InvalidOperationException>(() => transfer.Append(id, "image/png", 0, new string('A', 50000), true));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
