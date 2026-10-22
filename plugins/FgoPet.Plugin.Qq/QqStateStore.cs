using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Platform.Secrets;

namespace FgoPet.Plugin.Qq;

/// <summary>Protected, atomic local pairing and receipt state; never persists a bot secret or chat text.</summary>
public sealed class QqStateStore(string path, IProtectedStateProtector protector)
{
    private const string Purpose = "qq-private-chat.v1";
    private const int MaxBytes = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal QqStoredState Load()
    {
        if (!File.Exists(path)) return new();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxBytes) throw new InvalidDataException("QQ_STATE_INVALID");
        var envelope = new byte[checked((int)stream.Length)];
        stream.ReadExactly(envelope);
        var plaintext = protector.Unprotect(envelope, Purpose);
        try
        {
            if (plaintext.Length > MaxBytes) throw new InvalidDataException("QQ_STATE_INVALID");
            var state = JsonSerializer.Deserialize<QqStoredState>(plaintext, JsonOptions);
            if (state is null || !Valid(state)) throw new InvalidDataException("QQ_STATE_INVALID");
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal void Save(QqStoredState state, CancellationToken token)
    {
        if (!Valid(state)) throw new InvalidDataException("QQ_STATE_INVALID");
        token.ThrowIfCancellationRequested();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        byte[] envelope;
        try { envelope = protector.Protect(plaintext, Purpose); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        if (envelope.Length > MaxBytes) throw new InvalidDataException("QQ_STATE_TOO_LARGE");
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".qq-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(envelope);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Valid(QqStoredState state) => state.Version == 1 &&
        (state.AppId.Length == 0 || QqValidation.AppId(state.AppId)) &&
        (state.OwnerOpenId is null && state.Binding is null ||
            QqValidation.Identifier(state.OwnerOpenId) && QqValidation.Binding(state.Binding) && QqValidation.AppId(state.AppId)) &&
        (!state.Enabled || state.OwnerOpenId is not null && state.Binding is not null) &&
        (state.Receipts is null || state.Receipts.Length <= 1024 && state.Receipts.All(r => r is not null &&
            r.IdHash.Length == 64 && r.IdHash.All(char.IsAsciiHexDigit)));
}
