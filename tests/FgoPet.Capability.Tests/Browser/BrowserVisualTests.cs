using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Plugin.Browser;
using Xunit;

namespace FgoPet.Capability.Tests.Browser;

public sealed class BrowserVisualTests
{
    private static readonly ToolScope Scope = new("conversation", "role", null);
    private static Task<ToolResult> Invoke(ChromeBrowserPlugin plugin, string tool, object args) =>
        plugin.Contributions.Tools.Single(t=>t.Descriptor.Name==tool).InvokeAsync(new(Scope, JsonSerializer.SerializeToElement(args))
        { ExecutionContext = new("run", 1, "call", "key") }, default).AsTask();

    [Fact]
    public async Task Visual_capture_requires_grant_and_snapshot_and_never_accepts_path_or_script()
    {
        var settings = new ChromeBrowserSettings(true,"node","server");
        var wire = new Wire(); var sink = new Sink();
        await using var plugin = new ChromeBrowserPlugin(()=>settings,_=>true,wire,imageSink:sink);
        await plugin.StartAsync(default);
        Assert.Equal("BROWSER_VISUAL_NOT_ENABLED", (await Invoke(plugin,"browser.inspect_image",new { pageId=7,snapshotId="missing" })).ErrorCode);
        settings = settings with { VisualEnabled=true };
        var read = await Invoke(plugin,"browser.read",new {pageId=7});
        var snapshot = read.Payload.GetProperty("snapshotId").GetString();
        Assert.Equal("TOOL_INVALID_ARGUMENTS", (await Invoke(plugin,"browser.inspect_image",new {pageId=7,snapshotId=snapshot,filePath="secret"})).ErrorCode);
        var image = await Invoke(plugin,"browser.inspect_image",new {pageId=7,snapshotId=snapshot,uid="1_2"});
        Assert.True(image.Success); Assert.NotNull(image.Image); Assert.Equal(1,sink.Accepted);
        var capture = Assert.Single(wire.Calls.Where(c=>c.Name=="take_screenshot"));
        Assert.False(capture.Args.TryGetProperty("filePath",out _));
        Assert.False(capture.Args.GetProperty("fullPage").GetBoolean());
    }

    [Fact]
    public async Task Revocation_during_capture_discards_pixels()
    {
        var settings = new ChromeBrowserSettings(true,"node","server", VisualEnabled:true);
        var wire = new Wire(); var sink = new Sink();
        await using var plugin = new ChromeBrowserPlugin(()=>settings,_=>true,wire,imageSink:sink);
        await plugin.StartAsync(default);
        var read = await Invoke(plugin,"browser.read",new {pageId=7});
        var args = new {pageId=7,snapshotId=read.Payload.GetProperty("snapshotId").GetString()};
        wire.BeforeCapture = ()=>settings=settings with {VisualEnabled=false};
        Assert.Equal("BROWSER_AUTHORIZATION_CHANGED", (await Invoke(plugin,"browser.inspect_image",args)).ErrorCode);
        Assert.Equal(0,sink.Accepted);
    }

    [Fact]
    public async Task File_only_capture_is_not_followed_and_unknown_uid_never_reaches_capture()
    {
        var settings = new ChromeBrowserSettings(true,"node","server", VisualEnabled:true);
        var wire = new Wire { FileOnly = true }; var sink = new Sink();
        await using var plugin = new ChromeBrowserPlugin(()=>settings,_=>true,wire,imageSink:sink);
        await plugin.StartAsync(default);
        var read = await Invoke(plugin,"browser.read",new {pageId=7});
        var snapshot = read.Payload.GetProperty("snapshotId").GetString();
        Assert.Equal("BROWSER_ELEMENT_UNAVAILABLE", (await Invoke(plugin,"browser.inspect_image",new {pageId=7,snapshotId=snapshot,uid="99_9"})).ErrorCode);
        Assert.DoesNotContain(wire.Calls, c=>c.Name=="take_screenshot");
        Assert.Equal("BROWSER_IMAGE_UNAVAILABLE", (await Invoke(plugin,"browser.inspect_image",new {pageId=7,snapshotId=snapshot})).ErrorCode);
        Assert.Equal(0,sink.Accepted);
    }

    internal sealed class Sink : IBrowserImageSink
    {
        public int Accepted;
        public string? Check(ToolScope scope,string runId) => null;
        public BrowserImageAttachment Accept(ToolScope scope,string runId,byte[] bytes,string mime,Func<bool> authorized,CancellationToken token)
        { Assert.True(authorized()); Accepted++; return new(new(Guid.NewGuid().ToString("N")),1,1,bytes.Length); }
    }
    internal sealed class Wire : IChromeMcpConnection
    {
        public List<(string Name,JsonElement Args)> Calls = [];
        public Action? BeforeCapture;
        public bool FileOnly;
        public ValueTask<JsonElement> CallAsync(ChromeBrowserSettings settings,string name,JsonElement args,CancellationToken token)
        {
            Calls.Add((name,args));
            if(name=="take_snapshot") return ValueTask.FromResult(JsonSerializer.SerializeToElement(new {content=new[]{new{type="text",text="uid=1_2 image chart"}},structuredContent=new{snapshot=new{url="https://example.org/"}}}));
            if(name=="take_screenshot") {
                BeforeCapture?.Invoke();
                if (FileOnly) return ValueTask.FromResult(JsonSerializer.SerializeToElement(new {content=new[]{new{type="text",text="Screenshot saved at /untrusted/path.png"}}}));
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new{content=new[]{new{type="image",mimeType="image/png",data="AQID"}}}));
            }
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new{structuredContent=new{pages=new[]{new{id=7,url="https://example.org/",title="fixture",selected=true}}}}));
        }
        public ValueTask ResetAsync()=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
