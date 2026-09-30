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
