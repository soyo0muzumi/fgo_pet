namespace FgoPet.Extensibility;

/// <summary>Native host settings port. No tool or web content can grant visual access.</summary>
public interface IBrowserVisualAccess
{
    bool IsAvailable { get; }
    bool Enabled { get; }
    bool TrySetEnabled(bool enabled);
}

/// <summary>Host-owned image intake. A browser observation cannot grant authority to itself.</summary>
public interface IBrowserImageSink
{
    string? Check(ToolScope scope, string runId);
    BrowserImageAttachment Accept(ToolScope scope, string runId, byte[] bytes, string mimeType,
        Func<bool> authorizationCurrent, CancellationToken token);
}

public sealed record BrowserImageAttachment(ToolImageReference Reference, int Width, int Height, int ByteLength);
public sealed class BrowserImageException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
