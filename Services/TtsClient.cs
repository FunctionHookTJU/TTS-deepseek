using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeepSeekTTS;

/// <summary>
/// DeepSeek 语音合成接口客户端。
/// 文档：https://api-docs.deepseek.com/zh-cn/guides/tts
/// 整段合成：POST https://api.deepseek.com/v1/tools/tts，响应体中流式返回音频。
/// </summary>
public sealed class TtsClient : IDisposable
{
    private const string DefaultBase = "https://api.deepseek.com";
    private readonly HttpClient _http;

    public TtsClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeekTTS/2.0");
    }

    private static string Endpoint
    {
        get
        {
            var baseUrl = Environment.GetEnvironmentVariable("DEEPSEEK_TTS_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultBase;
            return baseUrl.TrimEnd('/') + "/v1/tools/tts";
        }
    }

    /// <summary>计费字符：汉字算 2，其他算 1。</summary>
    public static int CountBillingChars(string text)
    {
        var n = 0;
        foreach (var ch in text)
        {
            n += ch is >= '\u4e00' and <= '\u9fff' or >= '\u3400' and <= '\u4dbf' ? 2 : 1;
        }
        return n;
    }

    public sealed record TtsResult(bool Success, string? Error, string? Format, byte[]? Audio)
    {
        public static TtsResult Ok(string format, byte[] audio) => new(true, null, format, audio);
        public static TtsResult Fail(string error) => new(false, error, null, null);
    }

    /// <summary>
    /// 合成语音。返回原始音频字节（wav 含容器头；pcm 为 24kHz/16bit/单声道裸流）。
    /// </summary>
    public async Task<TtsResult> SynthesizeAsync(
        string text,
        string voiceId,
        string format,
        bool filterMarkdown,
        string apiKey,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TtsResult.Fail("请输入要朗读的文本。");

        var billing = CountBillingChars(text);
        if (billing > 10000)
            return TtsResult.Fail($"文本超长：单次上限 10,000 计费字符（汉字算 2），当前约 {billing}。");

        if (string.IsNullOrWhiteSpace(apiKey))
            return TtsResult.Fail("尚未配置 DeepSeek API Key，请点击右上角「设置」填写。");

        var payload = JsonSerializer.Serialize(new
        {
            text,
            voice_id = voiceId,
            format,
            filter_markdown = filterMarkdown,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException)
        {
            return TtsResult.Fail("已取消合成。");
        }
        catch (Exception ex)
        {
            return TtsResult.Fail($"连接 DeepSeek 语音合成服务失败：{ex.Message}");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct);
                return TtsResult.Fail(DescribeError((int)resp.StatusCode, raw));
            }

            // 流式读取：边收边写内存，最后统一校验完整性
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read <= 0) break;
                    ms.Write(buffer, 0, read);
                    total += read;
                    progress?.Report(total);
                }
            }
            catch (OperationCanceledException)
            {
                return TtsResult.Fail("已取消合成。");
            }
            catch (Exception ex)
            {
                return TtsResult.Fail($"音频传输中断（{ex.Message}）。官方说明提前结束的响应视为失败，请重试。");
            }

            var audio = ms.ToArray();
            var problem = Validate(audio, format);
            if (problem is not null) return TtsResult.Fail(problem);

            return TtsResult.Ok(format, audio);
        }
    }

    /// <summary>校验音频完整性：文档说明“响应提前结束视为失败”。</summary>
    private static string? Validate(byte[] audio, string format)
    {
        if (audio.Length == 0) return "音频为空，请重试。";

        if (format == "wav")
        {
            if (audio.Length < 44) return "音频数据不完整（WAV 过短），请重试。";
            var riff = Encoding.ASCII.GetString(audio, 0, 4);
            var wave = Encoding.ASCII.GetString(audio, 8, 4);
            if (riff != "RIFF" || wave != "WAVE") return "音频数据不完整（非合法 WAV），请重试。";
            return null;
        }

        // pcm：24 kHz / 16 bit / 单声道，字节数必须是偶数，否则是被截断的响应
        if (audio.Length < 960) return "音频数据不完整（PCM 过短），请重试。";
        if (audio.Length % 2 != 0) return "音频数据不完整（PCM 字节数为奇数，疑似响应被中途断开），请重试。";
        return null;
    }

    /// <summary>把上游错误转成可读中文提示。</summary>
    private static string DescribeError(int status, string raw)
    {
        var baseMsg = status switch
        {
            400 => "没有可朗读的文本，或文本未通过内容审核（Content Exists Risk）",
            401 => "API Key 无效或未授权，请在设置中重新填写",
            402 => "账户余额不足",
            403 => "无权访问语音合成接口",
            429 => "并发请求数超过账号上限（最大 50 个），请稍后重试",
            503 => "服务繁忙，请稍后重试",
            504 => "语音合成超时，请缩短文本后重试",
            _ => $"上游返回 HTTP {status}",
        };

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
            {
                var detail = msg.GetString();
                if (!string.IsNullOrWhiteSpace(detail) && !baseMsg.Contains(detail))
                    return $"{baseMsg}（{detail}）";
            }
        }
        catch
        {
            // 非 JSON 错误体，忽略细节
        }
        return baseMsg;
    }

    public void Dispose() => _http.Dispose();
}
