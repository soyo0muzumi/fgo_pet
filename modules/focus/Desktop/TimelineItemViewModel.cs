using FgoPet.Core.Timeline;

namespace FgoPet.App.Panels;

/// <summary>One Today row: bounded time-formatted line for the timeline list.</summary>
public sealed record TimelineItemViewModel(string TimeText, string SummaryText, string? BondLevelText);
