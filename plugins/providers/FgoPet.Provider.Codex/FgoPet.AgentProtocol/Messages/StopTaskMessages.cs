using System.Text.Json.Serialization;

namespace FgoPet.AgentProtocol.Messages;

/// <summary>Provider-neutral stop request. It carries opaque task identities only.</summary>
public sealed record StopTaskRequest
{
    public StopTaskRequest()
    {
    }

    public StopTaskRequest(
        string stopRequestId,
        string sourceType,
        string sourceInstanceId,
        string taskId,
        string dispatchRequestId)
    {
        StopRequestId = stopRequestId;
        SourceType = sourceType;
        SourceInstanceId = sourceInstanceId;
        TaskId = taskId;
        DispatchRequestId = dispatchRequestId;
    }

    [JsonPropertyName("stop_request_id")]
    public string StopRequestId { get; init; } = string.Empty;

    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("source_instance_id")]
    public string SourceInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("dispatch_request_id")]
    public string DispatchRequestId { get; init; } = string.Empty;
}

/// <summary>Adapter acknowledgement after the stop control has been accepted locally.</summary>
public sealed record StopAcknowledgementRequest(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("stop_request_ids")] IReadOnlyList<string> StopRequestIds);
