using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using FgoPet.Core.Secrets;
using FgoPet.Extensibility;
using FgoPet.Plugin.Qq.Transport;

namespace FgoPet.Plugin.Qq;

/// <summary>One locally paired QQ user, one explicitly bound desktop conversation, one message consumer.</summary>
public sealed class QqChatPlugin : IFgoPetPlugin, IApplicationReadyObserver, IDisposable
{
    private readonly QqStateStore _store;
    private readonly ICredentialStore _credentials;
    private readonly Func<IQqBotTransport> _transportFactory;
    private readonly Func<CancellationToken, Task<QqConversationBinding?>> _captureBinding;
    private readonly Func<QqConversationBinding, string, CancellationToken, Task<QqConversationReply>> _exchange;
    private readonly TimeProvider _time;
    private readonly Action<QqDiagnostic>? _diagnostics;
    private readonly SemaphoreSlim _configuration = new(1, 1);
    private readonly object _gate = new();
    private QqStoredState _state = new();
    private Pairing? _pairing;
    private Session? _session;
    private CancellationToken _stopping;
    private string _status = "Disabled";
    private string? _error;
    private bool _stateUnavailable;
    private bool _started;
    private bool _ready;
    private bool _closed;
    private Task? _shutdown;

    public QqChatPlugin(QqStateStore store, ICredentialStore credentials, Func<IQqBotTransport> transportFactory,
        Func<CancellationToken, Task<QqConversationBinding?>> captureBinding,
        Func<QqConversationBinding, string, CancellationToken, Task<QqConversationReply>> exchange,
        TimeProvider? timeProvider = null, Action<QqDiagnostic>? diagnostics = null)
    {
        _store = store; _credentials = credentials; _transportFactory = transportFactory;
        _captureBinding = captureBinding; _exchange = exchange;
        _time = timeProvider ?? TimeProvider.System; _diagnostics = diagnostics;
        try { _state = store.Load(); }
        catch (Exception) { _stateUnavailable = true; _status = "Faulted"; _error = "QQ_STATE_UNAVAILABLE"; }
    }

    public PluginManifest Manifest { get; } = new("qq", "1.0.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty;
    public static string CredentialTarget(string appId) => QqValidation.AppId(appId)
        ? "FgoPet:Qq:" + appId + ":AppSecret" : throw new ArgumentException("QQ_APP_ID_INVALID");

    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        lock (_gate) { if (!_closed) { _stopping = stoppingToken; _started = true; } }
        return ValueTask.CompletedTask;
    }

    public async ValueTask OnApplicationReadyAsync(ApplicationReadiness readiness, CancellationToken token)
    {
        await _configuration.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _ready = readiness.StorageAvailable;
                if (!_ready) { _status = "Faulted"; _error = "QQ_STORAGE_UNAVAILABLE"; }
                else if (_state.Enabled && !_stateUnavailable) StartConnection();
            }
        }
        finally { _configuration.Release(); }
    }

    public async Task<QqChannelSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        QqChannelSnapshot snapshot;
        lock (_gate) snapshot = Snapshot(false);
        var hasSecret = snapshot.AppId.Length != 0 &&
            await _credentials.ExistsAsync(CredentialTarget(snapshot.AppId), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return snapshot with { HasSecret = hasSecret };
    }

    public Task<QqChannelSnapshot> ConfigureAsync(string appId, string? appSecret, CancellationToken token) =>
        ConfigureOperationAsync(async () =>
        {
            if (!QqValidation.AppId(appId) || appSecret is { Length: > 4096 } ||
                appSecret?.Any(char.IsControl) == true) { Error("QQ_CONFIG_INVALID"); return; }
            var target = CredentialTarget(appId);
            string currentAppId;
            lock (_gate) currentAppId = _state.AppId;
            if (string.IsNullOrWhiteSpace(appSecret) && (currentAppId != appId ||
                !await _credentials.ExistsAsync(target, token).ConfigureAwait(false)))
            { Error("QQ_SECRET_REQUIRED"); return; }
            await StopConnectionAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(appSecret)) await _credentials.SaveAsync(target, appSecret, token).ConfigureAwait(false);
            string previous;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                previous = _state.AppId;
                Persist(previous == appId ? _state with { Enabled = false } : new(AppId: appId), token);
                _pairing = null; _status = "Disabled"; _error = null;
            }
            if (previous.Length != 0 && previous != appId)
                await _credentials.DeleteAsync(CredentialTarget(previous), token).ConfigureAwait(false);
        }, token);

    public Task<QqChannelSnapshot> BeginPairingAsync(CancellationToken token) => ConfigureOperationAsync(async () =>
    {
        if (!CanConnect()) return;
        var binding = await _captureBinding(token).ConfigureAwait(false);
        if (!QqValidation.Binding(binding)) { Error("QQ_CONVERSATION_REQUIRED"); return; }
        string appId;
        lock (_gate) appId = _state.AppId;
        if (!QqValidation.AppId(appId) || !await _credentials.ExistsAsync(CredentialTarget(appId), token).ConfigureAwait(false))
        { Error("QQ_SECRET_REQUIRED"); return; }
        await StopConnectionAsync().ConfigureAwait(false);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            // Pairing always requires local interaction; an unknown sender is never auto-enrolled.
            Persist(_state with { Enabled = false, OwnerOpenId = null, Binding = null }, token);
            _pairing = new("FGO-" + RandomNumberGenerator.GetInt32(100_000_000).ToString("D8"),
                _time.GetUtcNow().AddMinutes(5), binding!);
            StartConnection();
        }
    }, token);

    public Task<QqChannelSnapshot> SetEnabledAsync(bool enabled, CancellationToken token) => ConfigureOperationAsync(async () =>
    {
        if (enabled && !CanConnect()) return;
        lock (_gate)
            if (enabled && (_state.OwnerOpenId is null || !QqValidation.Binding(_state.Binding)))
            { _error = "QQ_PAIRING_REQUIRED"; return; }
        await StopConnectionAsync().ConfigureAwait(false);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            Persist(_state with { Enabled = enabled }, token);
            _pairing = null; _error = null; _status = "Disabled";
            if (enabled) StartConnection();
        }
    }, token);

    public Task<QqChannelSnapshot> BindCurrentConversationAsync(CancellationToken token) => ConfigureOperationAsync(async () =>
    {
        var binding = await _captureBinding(token).ConfigureAwait(false);
        if (!QqValidation.Binding(binding)) { Error("QQ_CONVERSATION_REQUIRED"); return; }
        lock (_gate) if (_state.OwnerOpenId is null) { _error = "QQ_PAIRING_REQUIRED"; return; }
        await StopConnectionAsync().ConfigureAwait(false);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            Persist(_state with { Binding = binding }, token);
            _pairing = null; _error = null;
            if (_state.Enabled) StartConnection();
        }
    }, token);

    public async Task<QqChannelSnapshot> ResetAsync(CancellationToken token)
    {
        await _configuration.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await StopConnectionAsync().ConfigureAwait(false);
            string appId;
            lock (_gate) appId = _state.AppId;
            if (QqValidation.AppId(appId)) await _credentials.DeleteAsync(CredentialTarget(appId), token).ConfigureAwait(false);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                Persist(new(), token);
                _stateUnavailable = false; _pairing = null; _error = null; _status = "Disabled";
            }
            return await GetSnapshotAsync(token).ConfigureAwait(false);
        }
        finally { _configuration.Release(); }
    }

    private async Task<QqChannelSnapshot> ConfigureOperationAsync(Func<Task> operation, CancellationToken token)
    {
        await _configuration.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            bool unavailable;
            lock (_gate) unavailable = _stateUnavailable || _closed;
            if (!unavailable) await operation().ConfigureAwait(false);
            return await GetSnapshotAsync(token).ConfigureAwait(false);
        }
        finally { _configuration.Release(); }
    }

    private bool CanConnect()
    {
        lock (_gate)
        {
            if (_started && _ready && !_closed && !_stopping.IsCancellationRequested) return true;
            _error = "QQ_HOST_NOT_READY"; return false;
        }
    }

    // Called under _gate, and only after the preceding session has drained.
    private void StartConnection()
    {
        if (!_started || !_ready || _closed || _stopping.IsCancellationRequested || _stateUnavailable || _session is not null) return;
        try
        {
            var session = new Session(_transportFactory(), CancellationTokenSource.CreateLinkedTokenSource(_stopping), _state.AppId);
            _session = session; _status = "Connecting"; _error = null;
            session.Completion = Task.Run(() => RunSessionAsync(session));
        }
        catch (Exception) { _status = "Faulted"; _error = "QQ_CONNECT_FAILED"; }
    }

    private async Task RunSessionAsync(Session session)
    {
        var token = session.Cancellation.Token;
        Task receiver = Task.CompletedTask, consumer = Task.CompletedTask, expiry = Task.CompletedTask;
        try
        {
            receiver = session.Transport.RunAsync(new(session.AppId, CredentialTarget(session.AppId)),
                (message, _) =>
                {
                    // This callback never waits for a model or reply; heartbeats remain independent.
                    lock (_gate)
                        if (!ReferenceEquals(_session, session) ||
                            _pairing is null && (!_state.Enabled || _state.OwnerOpenId != message.UserOpenId))
                            return Task.CompletedTask;
                    if (!session.Inbox.Writer.TryWrite(message)) Trace(message.MessageId, "queue", "QQ_QUEUE_FULL");
                    return Task.CompletedTask;
                }, status =>
                {
                    lock (_gate) if (ReferenceEquals(_session, session))
                    { _status = status.State.ToString(); _error = status.ErrorCode; }
                    Trace("connection", "transport", status.ErrorCode);
                }, token);
            consumer = ConsumeAsync(session, token);
            expiry = WatchPairingAsync(session, token);
            var completed = await Task.WhenAny(receiver, consumer, expiry).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Error("QQ_CONNECTION_FAILED"); Trace("connection", "fault", "QQ_CONNECTION_FAILED"); }
        finally
        {
            try { await session.Cancellation.CancelAsync().ConfigureAwait(false); }
            catch (Exception) { Trace("connection", "cancel", "QQ_STOP_FAILED"); }
            session.Inbox.Writer.TryComplete();
            try { await Task.WhenAll(receiver, consumer, expiry).ConfigureAwait(false); }
            catch (Exception) { /* Each boundary exposes only fixed, non-sensitive error codes. */ }
            try { await session.Transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { Trace("connection", "stop", "QQ_STOP_FAILED"); }
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    if (_status != "Faulted") _status = _error is null ? "Stopped" : "Faulted";
                }
            }
            session.Cancellation.Dispose();
        }
    }

    private async Task WatchPairingAsync(Session session, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _time, token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_session, session)) return;
                if (_pairing is not null && _pairing.ExpiresAt <= _time.GetUtcNow())
                { _pairing = null; _error = "QQ_PAIRING_EXPIRED"; return; }
            }
        }
    }

    private async Task ConsumeAsync(Session session, CancellationToken token)
    {
        await foreach (var message in session.Inbox.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try { await ProcessAsync(session, message, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Fail closed if receipt/pairing persistence fails, before another message is accepted.
                Error("QQ_MESSAGE_FAILED"); Trace(message.MessageId, "dispatch", "QQ_MESSAGE_FAILED");
                throw;
            }
        }
    }

    private async Task ProcessAsync(Session session, QqC2cMessage message, CancellationToken token)
    {
        var now = _time.GetUtcNow();
        if (!QqValidation.Identifier(message.MessageId) || !QqValidation.Identifier(message.UserOpenId) ||
            (message.IsUnsupportedMedia ? message.Content != string.Empty
                : message.Content is not { Length: > 0 and <= 8192 } || string.IsNullOrWhiteSpace(message.Content)) ||
            message.Timestamp < now.AddMinutes(-5) || message.Timestamp > now.AddMinutes(1)) return;
        Pairing? pairing;
        lock (_gate) pairing = _pairing;
        if (pairing is not null)
        {
            if (message.IsUnsupportedMedia) return;
            if (pairing.ExpiresAt <= now) return;
            var candidate = message.Content.Trim();
            if (!string.Equals(candidate, pairing.Code, StringComparison.Ordinal))
            {
                if (candidate.StartsWith("FGO-", StringComparison.Ordinal))
                    lock (_gate) if (ReferenceEquals(_pairing, pairing) && ++pairing.Attempts >= 10)
                    { _pairing = null; _error = "QQ_PAIRING_ATTEMPTS_EXCEEDED"; session.Cancellation.Cancel(); }
                return;
            }
            var current = await _captureBinding(token).ConfigureAwait(false);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_pairing, pairing) || !ReferenceEquals(_session, session)) return;
                if (pairing.ExpiresAt <= _time.GetUtcNow())
                { _pairing = null; _error = "QQ_PAIRING_EXPIRED"; session.Cancellation.Cancel(); return; }
                if (pairing.Binding != current) { _error = "QQ_SCOPE_CHANGED"; return; }
                var retained = (_state.Receipts ?? []).Where(r => r.AcceptedAt > now.AddMinutes(-10)).ToArray();
                if (retained.Length >= 1024) { _error = "QQ_RECEIPT_CAPACITY"; return; }
                Persist(_state with { Enabled = true, OwnerOpenId = message.UserOpenId, Binding = current,
                    Receipts = [.. retained, new(ReceiptId(session.AppId, message), now)] }, token);
                _pairing = null; _error = null;
            }
            Trace(message.MessageId, "paired");
            await ReplyAsync(session, message, "已连接。你可以在这里继续和我聊天，电脑上的对话也会同步更新。", token).ConfigureAwait(false);
            return;
        }

        QqConversationBinding binding;
        var notifyUnsupportedMedia = false;
        var receiptId = ReceiptId(session.AppId, message);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session, session) || !_state.Enabled || _state.OwnerOpenId != message.UserOpenId ||
                _state.Binding is not { } bound || message.Content.StartsWith("FGO-", StringComparison.Ordinal)) return;
            binding = bound;
            var retained = (_state.Receipts ?? []).Where(r => r.AcceptedAt > now.AddMinutes(-10)).ToArray();
            if (retained.Any(r => r.IdHash == receiptId)) { Trace(message.MessageId, "duplicate"); return; }
            if (retained.Length >= 1024) { _error = "QQ_RECEIPT_CAPACITY"; return; }
            // Commit admission before calling the model. An uncertain/crashed turn is never auto-replayed.
            Persist(_state with { Receipts = [.. retained, new(receiptId, now)] }, token);
            if (message.IsUnsupportedMedia && (session.LastUnsupportedNoticeAt is null
                || now - session.LastUnsupportedNoticeAt.Value >= TimeSpan.FromSeconds(30)))
            {
                session.LastUnsupportedNoticeAt = now;
                notifyUnsupportedMedia = true;
            }
        }
        if (message.IsUnsupportedMedia)
        {
            Trace(message.MessageId, "unsupported", "QQ_MEDIA_NOT_SUPPORTED");
            if (notifyUnsupportedMedia)
                await ReplyAsync(session, message, "当前 QQ 入口只支持文字，这条消息的图片或附件未被读取。请发送纯文字，或在桌面聊天中选图。", token).ConfigureAwait(false);
            return;
        }
        Trace(message.MessageId, "dispatch");
        QqConversationReply reply;
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(token);
        turn.CancelAfter(TimeSpan.FromMinutes(4));
        try { reply = await _exchange(binding, message.Content, turn.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { reply = new("这次回复超时了，请稍后再试。", "QQ_TURN_TIMEOUT"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { reply = new("这次对话没有完成，请查看电脑上的对话状态。", "QQ_DIALOGUE_FAILED"); }
        token.ThrowIfCancellationRequested();
        Trace(message.MessageId, "completed", reply.ErrorCode);
        var text = string.IsNullOrWhiteSpace(reply.Text) ? SafeReply(reply.ErrorCode) : reply.Text;
        await ReplyAsync(session, message, text, token).ConfigureAwait(false);
    }

    private async Task ReplyAsync(Session session, QqC2cMessage message, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
            if (!ReferenceEquals(_session, session) || !_state.Enabled || _state.OwnerOpenId != message.UserOpenId)
                return;
        if (text.Length > QqGatewayProtocol.MaxTextLength)
        {
            const string suffix = "\n\n（回复较长，完整内容已保存在电脑上的对话中。）";
            var length = QqGatewayProtocol.MaxTextLength - suffix.Length;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            text = text[..length] + suffix;
        }
        var result = await session.Transport.ReplyTextAsync(message, text, token).ConfigureAwait(false);
        if (!result.Succeeded) Error(result.ErrorCode ?? "QQ_SEND_FAILED");
        Trace(message.MessageId, "delivery", result.ErrorCode);
    }

    private async Task StopConnectionAsync()
    {
        Session? session;
        lock (_gate) { session = _session; _session = null; _pairing = null; _status = "Stopped"; }
        if (session is null) return;
        // Cancel immediately, then drain before allowing a new identity, binding or credential.
        await CancelAndDrainAsync(session).ConfigureAwait(false);
    }

    private async Task CancelAndDrainAsync(Session session)
    {
        try { await session.Cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception) { Trace("connection", "cancel", "QQ_STOP_FAILED"); }
        await session.Completion.ConfigureAwait(false);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        lock (_gate) return new ValueTask(_shutdown!);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return;
            _closed = true; _status = "Stopped"; _pairing = null;
            // Detach admission synchronously. Cancellation callbacks and drain must not block WPF disposal.
            var session = _session;
            _session = null;
            _shutdown = Task.Run(async () =>
            {
                if (session is not null) await CancelAndDrainAsync(session).ConfigureAwait(false);
                // An already-authorized configuration operation may own a detached session.
                await _configuration.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await StopConnectionAsync().ConfigureAwait(false); }
                finally { _configuration.Release(); }
            });
        }
    }
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);

    private void Persist(QqStoredState state, CancellationToken token) { _store.Save(state, token); _state = state; }
    private void Error(string code) { lock (_gate) _error = code; }
    private QqChannelSnapshot Snapshot(bool hasSecret) => new(_state.AppId, hasSecret, _state.Enabled,
        _state.OwnerOpenId is not null, _status, _error, _state.Binding, _pairing?.Code, _pairing?.ExpiresAt);
    private static string ReceiptId(string appId, QqC2cMessage message) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appId + "\0" + message.UserOpenId + "\0" + message.MessageId)));
    private void Trace(string id, string stage, string? error = null)
    {
        try { _diagnostics?.Invoke(new(_time.GetUtcNow(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12], stage, error)); }
        catch (Exception) { /* Diagnostics cannot change admission or delivery. */ }
    }
    private static string SafeReply(string? code) => code switch
    {
        "CHAT_EXTERNAL_BUSY" => "我正在处理上一条消息，请稍后再发。",
        "CHAT_EXTERNAL_SCOPE_CHANGED" => "电脑上的会话已经切换，请在 QQ 设置里重新绑定当前对话。",
        "CHAT_EXTERNAL_CONFIGURATION_REQUIRED" => "请先在电脑上完成聊天模型配置。",
        "CHAT_EXTERNAL_CLOSED" => "桌面对话暂时不可用，请检查电脑上的运行状态。",
        _ => "这次对话没有完成，请查看电脑上的对话状态。"
    };
    private sealed class Pairing(string code, DateTimeOffset expiresAt, QqConversationBinding binding)
    {
        internal string Code { get; } = code;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal QqConversationBinding Binding { get; } = binding;
        internal int Attempts;
    }
    private sealed class Session(IQqBotTransport transport, CancellationTokenSource cancellation, string appId)
    {
        internal IQqBotTransport Transport { get; } = transport;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal string AppId { get; } = appId;
        internal Channel<QqC2cMessage> Inbox { get; } = Channel.CreateBounded<QqC2cMessage>(new BoundedChannelOptions(32)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        internal Task Completion { get; set; } = Task.CompletedTask;
        internal DateTimeOffset? LastUnsupportedNoticeAt;
    }
}
