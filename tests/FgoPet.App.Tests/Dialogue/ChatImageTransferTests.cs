using FgoPet.App.Dialogue;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ChatImageTransferTests
{
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
}
