using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Core.Todo;

public enum TodoAgentCommitKind { Committed, AlreadyCommitted, Conflict, Unavailable }
public sealed record TodoAgentCommitResult(TodoAgentCommitKind Kind, string? ItemId = null, string? Version = null);
public sealed record TodoAgentCommit(ToolScope Scope, string IdempotencyKey, string Fingerprint,
    TodoItem? Expected, TodoItem Replacement);
public interface ITodoAgentCommandRepository
{
    TodoAgentCommitResult CommitAgentCommand(TodoAgentCommit command);
    /// <summary>Read a bound durable receipt without attempting or replaying a mutation.</summary>
    TodoAgentCommitResult? ReadAgentReceipt(ToolScope scope, string idempotencyKey, string fingerprint) => null;
}
public static class TodoItemVersion
{
    public static string Of(TodoItem item) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item)));
}
