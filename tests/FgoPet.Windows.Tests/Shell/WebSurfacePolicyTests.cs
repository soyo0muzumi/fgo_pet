using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class WebSurfacePolicyTests
{
    private readonly WebSurfacePolicy _policy = new("https://todo.fgopet.invalid/peek/index.html",
        ["getPeekSnapshot", "quickAdd", "setCompletion"]);

    [Theory]
    [InlineData("https://todo.fgopet.invalid/peek/index.html", true)]
    [InlineData("https://todo.fgopet.invalid/peek/site.css", true)]
    [InlineData("https://other.fgopet.invalid/peek/index.html", false)]
    [InlineData("file:///C:/private/settings.json", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Navigation_is_limited_to_the_local_virtual_host(string uri, bool expected) =>
        Assert.Equal(expected, _policy.AllowsNavigation(uri));

    [Theory]
    [InlineData("https://example.org/article#section", true, true)]
    [InlineData("http://example.org/article", true, true)]
    [InlineData("https://example.org/article", false, false)]
    [InlineData("https://todo.fgopet.invalid/peek/index.html", true, false)]
    [InlineData("https://user:secret@example.org/", true, false)]
    [InlineData("javascript:alert(1)", true, false)]
    [InlineData("file:///C:/private/document.html", true, false)]
    [InlineData("data:text/html,hello", true, false)]
    [InlineData("mailto:user@example.org", true, false)]
    [InlineData("/relative", true, false)]
    [InlineData("https://example.org/\nunsafe", true, false)]
    public void Only_a_user_clicked_http_link_can_be_handed_to_an_external_browser(string url, bool user, bool expected)
    {
        Assert.Equal(expected, _policy.TryGetExternalLink(url, user, out var external));
        Assert.Equal(expected, external is not null);
        if (expected) Assert.False(_policy.AllowsNavigation(url));
    }

    [Fact]
    public void Message_requires_the_current_main_document_and_a_known_typed_command()
    {
        const string current = "https://todo.fgopet.invalid/peek/index.html";
        const string valid = """{"type":"quickAdd","requestId":"req-1","payload":{"title":"fixture"}}""";

        var message = Assert.IsType<WebSurfaceMessage>(_policy.ValidateMessage(current, current, valid));
        Assert.Equal("quickAdd", message.Type);
        Assert.Null(_policy.ValidateMessage("https://other.fgopet.invalid/peek/index.html", current, valid));
        Assert.Null(_policy.ValidateMessage(current, "https://todo.fgopet.invalid/other.html", valid));
        Assert.Null(_policy.ValidateMessage(current, current,
            """{"type":"deleteEverything","requestId":"req-1","payload":{}}"""));
        Assert.Null(_policy.ValidateMessage(current, current,
            """{"type":"quickAdd","requestId":"req-1","payload":"not-an-object"}"""));
        Assert.Null(_policy.ValidateMessage(current, current, new string('x', 65537)));
    }
}
