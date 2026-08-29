using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class KnowledgeManifestReaderTests
{
    [Fact]
    public void Reader_excludes_pending_and_rejected_knowledge()
    {
        var entries = KnowledgeManifestReader.ReadOptional(Fixture("knowledge-mixed-approval"), "800100", "casual")!;

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal("approved", entry.Approval));
        Assert.DoesNotContain(entries, entry => entry.Id is "pending-note" or "rejected-note");
        Assert.Contains(entries, entry => entry.Id == "casual-story");
        Assert.DoesNotContain(entries, entry => entry.Id == "other-appearance");
    }

    [Fact]
    public void Malformed_optional_knowledge_falls_back_to_null()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", "knowledge-invalid");

        Assert.Null(KnowledgeManifestReader.ReadOptional(root, "800100", "casual"));
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", name);
}
