using System.Text.Json.Serialization;

namespace FgoPet.AgentProtocol.Messages;

public sealed record OpenTaskRequest
{
    public OpenTaskRequest()
    {
    }

    public OpenTaskRequest(string sourceType, string sourceInstance, string taskId)
    {
        SourceType = sourceType;
        SourceInstance = sourceInstance;
        TaskId = taskId;
    }

    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("source_instance")]
    public string SourceInstance { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string TaskId { get; init; } = string.Empty;
}
