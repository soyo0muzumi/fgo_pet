using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using FgoPet.Core.Secrets;
using FgoPet.Extensibility;
using FgoPet.Platform.Secrets;
using FgoPet.Plugin.Qq;
using FgoPet.Plugin.Qq.Transport;
using Xunit;

namespace FgoPet.Qq.Tests;

public sealed class QqChatPluginTests
{
    [Fact]
    public async Task Bound_media_gets_a_deduplicated_rate_limited_notice_without_any_model_exchange()
    {
        var clock = new TestClock();
        await using var fixture = new Fixture(clock);
        await fixture.StartAsync(); await fixture.PairAsync();
        var transport = fixture.CurrentTransport;
        var image = Message("image-one", "owner", "") with { IsUnsupportedMedia = true };
        await transport.EmitAsync(image);
        Assert.Contains("未被读取", await transport.NextReplyAsync());
        Assert.Empty(fixture.Exchanges);
        await transport.EmitAsync(image);
        await transport.EmitAsync(image with { MessageId = "image-two" });
        await transport.EmitAsync(image with { MessageId = "foreign-image", UserOpenId = "someone-else" });
        await transport.EmitAsync(Message("barrier-one", "owner", "text still works"));
        Assert.Equal("reply:text still works", await transport.NextReplyAsync());
        Assert.False(transport.Replies.Reader.TryRead(out _));
        clock.Advance(TimeSpan.FromSeconds(31));
        await transport.EmitAsync(image with { MessageId = "image-three" });
        Assert.Contains("只支持文字", await transport.NextReplyAsync());
        Assert.Single(fixture.Exchanges);
        await fixture.RestartAsync();
        await fixture.CurrentTransport.EmitAsync(image);
        await fixture.CurrentTransport.EmitAsync(Message("barrier-two", "owner", "after restart"));
        Assert.Equal("reply:after restart", await fixture.CurrentTransport.NextReplyAsync());
        Assert.False(fixture.CurrentTransport.Replies.Reader.TryRead(out _));
        Assert.Equal(2, fixture.Exchanges.Count);
    }

    [Fact]
    public async Task Unpaired_media_is_silent_and_cannot_replace_a_pairing_code()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.Plugin.ConfigureAsync("123", "synthetic-secret", default);
        var pairing = await fixture.Plugin.BeginPairingAsync(default);
        await fixture.CurrentTransport.EmitAsync(Message("unpaired-image", "owner", "") with { IsUnsupportedMedia = true });
        await fixture.CurrentTransport.EmitAsync(Message("pairing-after-media", "owner", pairing.PairingCode!));
        await fixture.CurrentTransport.NextReplyAsync();
        Assert.False(fixture.CurrentTransport.Replies.Reader.TryRead(out _));
        Assert.True((await fixture.Plugin.GetSnapshotAsync(default)).IsPaired);
        Assert.Empty(fixture.Exchanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_disposal_revokes_admission_without_waiting_for_callbacks_and_async_disposal_drains(bool callbackThrows)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Exchange = async (_, _, token) =>
        {
            using var callback = token.Register(() =>
            {
                cancelling.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("SYNTHETIC_CALLBACK_DEADLINE");
                if (callbackThrows) throw new InvalidOperationException("SYNTHETIC_CALLBACK_FAILURE");
            });
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { drained.TrySetResult(); }
            return new("must not be delivered");
        };
        await fixture.CurrentTransport.EmitAsync(Message("running-dispose", "owner", "synthetic pending question"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? stopping = null;
        try
        {
            var sync = Assert.IsAssignableFrom<IDisposable>(fixture.Plugin);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            sync.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = fixture.Plugin.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            sync.Dispose();
            await fixture.CurrentTransport.EmitAsync(Message("after-dispose", "owner", "must not be admitted"));
            Assert.Single(fixture.Exchanges);
        }
        finally
        {
            release.Set();
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(drained.Task.IsCompleted);
        Assert.True(fixture.CurrentTransport.Disposed);
        Assert.False(fixture.CurrentTransport.Replies.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Disabled_installation_does_not_create_a_transport_or_read_a_model()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var state = await fixture.Plugin.GetSnapshotAsync(default);
        Assert.False(state.Enabled);
        Assert.False(state.IsPaired);
        Assert.False(state.HasSecret);
        Assert.Empty(fixture.Transports);
        Assert.Empty(fixture.Exchanges);
    }

    [Fact]
    public async Task Local_code_pairs_one_sender_without_putting_the_code_in_conversation_history()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var code = await fixture.PairAsync();
        var snapshot = await fixture.Plugin.GetSnapshotAsync(default);
        Assert.True(snapshot.Enabled);
        Assert.True(snapshot.IsPaired);
        Assert.Null(snapshot.PairingCode);
        Assert.Equal(fixture.Binding, snapshot.Binding);
        Assert.Empty(fixture.Exchanges);
        Assert.DoesNotContain(code, Encoding.UTF8.GetString(fixture.Protector.LastPlaintext));
        Assert.DoesNotContain("synthetic-secret", Encoding.UTF8.GetString(fixture.Protector.LastPlaintext));
        Assert.DoesNotContain("owner", Encoding.UTF8.GetString(File.ReadAllBytes(fixture.StatePath)));
    }

    [Fact]
    public async Task Only_bound_sender_is_admitted_and_replayed_message_is_not_generated_twice()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        var transport = fixture.CurrentTransport;
        await transport.EmitAsync(Message("foreign", "someone-else", "not mine"));
        await transport.EmitAsync(Message("first", "owner", "today's experiment"));
        Assert.Equal("reply:today's experiment", await transport.NextReplyAsync());
        await transport.EmitAsync(Message("first", "owner", "today's experiment"));
        await transport.EmitAsync(Message("second", "owner", "follow up"));
        Assert.Equal("reply:follow up", await transport.NextReplyAsync());
        Assert.Equal(new[] { "today's experiment", "follow up" }, fixture.Exchanges.Select(x => x.Text));
        Assert.All(fixture.Exchanges, x => Assert.Equal(fixture.Binding, x.Binding));
        Assert.DoesNotContain("today's experiment", Encoding.UTF8.GetString(fixture.Protector.LastPlaintext));
    }

    [Fact]
    public async Task Receipt_survives_restart_and_no_second_model_call_is_made_for_the_old_message()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        var message = Message("durable", "owner", "remember the context");
        await fixture.CurrentTransport.EmitAsync(message);
        await fixture.CurrentTransport.NextReplyAsync();
        await fixture.RestartAsync();
        await fixture.CurrentTransport.EmitAsync(message);
        await fixture.CurrentTransport.EmitAsync(Message("after-restart", "owner", "continue"));
        Assert.Equal("reply:continue", await fixture.CurrentTransport.NextReplyAsync());
        Assert.Equal(2, fixture.Exchanges.Count);
    }

    [Fact]
    public async Task Pairing_again_preserves_recent_receipts_and_does_not_replay_an_old_turn()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        var old = Message("before-pairing-again", "owner", "already answered");
        await fixture.CurrentTransport.EmitAsync(old);
        await fixture.CurrentTransport.NextReplyAsync();
        var pairing = await fixture.Plugin.BeginPairingAsync(default);
        await fixture.CurrentTransport.EmitAsync(Message("pairing-again", "owner", pairing.PairingCode!));
        await fixture.CurrentTransport.NextReplyAsync();
        await fixture.CurrentTransport.EmitAsync(old);
        await fixture.CurrentTransport.EmitAsync(Message("after-pairing-again", "owner", "continue"));
        Assert.Equal("reply:continue", await fixture.CurrentTransport.NextReplyAsync());
        Assert.Equal(2, fixture.Exchanges.Count);
    }

    [Fact]
    public async Task Whitespace_cannot_bypass_the_pairing_attempt_limit()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.Plugin.ConfigureAsync("123", "synthetic-secret", default);
        await fixture.Plugin.BeginPairingAsync(default);
        for (var i = 0; i < 10; i++)
            await fixture.CurrentTransport.EmitAsync(Message("wrong-code-" + i, "someone-else", "  FGO-invalid  "));
        await fixture.CurrentTransport.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = await fixture.Plugin.GetSnapshotAsync(default);
        Assert.False(snapshot.IsPaired);
        Assert.Null(snapshot.PairingCode);
        Assert.Equal("QQ_PAIRING_ATTEMPTS_EXCEEDED", snapshot.ErrorCode);
        Assert.Empty(fixture.Exchanges);
    }

    [Fact]
    public async Task A_long_reply_is_delivered_with_a_desktop_continuation_notice_and_valid_unicode()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        var fullReply = string.Concat(Enumerable.Repeat("😀", 8_000));
        fixture.Exchange = (_, _, _) => Task.FromResult(new QqConversationReply(fullReply));
        await fixture.CurrentTransport.EmitAsync(Message("long-reply", "owner", "explain the plan"));
        var reply = await fixture.CurrentTransport.NextReplyAsync();
        Assert.InRange(reply.Length, 1, 12_000);
        Assert.Contains("完整内容已保存在电脑上的对话中", reply);
        Assert.StartsWith("😀", reply);
        _ = new UnicodeEncoding(false, false, true).GetBytes(reply);
        Assert.Single(fixture.Exchanges);
    }

    [Fact]
    public async Task Unknown_delivery_is_not_retried_or_regenerated_after_reconnect()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        fixture.CurrentTransport.SendResult = new(false, "QQ_SEND_UNKNOWN");
        var message = Message("uncertain", "owner", "one model turn");
        await fixture.CurrentTransport.EmitAsync(message);
        await fixture.CurrentTransport.NextReplyAsync();
        await fixture.RestartAsync();
        await fixture.CurrentTransport.EmitAsync(message);
        await fixture.CurrentTransport.EmitAsync(Message("next", "owner", "next turn"));
        Assert.Equal("reply:next turn", await fixture.CurrentTransport.NextReplyAsync());
        Assert.Equal(2, fixture.Exchanges.Count);
    }

    [Fact]
    public async Task Disabling_cancels_and_drains_the_running_turn_before_returning()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Exchange = async (_, _, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.TrySetResult(); }
            return new("must not be delivered");
        };
        await fixture.CurrentTransport.EmitAsync(Message("running", "owner", "slow question"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var state = await fixture.Plugin.SetEnabledAsync(false, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Task.IsCompleted);
        Assert.False(state.Enabled);
        Assert.True(fixture.CurrentTransport.Disposed);
        Assert.False(fixture.CurrentTransport.Replies.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Expired_or_future_messages_never_enter_the_dialogue()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        await fixture.CurrentTransport.EmitAsync(Message("old", "owner", "old") with { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-6) });
        await fixture.CurrentTransport.EmitAsync(Message("future", "owner", "future") with { Timestamp = DateTimeOffset.UtcNow.AddMinutes(2) });
        await fixture.CurrentTransport.EmitAsync(Message("current", "owner", "current"));
        Assert.Equal("reply:current", await fixture.CurrentTransport.NextReplyAsync());
        Assert.Single(fixture.Exchanges);
    }

    [Fact]
    public async Task Cancelled_configuration_has_no_side_effect_and_another_app_cannot_reuse_an_unrelated_secret()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Plugin.ConfigureAsync("123", "synthetic-secret", new(true)));
        Assert.Empty(fixture.Credentials.Values);
        await fixture.Plugin.ConfigureAsync("123", "synthetic-secret", default);
        await fixture.Credentials.SaveAsync(QqChatPlugin.CredentialTarget("456"), "unrelated-secret", default);
        var state = await fixture.Plugin.ConfigureAsync("456", "", default);
        Assert.Equal("123", state.AppId);
        Assert.Equal("QQ_SECRET_REQUIRED", state.ErrorCode);
    }

    [Fact]
    public async Task Corrupt_protected_state_fails_closed_and_can_be_reset_locally()
    {
        await using var fixture = new Fixture();
        File.WriteAllText(fixture.StatePath, "corrupt envelope");
        await fixture.RestartAsync();
        var snapshot = await fixture.Plugin.GetSnapshotAsync(default);
        Assert.False(snapshot.Enabled);
        Assert.Equal("QQ_STATE_UNAVAILABLE", snapshot.ErrorCode);
        Assert.Empty(fixture.Transports);
        Assert.Null((await fixture.Plugin.ResetAsync(default)).ErrorCode);
        Assert.Equal("123", (await fixture.Plugin.ConfigureAsync("123", "synthetic-secret", default)).AppId);
    }

    [Fact]
    public async Task Receipt_storage_failure_prevents_model_dispatch()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        fixture.Protector.FailWrites = true;
        await fixture.CurrentTransport.EmitAsync(Message("write-fails", "owner", "must not run"));
        await fixture.CurrentTransport.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Exchanges);
        Assert.NotNull((await fixture.Plugin.GetSnapshotAsync(default)).ErrorCode);
    }

    [Fact]
    public async Task Reset_removes_the_current_credential_and_binding_and_closes_the_connection()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.PairAsync();
        var state = await fixture.Plugin.ResetAsync(default);
        Assert.False(state.Enabled);
        Assert.False(state.IsPaired);
        Assert.False(state.HasSecret);
        Assert.Null(state.Binding);
        Assert.Empty(fixture.Credentials.Values);
        Assert.True(fixture.CurrentTransport.Disposed);
    }

    private static QqC2cMessage Message(string id, string sender, string text) => new(id, sender, text, DateTimeOffset.UtcNow);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TimeProvider _clock;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "fgo-qq-test-" + Guid.NewGuid().ToString("N"));
        internal string StatePath => Path.Combine(_directory, "state.protected");
        internal TestProtector Protector { get; } = new();
        internal TestCredentials Credentials { get; } = new();
        internal List<FakeTransport> Transports { get; } = [];
        internal FakeTransport CurrentTransport => Transports[^1];
        internal ConcurrentQueue<(QqConversationBinding Binding, string Text)> Exchanges { get; } = new();
        internal QqConversationBinding Binding { get; set; } = new("mash", "conversation-one", null);
        internal Func<QqConversationBinding, string, CancellationToken, Task<QqConversationReply>>? Exchange { get; set; }
        internal QqChatPlugin Plugin { get; private set; }
        internal Fixture(TimeProvider? clock = null) { _clock=clock ?? TimeProvider.System; Directory.CreateDirectory(_directory); Plugin = Create(); }
        private QqChatPlugin Create() => new(new(StatePath, Protector), Credentials,
            () => { var value = new FakeTransport(); Transports.Add(value); return value; },
            _ => Task.FromResult<QqConversationBinding?>(Binding), async (binding, text, token) =>
            {
                Exchanges.Enqueue((binding, text));
                return Exchange is null ? new("reply:" + text) : await Exchange(binding, text, token);
            }, timeProvider: _clock);
        internal async Task StartAsync()
        { await Plugin.StartAsync(default); await Plugin.OnApplicationReadyAsync(new(true), default); }
        internal async Task RestartAsync()
        { await Plugin.DisposeAsync(); Plugin = Create(); await StartAsync(); }
        internal async Task<string> PairAsync()
        {
            await Plugin.ConfigureAsync("123", "synthetic-secret", default);
            var snapshot = await Plugin.BeginPairingAsync(default);
            Assert.NotNull(snapshot.PairingCode);
            await CurrentTransport.EmitAsync(Message("pairing", "owner", snapshot.PairingCode));
            await CurrentTransport.NextReplyAsync();
            return snapshot.PairingCode;
        }
        public async ValueTask DisposeAsync()
        { await Plugin.DisposeAsync(); Directory.Delete(_directory, recursive: true); }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeTransport : IQqBotTransport
    {
        private Func<QqC2cMessage, CancellationToken, Task>? _receive;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Channel<string> Replies { get; } = Channel.CreateUnbounded<string>();
        internal TaskCompletionSource Disposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        internal QqSendResult SendResult { get; set; } = new(true);
        public async Task RunAsync(QqBotConnection connection, Func<QqC2cMessage, CancellationToken, Task> onMessage,
            Action<QqTransportStatus> onStatus, CancellationToken cancellationToken)
        {
            _receive = onMessage;
            onStatus(new(QqTransportState.Connected));
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        internal async Task EmitAsync(QqC2cMessage message)
        { await _started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await _receive!(message, default); }
        internal async Task<string> NextReplyAsync() => await Replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public Task<QqSendResult> ReplyTextAsync(QqC2cMessage message, string text, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Replies.Writer.TryWrite(text); return Task.FromResult(SendResult); }
        public ValueTask DisposeAsync() { Disposed = true; Disposal.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class TestCredentials : ICredentialStore
    {
        internal Dictionary<string, string> Values { get; } = [];
        public Task SaveAsync(string target, string secret, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Values[target] = secret; return Task.CompletedTask; }
        public Task DeleteAsync(string target, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Values.Remove(target); return Task.CompletedTask; }
        public Task<bool> ExistsAsync(string target, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Values.ContainsKey(target)); }
    }

    private sealed class TestProtector : IProtectedStateProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        internal byte[] LastPlaintext { get; private set; } = [];
        internal bool FailWrites { get; set; }
        public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
        {
            if (FailWrites) throw new StateProtectionException("SYNTHETIC_FAILURE");
            LastPlaintext = plaintext.ToArray();
            var result = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16), Encoding.UTF8.GetBytes(purpose));
            return result;
        }
        public byte[] Unprotect(ReadOnlySpan<byte> protectedState, string purpose)
        {
            if (protectedState.Length < 28) throw new StateProtectionException("SYNTHETIC_INVALID");
            var result = new byte[protectedState.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(protectedState[..12], protectedState[28..], protectedState.Slice(12, 16), result, Encoding.UTF8.GetBytes(purpose));
            return result;
        }
    }
}
