using System.Net.Http.Json;
using System.Text.Json;
using FgoPet.Core.Speech;

namespace FgoPet.Infrastructure.Speech;

/// <summary>Connects an existing IndexTTS 2/2.5 Gradio WebUI. Never starts a process or retries synthesis.</summary>
public sealed class IndexTtsSpeechSynthesizer(HttpClient client) : ISpeechSynthesizer
{
    public SpeechProviderKind Provider => SpeechProviderKind.IndexTts;
    private const int JsonLimit = 2 * 1024 * 1024;
    public async Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Provider != Provider) throw Failure("语音服务与请求不匹配。");
        var root = request.Endpoint;
        if (!root.IsAbsoluteUri || root.Scheme != "http" || !root.IsLoopback ||
            root.UserInfo.Length > 0 || root.Query.Length > 0 || root.Fragment.Length > 0)
            throw Failure("IndexTTS 仅允许本机 HTTP 服务。");
        if (string.IsNullOrWhiteSpace(request.ReferenceAudioPath))
            throw Failure("请先导入并选择参考音色。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        Uri At(string path) => new(root.ToString().TrimEnd('/') + "/" + path);
        try
        {
            // Read defaults from the running UI. Reject incompatible APIs before uploading private audio.
            using var configResponse = await client.GetAsync(At("config"), HttpCompletionOption.ResponseHeadersRead, ct);
            using var config = await JsonAsync(configResponse, ct);
            var dependencies = config.RootElement.GetProperty("dependencies").EnumerateArray()
                .Where(d => d.TryGetProperty("api_name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "gen_single").ToArray();
            if (dependencies.Length != 1) throw Failure("本地 IndexTTS 未公开 gen_single 接口，请核对 WebUI 版本。");
            var ids = dependencies[0].GetProperty("inputs").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (ids.Length != 26) throw Failure("IndexTTS 接口参数与已核对版本不同，未发送参考音频。");
            var components = config.RootElement.GetProperty("components").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetInt32());
            var expectedTypes = new[] { "radio", "audio", "textbox", "dropdown", "audio", "slider" };
            for (var i = 0; i < expectedTypes.Length; i++)
                if (components[ids[i]].GetProperty("type").GetString() != expectedTypes[i])
                    throw Failure("IndexTTS 接口结构不兼容，未发送参考音频。");
            var data = ids.Select(id => components[id].GetProperty("props").TryGetProperty("value", out var value)
                ? (object?)value.Clone() : null).ToArray();
            // First radio choice means use the speaker reference for emotion; use its raw Gradio value.
            var choice = components[ids[0]].GetProperty("props").GetProperty("choices")[0];
            data[0] = choice.ValueKind == JsonValueKind.Array ? choice[1].Clone() : choice.Clone();
            data[2] = request.Text;
            data[4] = null;
            data[14] = "";
            data[15] = false;
            using var audio = File.OpenRead(request.ReferenceAudioPath);
            if (audio.Length is < 12 or > 20 * 1024 * 1024) throw Failure("参考音频大小应在 20 MB 以内。");
            var header = new byte[12];
            await audio.ReadExactlyAsync(header, ct);
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                throw Failure("请导入 WAV 参考音频。");
            audio.Position = 0;
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(audio), "files", "reference.wav");
            using var uploaded = await client.PostAsync(At("gradio_api/upload"), form, ct);
            using var upload = await JsonAsync(uploaded, ct);
            var uploadedPath = upload.RootElement[0].GetString();
            if (string.IsNullOrEmpty(uploadedPath)) throw Failure("参考音频上传未完成。");
            data[1] = new { path = uploadedPath, meta = new { _type = "gradio.FileData" } };
            using var call = await client.PostAsJsonAsync(At("gradio_api/call/gen_single"), new { data }, ct);
            using var started = await JsonAsync(call, ct);
            var eventId = started.RootElement.GetProperty("event_id").GetString();
            if (string.IsNullOrEmpty(eventId) || eventId.Length > 128 || eventId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw Failure("IndexTTS 返回了无效请求标识。");
            using var events = await client.GetAsync(At("gradio_api/call/gen_single/" + eventId), HttpCompletionOption.ResponseHeadersRead, ct);
            EnsureSuccess(events);
            using var stream = await events.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var eventType = "";
            var read = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                read += line.Length;
                if (read > JsonLimit) throw Failure("IndexTTS 事件响应超出限制。");
                if (line.StartsWith("event:")) eventType = line[6..].Trim();
                if (!line.StartsWith("data:")) continue;
                if (eventType == "error") throw new SpeechSynthesisException(SpeechFailureCategory.ServiceUnavailable, "IndexTTS 推理失败，请检查本地服务。", requestWasSent: true);
                if (eventType != "complete") continue;
                using var result = JsonDocument.Parse(line[5..]);
                var file = result.RootElement[0];
                if (file.TryGetProperty("value", out var value)) file = value;
                var path = file.GetProperty("path").GetString();
                if (string.IsNullOrEmpty(path) || path.Length > 4096) throw Failure("IndexTTS 没有返回有效音频。");
                // Ignore server-supplied URLs. Downloads stay at the explicitly configured origin.
                using var wave = await client.GetAsync(At("gradio_api/file=" + Uri.EscapeDataString(path)), HttpCompletionOption.ResponseHeadersRead, ct);
                EnsureSuccess(wave);
                var bytes = await ReadLimitedAsync(wave.Content, 64 * 1024 * 1024, ct);
                ct.ThrowIfCancellationRequested();
                return new SpeechSynthesisResult(bytes, "audio/wav");
            }
            throw Failure("IndexTTS 连接结束，但未收到生成结果。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SpeechSynthesisException(SpeechFailureCategory.ServiceUnavailable, "IndexTTS 等待超时；本机推理可能仍在运行。", requestWasSent: true); }
        catch (HttpRequestException error) { throw SpeechHttp.Network(error); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        { throw new SpeechSynthesisException(SpeechFailureCategory.InvalidResponse, "IndexTTS 接口或音频格式不兼容，请核对本地服务。", requestWasSent: true); }
        catch (IOException) { throw Failure("参考音频或本地语音连接不可用。"); }
        catch (UnauthorizedAccessException) { throw Failure("无法读取参考音频。"); }
    }

    private static SpeechSynthesisException Failure(string message) => new(SpeechFailureCategory.Configuration, message);
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new SpeechSynthesisException(SpeechFailureCategory.ServiceUnavailable, "IndexTTS 服务未返回成功响应。", statusCode: response.StatusCode, requestWasSent: true);
    }
    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        EnsureSuccess(response);
        return JsonDocument.Parse(await ReadLimitedAsync(response.Content, JsonLimit, ct));
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw Failure("语音服务响应过大。");
        using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > limit) throw Failure("语音服务响应过大。");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }
}
