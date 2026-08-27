namespace FgoPet.RenderingProbe.Diagnostics;

public sealed record ProbeSample(
    DateTimeOffset Timestamp,
    string Renderer,
    string Transparency,
    string ExpressionId,
    double Scale,
    double DpiScale,
    double SwitchMilliseconds,
    long WorkingSetBytes);

public sealed record SessionSummary(
    long MinimumWorkingSetBytes,
    long MaximumWorkingSetBytes,
    long FinalWorkingSetBytes,
    long PostGcWorkingSetBytes,
    int SwitchCount);
