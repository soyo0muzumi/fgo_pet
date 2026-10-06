using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Workspace;

public static class WorkspaceWriteTools
{
    public static IReadOnlyList<IToolProvider> Create(IWorkspaceAccessGuard guard) => [new Writer(guard, false), new Writer(guard, true)];
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed class Writer(IWorkspaceAccessGuard guard, bool edit) : IToolProvider, IToolResourceAuthorizationProvider
    {
        public ToolDescriptor Descriptor { get; } = new(edit ? "workspace.edit" : "workspace.write",
            edit ? "Replace exactly one matching text fragment, requiring the current file version." : "Create or replace a text file; replacing requires the current version and explicit approval.",
            edit ? """
              {"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":1024},
              "oldText":{"type":"string","minLength":1,"maxLength":32768},"newText":{"type":"string","maxLength":32768},
              "expectedVersion":{"type":"string","minLength":64,"maxLength":64}},"required":["path","oldText","newText","expectedVersion"],"additionalProperties":false}
              """ : """
              {"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":1024},
              "content":{"type":"string","maxLength":32768},"mustNotExist":{"type":"boolean"},
              "expectedVersion":{"type":"string","minLength":64,"maxLength":64}},"required":["path","content","mustNotExist"],"additionalProperties":false}
              """, ToolEffect.Command);
        public ToolResourceAuthorization GetAuthorization(ToolScope scope) => guard.GetAuthorization(scope).Authorization;

        public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            string? temporary = null;
            var enteredCommit = false;
            var committed = false;
            IWorkspacePathLease? lease = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (invocation.ExecutionContext is null) return Failure("TOOL_AUTHORIZATION_DENIED");
                var args = invocation.Arguments;
                var path = args.GetProperty("path").GetString()!;
                var create = !edit && args.GetProperty("mustNotExist").GetBoolean();
                var expected = args.TryGetProperty("expectedVersion", out var versionValue) ? versionValue.GetString() : null;
                if (create && expected is not null || !create && (expected is not { Length: 64 } ||
                    !expected.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9'))) return Failure("TOOL_INVALID_ARGUMENTS");
                lease = guard.Open(invocation.Scope, path, WorkspacePathKind.File, allowCreate: create);
                if (invocation.ExecutionContext.ResourceAuthorization != lease.Authority.Authorization) return Failure("WORKSPACE_SCOPE_CHANGED");
                if (create && lease.Exists) return Failure("CONFLICT");
                WorkspaceText? original = null;
                byte[]? originalBytes = null;
                if (!create)
                {
                    var info = new FileInfo(lease.FullPath);
                    if (info.Length > 1048576) return Failure("WORKSPACE_INPUT_TOO_LARGE");
                    originalBytes = await ReadBounded(lease, token);
                    original = WorkspaceTextCodec.Decode(originalBytes);
                    if (original.Version != expected) return Failure("CONFLICT");
                }
                var content = edit ? Edit(original!.Content, args.GetProperty("oldText").GetString()!, args.GetProperty("newText").GetString()!)
                    : args.GetProperty("content").GetString()!;
                if (content is null) return Failure("EDIT_MATCH_CONFLICT");
                var bytes = original is null ? new UTF8Encoding(false, true).GetBytes(content) : WorkspaceTextCodec.Encode(original, content);
                if (bytes.Length > 1048576) return Failure("WORKSPACE_OUTPUT_TOO_LARGE");
                temporary = Path.Combine(Path.GetDirectoryName(lease.FullPath)!, ".fgopet-write-" + Guid.NewGuid().ToString("N") + ".tmp");
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(bytes, token);
                    await output.FlushAsync(token);
                    output.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                lease.Revalidate();
                if (!create)
                {
                    var current = await ReadBounded(lease, token);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), SHA256.HashData(originalBytes!))) return Failure("CONFLICT");
                }
                token.ThrowIfCancellationRequested();
                lease.Revalidate();
                enteredCommit = true;
                if (create) File.Move(temporary, lease.FullPath, overwrite: false);
                else File.Replace(temporary, lease.FullPath, null);
                committed = true;
                temporary = null;
                return new(true, JsonSerializer.SerializeToElement(new { path = lease.FullPath,
                    fileVersion = Convert.ToHexString(SHA256.HashData(bytes)), committed = true }, Json))
                    { ExecutionState = ToolExecutionState.Committed };
            }
            catch (OperationCanceledException) { throw; }
            catch (WorkspaceAccessException error) { return Failure(error.Code); }
            catch (WorkspaceTextDecodeException error) { return Failure(error.Code); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                return Failure(enteredCommit ? "WORKSPACE_COMMIT_UNCONFIRMED" : "WORKSPACE_WRITE_FAILED",
                    committed ? ToolExecutionState.Committed : enteredCommit ? ToolExecutionState.Unknown : ToolExecutionState.NotExecuted);
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Exact owned scratch path only; effect state remains unchanged. */ }
                }
                lease?.Dispose();
            }
        }
        private static string? Edit(string content, string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText)) return null;
            var first = content.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0 || content.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0) return null;
            return string.Concat(content.AsSpan(0, first), newText, content.AsSpan(first + oldText.Length));
        }
        private static async Task<byte[]> ReadBounded(IWorkspacePathLease lease, CancellationToken token)
        {
            await using var input = new FileStream(lease.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            lease.ValidateOpenedFile(input);
            if (input.Length > 1048576) throw new WorkspaceAccessException("WORKSPACE_INPUT_TOO_LARGE");
            var bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, token);
            if (input.ReadByte() != -1) throw new WorkspaceAccessException("SOURCE_CHANGED");
            return bytes;
        }
        private static ToolResult Failure(string code, ToolExecutionState state = ToolExecutionState.NotExecuted) =>
            new(false, JsonSerializer.SerializeToElement(new { }), code) { ExecutionState = state };
    }
}
