using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.AgentRuntime.Security;

namespace FgoPet.AgentRuntime.Storage;

/// <summary>
/// Stores one JSON value as an encrypted, versioned wrapper and replaces it atomically.
/// The lock is per path so independently-created store instances cannot interleave writes.
/// </summary>
public sealed class AtomicProtectedJsonStore<T>
{
    private const int FormatVersion = 1;
    public const int DefaultMaxBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly int _maxBytes;
    private readonly object _pathGate;

    public AtomicProtectedJsonStore(string path, ISecretProtector protector, int maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _path = Path.GetFullPath(path);
        _protector = protector;
        _maxBytes = maxBytes;
        _pathGate = PathLocks.GetOrAdd(_path, static _ => new object());
    }

    public T Load(Func<T>? emptyFactory = null, Func<T, bool>? validate = null)
    {
        lock (_pathGate)
        {
            try
            {
                byte[] fileBytes;
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > _maxBytes)
                        throw new InvalidDataException("The protected state file exceeds its size limit.");
                    fileBytes = new byte[(int)stream.Length];
                    stream.ReadExactly(fileBytes);
                }
                var wrapper = JsonSerializer.Deserialize<ProtectedWrapper>(fileBytes, JsonOptions)
                    ?? throw new JsonException("The protected state wrapper is null.");
                if (wrapper.Version != FormatVersion || string.IsNullOrWhiteSpace(wrapper.Payload))
                    throw new JsonException("The protected state wrapper schema is invalid.");

                var protectedBytes = Convert.FromBase64String(wrapper.Payload);
                var plaintext = _protector.Unprotect(protectedBytes);
                var value = JsonSerializer.Deserialize<T>(plaintext, JsonOptions)
                    ?? throw new JsonException("The protected state payload is null.");
                if (validate is not null && !validate(value))
                    throw new JsonException("The protected state payload failed validation.");
                return value;
            }
            catch (FileNotFoundException) { return emptyFactory is null ? default! : emptyFactory(); }
            catch (DirectoryNotFoundException) { return emptyFactory is null ? default! : emptyFactory(); }
            catch (Exception error) when (IsCorruptContent(error))
            {
                QuarantineCorruptFile();
                return emptyFactory is null ? default! : emptyFactory();
            }
        }
    }

    public void Save(T value)
    {
        lock (_pathGate)
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            var protectedBytes = _protector.Protect(plaintext);
            var wrapper = JsonSerializer.SerializeToUtf8Bytes(
                new ProtectedWrapper(FormatVersion, Convert.ToBase64String(protectedBytes)), JsonOptions);
            if (wrapper.Length > _maxBytes || plaintext.Length > _maxBytes)
                throw new InvalidDataException("The protected state exceeds its size limit.");

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(wrapper);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { /* Never mask the failed save with best-effort cleanup. */ }
                throw;
            }
        }
    }

    private void QuarantineCorruptFile()
    {
        var quarantinePath = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff")
            + "-" + Guid.NewGuid().ToString("N");
        File.Move(_path, quarantinePath, overwrite: false);
    }

    private static bool IsCorruptContent(Exception error) => error switch
    {
        JsonException or FormatException or DecoderFallbackException or CryptographicException or EndOfStreamException => true,
        InvalidDataException => true,
        _ => false,
    };

    private sealed record ProtectedWrapper(int Version, string Payload);
}
