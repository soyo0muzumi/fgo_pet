using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Reminders;

public sealed class RemindersPlugin : IFgoPetPlugin, IApplicationReadyObserver, IDisposable
{
    private readonly ReminderScheduler _scheduler;
    private readonly object _lifecycle = new();
    private CancellationToken _processStopping;
    private bool _started;
    private bool _stopped;
    private int _ready;
    public PluginManifest Manifest { get; } = new("reminders", "1.0.0", 1, []);
    public PluginContributions Contributions { get; }
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public RemindersPlugin(ReminderService owner, ReminderScheduler scheduler, Func<ToolScope, bool> scopeCurrent, TimeProvider clock)
    {
        _scheduler = scheduler;
        bool AvailableScope(ToolScope scope)
        {
            if (Volatile.Read(ref _ready) == 0 || _processStopping.IsCancellationRequested) throw new ReminderException("REMINDER_UNAVAILABLE");
            return scopeCurrent(scope);
        }
        var workflow = new Workflow(owner, AvailableScope, clock);
        Contributions = PluginContributions.Empty with { Tools = [new ListTool(owner, AvailableScope), new Command("reminder.create", workflow), new Command("reminder.cancel", workflow)] };
    }
    public ValueTask StartAsync(CancellationToken token)
    {
        lock (_lifecycle)
        {
            token.ThrowIfCancellationRequested();
            if (_stopped) throw new InvalidOperationException("REMINDER_PLUGIN_STOPPED");
            _processStopping = token; _started = true;
        }
        return ValueTask.CompletedTask;
    }
    public ValueTask OnApplicationReadyAsync(ApplicationReadiness readiness, CancellationToken token)
    {
        lock (_lifecycle)
        {
            token.ThrowIfCancellationRequested();
            if (!_started || _stopped || Volatile.Read(ref _ready) != 0 || !readiness.StorageAvailable) return ValueTask.CompletedTask;
            // Global storage migration / corruption handling must finish before this owner touches SQLite.
            _scheduler.Start(_processStopping);
            Volatile.Write(ref _ready, 1);
        }
        return ValueTask.CompletedTask;
    }
    public ValueTask StopAsync(CancellationToken token)
    {
        lock (_lifecycle) { _stopped = true; Volatile.Write(ref _ready, 0); }
        return _scheduler.DisposeAsync();
    }
    public ValueTask DisposeAsync() => StopAsync(default);
    public void Dispose()
    {
        lock (_lifecycle) { _stopped = true; Volatile.Write(ref _ready, 0); }
        _scheduler.Dispose();
    }

    private sealed class ListTool(ReminderService owner, Func<ToolScope, bool> current) : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new("reminder.list",
            "Page through this conversation's reminders, pending first. Submitted is not proof the user saw it; unknown must not be retried automatically. Pages reflect current state, not a frozen snapshot.",
            """{"type":"object","properties":{"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":50}},"additionalProperties":false}""", ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!current(invocation.Scope)) return ValueTask.FromResult(Failure("REMINDER_SCOPE_DENIED"));
                var args = invocation.Arguments;
                if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => p.Name is not ("offset" or "limit")) ||
                    args.EnumerateObject().Select(p => p.Name).Distinct().Count() != args.EnumerateObject().Count())
                    return ValueTask.FromResult(Failure("REMINDER_INVALID_ARGUMENTS"));
                var offset = args.TryGetProperty("offset", out var from) ? from.GetInt32() : 0;
                var limit = args.TryGetProperty("limit", out var count) ? count.GetInt32() : 20;
                if (offset < 0 || offset > int.MaxValue - 51 || limit is < 1 or > 50) return ValueTask.FromResult(Failure("REMINDER_INVALID_ARGUMENTS"));
                var items = owner.List(invocation.Scope, offset, limit);
                token.ThrowIfCancellationRequested();
                if (!current(invocation.Scope)) return ValueTask.FromResult(Failure("REMINDER_SCOPE_DENIED"));
                return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new
                {
                    items = items.Take(limit).Select(Project), truncated = items.Count > limit,
                    nextOffset = items.Count > limit ? (int?)(offset + limit) : null,
                    delivery = "While this app is running; on restart, pending reminders at most 24 hours late are attempted. Older reminders expire. No OS background scheduling."
                }, Json)));
            }
            catch (OperationCanceledException) { throw; }
            catch (ReminderException error) { return ValueTask.FromResult(Failure(error.Code)); }
            catch (Exception) { return ValueTask.FromResult(Failure("REMINDER_UNAVAILABLE")); }
        }
    }

    private sealed class Command(string name, Workflow workflow) : IToolProvider, IToolBusinessConfirmationProvider
    {
        public ToolDescriptor Descriptor { get; } = new(name, name == "reminder.create"
            ? "Create a one-shot local reminder after exact user confirmation. Use an explicit ISO date/time with offset and a matching IANA or Windows time zone. The app must be running; restart catches up within 24 hours."
            : "Cancel an exact pending reminder revision after user confirmation. A notification already dispatching or submitted cannot be recalled.", name == "reminder.create"
            ? """{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":255},"dueAt":{"type":"string","minLength":20,"maxLength":40},"timeZoneId":{"type":"string","minLength":1,"maxLength":128}},"required":["message","dueAt","timeZoneId"],"additionalProperties":false}"""
            : """{"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":128},"expectedRevision":{"type":"integer","minimum":1}},"required":["id","expectedRevision"],"additionalProperties":false}""", ToolEffect.Command);
        public ToolBusinessConfirmation PrepareConfirmation(ToolInvocation invocation, CancellationToken token) => workflow.Prepare(name, invocation, token);
        public void ValidateConfirmation(ToolInvocation invocation, ToolBusinessConfirmation confirmation, CancellationToken token) => workflow.Validate(name, invocation, confirmation, token);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(workflow.Execute(name, invocation, token));
    }

    private sealed class Workflow(ReminderService owner, Func<ToolScope, bool> current, TimeProvider clock)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);
        public ToolBusinessConfirmation Prepare(string name, ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                try
                {
                    var key = Check(invocation); var fingerprint = Fingerprint(name, invocation);
                    if (_drafts.TryGetValue(key, out var prior))
                    {
                        if (prior.Fingerprint != fingerprint) throw new ReminderException("REMINDER_IDEMPOTENCY_CONFLICT");
                        Validate(name, invocation, prior.Binding, token); return prior.Binding;
                    }
                    foreach (var stale in _drafts.Where(p => clock.GetUtcNow() - p.Value.At >= TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray()) _drafts.Remove(stale);
                    if (_drafts.Count >= 64) throw new ReminderException("REMINDER_CONFIRMATION_CAPACITY");
                    ReminderDraft? create = null; Reminder? cancel = null;
                    if (name == "reminder.create") create = owner.Validate(Parse(invocation.Arguments));
                    else
                    {
                        Only(invocation.Arguments, "id", "expectedRevision");
                        cancel = owner.Get(invocation.Scope, invocation.Arguments.GetProperty("id").GetString()!) ?? throw new ReminderException("REMINDER_NOT_FOUND");
                        if (cancel.Revision != invocation.Arguments.GetProperty("expectedRevision").GetInt64()) throw new ReminderException("REMINDER_VERSION_CONFLICT");
                        if (cancel.Status != ReminderStatus.Pending) throw new ReminderException("REMINDER_NOT_PENDING");
                    }
                    var binding = new ToolBusinessConfirmation("reminder-draft-" + Guid.NewGuid().ToString("N"), 1,
                        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { fingerprint, create, cancel }))))
                    {
                        PlainTextSummary = create is not null
                            ? Summary("创建提醒", create.Message, create.DueAt, create.TimeZoneId)
                            : Summary("取消提醒", cancel!.Message, cancel.RequestedTime, cancel.TimeZoneId)
                                + $"\n提醒 ID：{cancel.Id}\n版本：{cancel.Revision}"
                    };
                    _drafts[key] = new(binding, fingerprint, invocation.Scope, create, cancel, clock.GetUtcNow()); return binding;
                }
                catch (ReminderException error) { throw new ToolBusinessConfirmationException(error.Code); }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
                { throw new ToolBusinessConfirmationException("REMINDER_INVALID_ARGUMENTS"); }
            }
        }
        private static string Summary(string operation, string message, DateTimeOffset dueAt, string timeZoneId) =>
            $"{operation}\n提醒内容（引用数据）：{QuoteMessage(message)}\n当地时间：{dueAt.ToString("O", CultureInfo.InvariantCulture)}\n时区：{timeZoneId}\nUTC：{dueAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}"
            + "\n仅应用运行时通知；下次启动补查 24 小时内的到期提醒，更早的提醒过期。";

        private static string QuoteMessage(string message)
        {
            var quoted = JsonSerializer.Serialize(message, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var visible = new StringBuilder(quoted.Length);
            foreach (var c in quoted)
                if (char.GetUnicodeCategory(c) == UnicodeCategory.Format) visible.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else visible.Append(c);
            return visible.ToString();
        }

        public void Validate(string name, ToolInvocation invocation, ToolBusinessConfirmation confirmation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                try
                {
                    var key = Check(invocation);
                    if (!_drafts.TryGetValue(key, out var draft) || draft.Binding != confirmation || draft.Scope != invocation.Scope ||
                        draft.Fingerprint != Fingerprint(name, invocation) || clock.GetUtcNow() - draft.At >= TimeSpan.FromMinutes(10))
                        throw new ReminderException("REMINDER_CONFIRMATION_STALE");
                    if (draft.Result is not null || draft.Attempted) return;
                    if (draft.Create is { } create) owner.Validate(create);
                    if (draft.Cancel is { } cancel && owner.Get(invocation.Scope, cancel.Id) != cancel)
                        throw new ReminderException("REMINDER_VERSION_CONFLICT");
                }
                catch (ReminderException error) { throw new ToolBusinessConfirmationException(error.Code); }
            }
        }
        public ToolResult Execute(string name, ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Draft? draft = null;
                try
                {
                    if (invocation.ExecutionContext?.BusinessConfirmation is not { } confirmation) return Failure("REMINDER_CONFIRMATION_REQUIRED");
                    Validate(name, invocation, confirmation, token);
                    var key = Check(invocation); draft = _drafts[key];
                    if (draft.Result is not null) return draft.Result;
                    token.ThrowIfCancellationRequested();
                    draft.Attempted = true;
                    var result = draft.Create is { } create ? owner.Create(invocation.Scope, key, draft.Fingerprint, create)
                        : owner.Cancel(invocation.Scope, key, draft.Fingerprint, draft.Cancel!.Id, draft.Cancel.Revision);
                    return draft.Result = new(true, JsonSerializer.SerializeToElement(Project(result), Json)) { ExecutionState = ToolExecutionState.Committed };
                }
                catch (ToolBusinessConfirmationException error) { return Failure(error.Code); }
                catch (ReminderException error) { return Failure(error.Code); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return Failure(draft?.Attempted == true ? "REMINDER_COMMIT_UNKNOWN" : "REMINDER_UNAVAILABLE", draft?.Attempted == true ? ToolExecutionState.Unknown : ToolExecutionState.NotExecuted); }
            }
        }
        private string Check(ToolInvocation invocation)
        {
            if (!current(invocation.Scope)) throw new ReminderException("REMINDER_SCOPE_DENIED");
            if (invocation.ExecutionContext is not { } context || string.IsNullOrWhiteSpace(context.RunId) || string.IsNullOrWhiteSpace(context.CallId) ||
                string.IsNullOrWhiteSpace(context.IdempotencyKey) || context.IdempotencyKey.Length > 128)
                throw new ReminderException("REMINDER_CONTEXT_REQUIRED");
            if (Encoding.UTF8.GetByteCount(invocation.Arguments.GetRawText()) > 8192) throw new ReminderException("REMINDER_INVALID_ARGUMENTS");
            return context.IdempotencyKey;
        }
        private sealed record Draft(ToolBusinessConfirmation Binding, string Fingerprint, ToolScope Scope,
            ReminderDraft? Create, Reminder? Cancel, DateTimeOffset At)
        { public ToolResult? Result { get; set; } public bool Attempted { get; set; } }
    }
    private static ReminderDraft Parse(JsonElement args)
    {
        Only(args, "message", "dueAt", "timeZoneId");
        var raw = args.GetProperty("dueAt").GetString()!;
        // DateTimeOffset.TryParse accepts local-time-only strings; those are forbidden.
        if (raw.Length is < 20 or > 40 || !(raw.EndsWith('Z') || raw.Length >= 6 && raw[^3] == ':' && raw[^6] is '+' or '-') ||
            !DateTimeOffset.TryParseExact(raw, ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var due)) throw new ReminderException("REMINDER_TIME_INVALID");
        return new(args.GetProperty("message").GetString()!, due, args.GetProperty("timeZoneId").GetString()!);
    }
    private static void Only(JsonElement args, params string[] names)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ReminderException("REMINDER_INVALID_ARGUMENTS");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in args.EnumerateObject()) if (!names.Contains(prop.Name, StringComparer.Ordinal) || !seen.Add(prop.Name)) throw new ReminderException("REMINDER_INVALID_ARGUMENTS");
        if (seen.Count != names.Length) throw new ReminderException("REMINDER_INVALID_ARGUMENTS");
    }
    private static object Project(Reminder r) => new { r.Id, r.Message, r.DueAtUtc, r.TimeZoneId, r.RequestedTime, r.Status, r.Revision, r.CreatedAtUtc, r.UpdatedAtUtc, r.ErrorCode };
    private static string Fingerprint(string name, ToolInvocation invocation) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { name, invocation.Scope, invocation.Arguments })));
    private static ToolResult Failure(string code, ToolExecutionState state = ToolExecutionState.NotExecuted) => new(false, JsonSerializer.SerializeToElement(new { }), code) { ExecutionState = state };
}
