namespace FgoPet.Core.Dialogue;

public enum TokenCountKind { Estimated, ProviderUsage }
public sealed record ChatUsage
{
    public ChatUsage(int inputTokens, int outputTokens)
    {
        if (inputTokens < 0 || outputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }
    public int InputTokens { get; }
    public int OutputTokens { get; }
}
public sealed record TokenMeasurement(int InputTokens, TokenCountKind Kind, string Fingerprint);
public interface IRequestTokenMeter
{
    TokenMeasurement Measure(ModelRouteKey route, ChatRequest request);
    void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage);
    void Invalidate(ModelRouteKey route);
}
