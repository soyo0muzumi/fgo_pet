using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Workspace;

/// <summary>Bounded read-only tools which delegate all path authority to the host workspace guard.</summary>
public static class WorkspaceReadTools
{
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxOutputScalars = 8000;
    private const int MaxScanEntries = 2048;
    private const int MaxScanDepth = 16;
    private const int MaxScanBytes = 8 * 1024 * 1024;
    private const int MaxResults = 100;
    private const int MaxOutputBytes = 32 * 1024;
    private static readonly TimeSpan MaxScanTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const int MaxGrepLineScalars = 3000;
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<IToolProvider> Create(IWorkspaceAccessGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return Array.AsReadOnly<IToolProvider>(
        [
            new ReadProvider(guard),
            new ListProvider(guard),
            new GlobProvider(guard),
            new GrepProvider(guard)
        ]);
    }

    private sealed class ReadProvider : Provider
    {
        public ReadProvider(IWorkspaceAccessGuard guard) : base(guard) { }

        public override ToolDescriptor Descriptor { get; } = new(
            "workspace.read",
            "Read a bounded text chunk from a file in the authorized workspace.",
            """
            {"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":1024},
            "offset":{"type":"integer","minimum":0,"maximum":1048576},
            "limit":{"type":"integer","minimum":1,"maximum":8000},
            "fileVersion":{"type":"string","minLength":64,"maxLength":64}},
            "required":["path"],"additionalProperties":false}
            """, ToolEffect.ReadOnly);

        protected override ToolResult Invoke(ToolInvocation invocation, CancellationToken token)
        {
            if (!TryArguments(invocation.Arguments, ["path", "offset", "limit", "fileVersion"], out var args)
                || !TryString(args, "path", required: true, 1, 1024, out var path)
                || !TryInt64(args, "offset", 0, MaxFileBytes, out var offset, defaultValue: 0)
                || !TryInt32(args, "limit", 1, MaxOutputScalars, out var limit, defaultValue: MaxOutputScalars))
                return Failure("TOOL_INVALID_ARGUMENTS");

            string? expectedVersion = null;
            if (args.TryGetValue("fileVersion", out var versionValue))
            {
                if (versionValue.ValueKind != JsonValueKind.String ||
                    !IsVersion(versionValue.GetString(), out expectedVersion)) return Failure("TOOL_INVALID_ARGUMENTS");
            }
            if (offset > 0 && expectedVersion is null) return Failure("TOOL_INVALID_ARGUMENTS");

            using var lease = Guard.Open(invocation.Scope, path!, WorkspacePathKind.File);
            token.ThrowIfCancellationRequested();
            var bytes = ReadGuardedFile(lease, MaxFileBytes, token, out var tooLarge);
            if (tooLarge) return Failure("WORKSPACE_FILE_TOO_LARGE");
            var text = WorkspaceTextCodec.Decode(bytes);
            if (expectedVersion is not null && !string.Equals(expectedVersion, text.Version, StringComparison.OrdinalIgnoreCase))
                return Failure("SOURCE_CHANGED");

            var scalarCount = text.Content.EnumerateRunes().Count();
            var actualOffset = (int)Math.Min(offset, scalarCount);
            var requestedCount = Math.Min(limit, scalarCount - actualOffset);
            ReadPayload? payload = null;
            var low = 0;
            var high = requestedCount;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var content = SliceScalars(text.Content, actualOffset, middle);
                var next = actualOffset + middle;
                var truncated = next < scalarCount;
                var candidate = new ReadPayload(DisplayPath(lease.Authority.RootPath, lease.FullPath), content,
                    text.Encoding, text.Version, truncated, truncated ? next : null);
                if (Fits(candidate))
                {
                    payload = candidate;
                    low = middle + 1;
                }
                else high = middle - 1;
            }
            if (payload is null) return Failure("TOOL_OUTPUT_LIMIT");

            token.ThrowIfCancellationRequested();
            var result = Success(payload);
            lease.Revalidate();
            token.ThrowIfCancellationRequested();
            return result;
        }
    }

    private sealed class ListProvider : Provider
    {
        public ListProvider(IWorkspaceAccessGuard guard) : base(guard) { }

        public override ToolDescriptor Descriptor { get; } = new(
            "workspace.list",
            "List bounded immediate file and directory entries in the authorized workspace.",
            """
            {"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":1024}},
            "additionalProperties":false}
            """, ToolEffect.ReadOnly);

        protected override ToolResult Invoke(ToolInvocation invocation, CancellationToken token)
        {
            if (!TryArguments(invocation.Arguments, ["path"], out var args) ||
                !TryString(args, "path", required: false, 1, 1024, out var path))
                return Failure("TOOL_INVALID_ARGUMENTS");

            path ??= ".";
            var items = new List<EntryPayload>();
            return Scan(Guard, invocation.Scope, path, recursive: false, token,
                (entry, _, state) =>
                {
                    var candidate = new EntryPayload(DisplayPath(state.Authority.RootPath, entry.FullPath), entry.Kind);
                    if (items.Count >= MaxResults)
                    {
                        state.Truncated = true;
                        return false;
                    }
                    var preview = new DirectoryPayload(DisplayPath(state.Authority.RootPath, state.BasePath),
                        [.. items, candidate], state.Truncated);
                    if (!Fits(preview))
                    {
                        state.Truncated = true;
                        return false;
                    }
                    items.Add(candidate);
                    return true;
                }, state => Success(new DirectoryPayload(DisplayPath(state.Authority.RootPath, state.BasePath),
                    items, state.Truncated)));
        }
    }

    private sealed class GlobProvider : Provider
    {
        public GlobProvider(IWorkspaceAccessGuard guard) : base(guard) { }

        public override ToolDescriptor Descriptor { get; } = new(
            "workspace.glob",
            "Find files and directories with a bounded workspace-relative glob pattern.",
            """
            {"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":1024},
            "pattern":{"type":"string","minLength":1,"maxLength":512}},
            "required":["pattern"],"additionalProperties":false}
            """, ToolEffect.ReadOnly);

        protected override ToolResult Invoke(ToolInvocation invocation, CancellationToken token)
        {
            if (!TryArguments(invocation.Arguments, ["path", "pattern"], out var args) ||
                !TryString(args, "path", required: false, 1, 1024, out var path) ||
                !TryString(args, "pattern", required: true, 1, 512, out var pattern))
                return Failure("TOOL_INVALID_ARGUMENTS");
            path ??= ".";

            Regex matcher;
            try { matcher = CompileGlob(pattern!); }
            catch (ArgumentException) { return Failure("TOOL_INVALID_PATTERN"); }

            var items = new List<EntryPayload>();
            return Scan(Guard, invocation.Scope, path, recursive: true, token,
                (entry, _, state) =>
                {
                    var relative = RelativePath(state.BasePath, entry.FullPath);
                    if (!matcher.IsMatch(relative)) return true;
                    var candidate = new EntryPayload(DisplayPath(state.Authority.RootPath, entry.FullPath), entry.Kind);
                    if (items.Count >= MaxResults)
                    {
                        state.Truncated = true;
                        return false;
                    }
                    var preview = new GlobPayload(DisplayPath(state.Authority.RootPath, state.BasePath), pattern!,
                        [.. items, candidate], state.Truncated);
                    if (!Fits(preview))
                    {
                        state.Truncated = true;
                        return false;
                    }
                    items.Add(candidate);
                    return true;
                }, state => Success(new GlobPayload(DisplayPath(state.Authority.RootPath, state.BasePath), pattern!,
                    items, state.Truncated)));
        }
    }

    private sealed class GrepProvider : Provider
    {
        public GrepProvider(IWorkspaceAccessGuard guard) : base(guard) { }

        public override ToolDescriptor Descriptor { get; } = new(
            "workspace.grep",
            "Search bounded text files in the authorized workspace with a literal or linear-time regular expression.",
            """
            {"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":1024},
            "pattern":{"type":"string","minLength":1,"maxLength":256},
            "mode":{"type":"string","enum":["literal","regex"]},"caseSensitive":{"type":"boolean"}},
            "required":["pattern"],"additionalProperties":false}
            """, ToolEffect.ReadOnly);

        protected override ToolResult Invoke(ToolInvocation invocation, CancellationToken token)
        {
            if (!TryArguments(invocation.Arguments, ["path", "pattern", "mode", "caseSensitive"], out var args) ||
                !TryString(args, "path", required: false, 1, 1024, out var path) ||
                !TryString(args, "pattern", required: true, 1, 256, out var pattern) ||
                !TryString(args, "mode", required: false, 1, 16, out var mode) ||
                !TryBoolean(args, "caseSensitive", out var caseSensitive, defaultValue: false))
                return Failure("TOOL_INVALID_ARGUMENTS");

            path ??= ".";
            mode ??= "literal";
            if (mode is not ("literal" or "regex")) return Failure("TOOL_INVALID_ARGUMENTS");
            Regex? regex = null;
            if (mode == "regex")
            {
                try { regex = CompileBoundedRegex(pattern!, caseSensitive); }
                catch (ArgumentException) { return Failure("TOOL_INVALID_PATTERN"); }
            }

            var matches = new List<GrepMatchPayload>();
            return Scan(Guard, invocation.Scope, path, recursive: true, token,
                (entry, lease, state) =>
                {
                    if (entry.Kind != "file") return true;
                    var remaining = (int)(MaxScanBytes - state.InputBytes);
                    byte[] bytes;
                    bool tooLarge;
                    try { bytes = ReadGuardedFile(lease, remaining, token, out tooLarge, state); }
                    catch (ScanTimeBudgetExceededException)
                    {
                        state.Truncated = true;
                        return false;
                    }
                    if (tooLarge)
                    {
                        state.Truncated = true;
                        return false;
                    }
                    state.InputBytes += bytes.Length;
                    var content = WorkspaceTextCodec.DecodeBounded(bytes, MaxScanBytes).Content;
                    using var reader = new StringReader(content);
                    var lineNumber = 0;
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        token.ThrowIfCancellationRequested();
                        if (state.Timer.Elapsed >= MaxScanTime)
                        {
                            state.Truncated = true;
                            return false;
                        }
                        lineNumber++;
                        if (!Matches(line, pattern!, mode!, caseSensitive, regex)) continue;

                        if (matches.Count >= MaxResults)
                        {
                            state.Truncated = true;
                            return false;
                        }
                        var scalarCount = line.EnumerateRunes().Count();
                        var boundedLine = scalarCount > MaxGrepLineScalars
                            ? SliceScalars(line, 0, MaxGrepLineScalars)
                            : line;
                        if (scalarCount > MaxGrepLineScalars) state.Truncated = true;
                        var candidate = new GrepMatchPayload(DisplayPath(state.Authority.RootPath, entry.FullPath),
                            lineNumber, boundedLine);
                        var preview = new GrepPayload(DisplayPath(state.Authority.RootPath, state.BasePath), pattern!, mode!,
                            [.. matches, candidate], state.Truncated);
                        if (!Fits(preview))
                        {
                            state.Truncated = true;
                            return false;
                        }
                        matches.Add(candidate);
                    }
                    return true;
                }, state => Success(new GrepPayload(DisplayPath(state.Authority.RootPath, state.BasePath),
                    pattern!, mode!, matches, state.Truncated)));
        }
    }

    private abstract class Provider(IWorkspaceAccessGuard guard) : IToolProvider, IToolResourceAuthorizationProvider
    {
        protected IWorkspaceAccessGuard Guard { get; } = guard;
        public abstract ToolDescriptor Descriptor { get; }
        public ToolResourceAuthorization GetAuthorization(ToolScope scope) => Guard.GetAuthorization(scope).Authorization;
        protected abstract ToolResult Invoke(ToolInvocation invocation, CancellationToken token);

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(Invoke(invocation, cancellationToken));
            }
            catch (OperationCanceledException) { return ValueTask.FromResult(Failure("TOOL_CANCELLED")); }
            catch (WorkspaceAccessException error) { return ValueTask.FromResult(Failure(error.Code)); }
            catch (WorkspaceTextDecodeException error) { return ValueTask.FromResult(Failure(error.Code)); }
            catch (RegexMatchTimeoutException) { return ValueTask.FromResult(Failure("TOOL_PATTERN_TIMEOUT")); }
            catch (JsonException) { return ValueTask.FromResult(Failure("TOOL_INVALID_ARGUMENTS")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            { return ValueTask.FromResult(Failure("WORKSPACE_READ_FAILED")); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
            { return ValueTask.FromResult(Failure("WORKSPACE_READ_FAILED")); }
        }
    }

    private static ToolResult Scan(IWorkspaceAccessGuard guard, ToolScope scope, string path, bool recursive,
        CancellationToken token, Func<ScannedEntry, IWorkspacePathLease, ScanBudget, bool> visit,
        Func<ScanBudget, ToolResult> finish)
    {
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        using var rootLease = guard.Open(scope, path, WorkspacePathKind.Directory);
        var state = new ScanBudget(rootLease.Authority, rootLease.FullPath, timer);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((rootLease.FullPath, 0));
        var stop = false;

        while (pending.Count > 0 && !stop)
        {
            token.ThrowIfCancellationRequested();
            if (state.Timer.Elapsed >= MaxScanTime)
            {
                state.Truncated = true;
                break;
            }
            var current = pending.Pop();
            IWorkspacePathLease directoryLease;
            var ownsLease = !string.Equals(current.Path, rootLease.FullPath, PathComparison);
            directoryLease = ownsLease
                ? guard.Open(scope, current.Path, WorkspacePathKind.Directory)
                : rootLease;
            try
            {
                directoryLease.Revalidate();
                if (recursive && current.Depth >= MaxScanDepth)
                {
                    state.Truncated = true;
                    continue;
                }

                using var entries = Directory.EnumerateFileSystemEntries(directoryLease.FullPath).GetEnumerator();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (state.Timer.Elapsed >= MaxScanTime)
                    {
                        state.Truncated = true;
                        stop = true;
                        break;
                    }
                    if (state.EntryCount >= MaxScanEntries)
                    {
                        state.Truncated = true;
                        stop = true;
                        break;
                    }
                    if (!entries.MoveNext()) break;

                    var fullPath = entries.Current;
                    var attributes = File.GetAttributes(fullPath);
                    var kind = (attributes & FileAttributes.Directory) != 0
                        ? WorkspacePathKind.Directory : WorkspacePathKind.File;
                    using var targetLease = guard.Open(scope, fullPath, kind);
                    targetLease.Revalidate();
                    state.EntryCount++;
                    var scanned = new ScannedEntry(fullPath, kind == WorkspacePathKind.Directory ? "directory" : "file");
                    var shouldContinue = visit(scanned, targetLease, state);
                    targetLease.Revalidate();
                    if (!shouldContinue)
                    {
                        stop = true;
                        break;
                    }
                    if (recursive && kind == WorkspacePathKind.Directory)
                    {
                        if (current.Depth + 1 >= MaxScanDepth)
                            state.Truncated = true;
                        else pending.Push((targetLease.FullPath, current.Depth + 1));
                    }
                }
                directoryLease.Revalidate();
            }
            finally
            {
                if (ownsLease) directoryLease.Dispose();
            }
        }

        token.ThrowIfCancellationRequested();
        var result = finish(state);
        rootLease.Revalidate();
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static byte[] ReadGuardedFile(IWorkspacePathLease lease, int maximumBytes, CancellationToken token,
        out bool tooLarge, ScanBudget? budget = null)
    {
        if (maximumBytes < 0) throw new WorkspaceAccessException("WORKSPACE_FILE_TOO_LARGE");
        token.ThrowIfCancellationRequested();
        using var input = new FileStream(lease.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        lease.ValidateOpenedFile(input);
        var length = input.Length;
        if (length > maximumBytes)
        {
            lease.Revalidate();
            tooLarge = true;
            return [];
        }

        var bytes = new byte[(int)length];
        var read = 0;
        while (read < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (budget is not null && budget.Timer.Elapsed >= MaxScanTime) throw new ScanTimeBudgetExceededException();
            var count = input.Read(bytes, read, Math.Min(64 * 1024, bytes.Length - read));
            if (count == 0) throw new WorkspaceAccessException("SOURCE_CHANGED");
            read += count;
        }
        if (budget is not null && budget.Timer.Elapsed >= MaxScanTime) throw new ScanTimeBudgetExceededException();
        if (input.Length != length) throw new WorkspaceAccessException("SOURCE_CHANGED");
        lease.Revalidate();
        tooLarge = false;
        return bytes;
    }

    private static Regex CompileBoundedRegex(string pattern, bool caseSensitive)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        return new Regex(pattern, options, RegexTimeout);
    }

    private static Regex CompileGlob(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Any(char.IsControl) ||
            normalized.Split('/').Any(segment => segment == ".." || segment is "[" or "]"))
            throw new ArgumentException("Unsupported glob pattern.");
        var regex = new StringBuilder("\\A");
        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];
            if (current == '*')
            {
                if (index + 1 < normalized.Length && normalized[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                    {
                        regex.Append("(?:.*/)?");
                        index++;
                    }
                    else regex.Append(".*");
                }
                else regex.Append("[^/]*");
            }
            else if (current == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(current.ToString()));
        }
        regex.Append("\\z");
        return new Regex(regex.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase |
            RegexOptions.NonBacktracking, RegexTimeout);
    }

    private static bool Matches(string line, string pattern, string mode, bool caseSensitive, Regex? regex) =>
        mode == "literal"
            ? line.IndexOf(pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0
            : regex!.IsMatch(line);

    private static bool ParseArguments(JsonElement value, HashSet<string> allowed, out Dictionary<string, JsonElement> args)
    {
        args = new(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !args.TryAdd(property.Name, property.Value)) return false;
        }
        return true;
    }

    private static bool TryArguments(JsonElement value, string[] allowed, out Dictionary<string, JsonElement> args) =>
        ParseArguments(value, allowed.ToHashSet(StringComparer.Ordinal), out args);

    private static bool TryString(Dictionary<string, JsonElement> args, string name, bool required,
        int minimumScalars, int maximumScalars, out string? result)
    {
        result = null;
        if (!args.TryGetValue(name, out var value)) return !required;
        if (value.ValueKind != JsonValueKind.String) return false;
        result = value.GetString();
        if (result is null) return false;
        var count = result.EnumerateRunes().Count();
        return count >= minimumScalars && count <= maximumScalars;
    }

    private static bool TryInt64(Dictionary<string, JsonElement> args, string name, long minimum, long maximum,
        out long result, long defaultValue)
    {
        result = defaultValue;
        if (!args.TryGetValue(name, out var value)) return true;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result) && result >= minimum && result <= maximum;
    }

    private static bool TryInt32(Dictionary<string, JsonElement> args, string name, int minimum, int maximum,
        out int result, int defaultValue)
    {
        result = defaultValue;
        if (!args.TryGetValue(name, out var value)) return true;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result) && result >= minimum && result <= maximum;
    }

    private static bool TryBoolean(Dictionary<string, JsonElement> args, string name, out bool result, bool defaultValue)
    {
        result = defaultValue;
        if (!args.TryGetValue(name, out var value)) return true;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = value.GetBoolean();
        return true;
    }

    private static bool IsVersion(string? value, out string? result)
    {
        result = value;
        return value is { Length: 64 } && value.All(character => character is >= 'A' and <= 'F' or >= 'a' and <= 'f' or >= '0' and <= '9');
    }

    private static string SliceScalars(string value, int offset, int count)
    {
        if (count <= 0) return string.Empty;
        var output = new StringBuilder();
        var skipped = 0;
        var appended = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (skipped < offset)
            {
                skipped++;
                continue;
            }
            if (appended >= count) break;
            output.Append(rune.ToString());
            appended++;
        }
        return output.ToString();
    }

    private static bool Fits<T>(T payload) => JsonSerializer.SerializeToUtf8Bytes(payload, OutputJson).Length <= MaxOutputBytes;
    private static ToolResult Success<T>(T payload) => new(true, JsonSerializer.SerializeToElement(payload, OutputJson));
    private static ToolResult Failure(string code)
    {
        var safeCode = code is { Length: > 0 and <= 128 } && code[0] is >= 'A' and <= 'Z' &&
            code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? code : "WORKSPACE_ACCESS_DENIED";
        return new(false, JsonSerializer.SerializeToElement(new { }, OutputJson), safeCode)
            { ExecutionState = ToolExecutionState.NotExecuted };
    }
    private static string RelativePath(string basePath, string fullPath) =>
        Path.GetRelativePath(basePath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    private static string DisplayPath(string rootPath, string fullPath) => RelativePath(rootPath, fullPath);

    private sealed record ReadPayload(string Path, string Content, string Encoding, string FileVersion,
        bool Truncated, long? NextOffset);
    private sealed record EntryPayload(string Path, string Kind);
    private sealed record DirectoryPayload(string Path, IReadOnlyList<EntryPayload> Items, bool Truncated);
    private sealed record GlobPayload(string Path, string Pattern, IReadOnlyList<EntryPayload> Items, bool Truncated);
    private sealed record GrepMatchPayload(string Path, int LineNumber, string Line);
    private sealed record GrepPayload(string Path, string Pattern, string Mode, IReadOnlyList<GrepMatchPayload> Items, bool Truncated);
    private readonly record struct ScannedEntry(string FullPath, string Kind);

    private sealed class ScanBudget(WorkspaceAuthorization authority, string basePath, Stopwatch timer)
    {
        public WorkspaceAuthorization Authority { get; } = authority;
        public string BasePath { get; } = basePath;
        public Stopwatch Timer { get; } = timer;
        public int EntryCount { get; set; }
        public long InputBytes { get; set; }
        public bool Truncated { get; set; }
    }

    private sealed class ScanTimeBudgetExceededException : Exception { }
}
