using FgoPet.Core.Packs;
using Xunit;

namespace FgoPet.Core.Tests.Packs;

public sealed class ContentContractTests
{
    [Fact]
    public void Persona_bundle_can_resolve_an_appearance_overlay()
    {
        var bundle = new PersonaBundle(
            "800100",
            "official.mash",
            "1.1.0",
            "persona-2",
            "核心人格",
            new[] { new PersonaAppearanceOverlay("casual", "便装时更放松", "前辈") });

        var overlay = bundle.FindAppearance("casual");

        Assert.NotNull(overlay);
        Assert.Equal("便装时更放松", overlay!.Text);
        Assert.Equal("前辈", overlay.DefaultAddress);
    }

    [Fact]
    public void Knowledge_entry_accepts_only_known_approval_states()
    {
        var approved = new KnowledgeEntry("story-1", "800100", "story", "冬木", "approved", KnowledgeKind.Story, "casual", "story://fuyuki/1");

        Assert.True(approved.IsApproved);
        Assert.Equal(KnowledgeKind.Story, approved.Kind);
        Assert.Equal("casual", approved.AppearanceId);
    }

    [Fact]
    public void Knowledge_entry_rejects_unknown_approval_state()
    {
        Assert.Throws<ArgumentException>(() =>
            new KnowledgeEntry("story-1", "800100", "story", "冬木", "trusted"));
    }
}
