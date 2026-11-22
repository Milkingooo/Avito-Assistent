using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BuyerAssistant.Core;

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Клиент Chat Completions шлюза организаторов (LLM_API.md).
/// Явный User-Agent, запрет редиректов, json_schema strict с откатом на json_object при 400,
/// поддержка Retry-After при 429, скрытие ключа в логах.
/// </summary>
public static class LlmClient
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    private static readonly HttpClient SharedClient = new(SharedHandler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static void ValidateBaseUrl(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new LlmException("LLM_BASE_URL должен быть корректным HTTPS-адресом (или HTTP на loopback)");
        }

        var isLoopback = uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        var schemeOk = uri.Scheme == "https" || (isLoopback && uri.Scheme == "http");
        if (!schemeOk)
        {
            throw new LlmException("LLM_BASE_URL должен быть HTTPS-адресом (или HTTP на loopback)");
        }
    }

    public static JsonElement ChatJson(
        LlmSettings settings,
        IReadOnlyList<Dictionary<string, string>> messages,
        object schema,
        string schemaName,
        int maxTokens,
        DateTimeOffset deadlineUtc,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new LlmException("не заданы LLM_API_KEY / LLM_BASE_URL");
        }

        ValidateBaseUrl(settings.BaseUrl);
        var url = settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var userAgent = string.IsNullOrWhiteSpace(settings.UserAgent) ? "hackathon-client/1.0" : settings.UserAgent;
        var model = string.IsNullOrWhiteSpace(settings.Model) ? "google/gemma-4-31B-it" : settings.Model;

        var useSchema = true;
        var attempts = 0;

        while (true)
        {
            var remaining = (deadlineUtc - DateTimeOffset.UtcNow).TotalSeconds;
            if (remaining < 5.0)
            {
                throw new LlmException("исчерпан лимит времени на вызов модели");
            }

            attempts++;
            object responseFormat = useSchema
                ? new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object>
                    {
                        ["name"] = schemaName,
                        ["strict"] = true,
                        ["schema"] = schema
                    }
                }
                : new Dictionary<string, object> { ["type"] = "json_object" };

            var bodyObj = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = maxTokens,
                ["temperature"] = 0,
                ["response_format"] = responseFormat
            };

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(bodyObj, JsonOpts);
            if (jsonBytes.Length > 65536)
            {
                throw new LlmException($"превышен лимит запроса к шлюзу ({jsonBytes.Length} > 65536 байт)");
            }

            int status;
            string rawResponse;
            string? retryAfterHeader = null;

            try
            {
                var callTimeout = TimeSpan.FromSeconds(Math.Min(remaining - 2.0, 90.0));
                using var cts = new CancellationTokenSource(callTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(jsonBytes)
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);

                using var resp = SharedClient.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                status = (int)resp.StatusCode;
                if (resp.Headers.TryGetValues("Retry-After", out var vals))
                {
                    retryAfterHeader = vals.FirstOrDefault();
                }

                using var stream = resp.Content.ReadAsStream(cts.Token);
                using var ms = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while (ms.Length < 1_000_000 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, 1_000_000 - ms.Length))) > 0)
                {
                    ms.Write(buffer, 0, read);
                }
                rawResponse = Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
            {
                log?.Invoke($"LLM: сетевая ошибка ({ex.GetType().Name}), попытка {attempts}");
                if (attempts < 3 && (deadlineUtc - DateTimeOffset.UtcNow).TotalSeconds > 20.0)
                {
                    Thread.Sleep(1500);
                    continue;
                }
                throw new LlmException("не удалось связаться с API модели", ex);
            }

            var redacted = rawResponse.Replace(settings.ApiKey, "[REDACTED]", StringComparison.Ordinal);
            if (status == 200)
            {
                return ParseEnvelope(redacted);
            }

            var preview = redacted.Length <= 300 ? redacted : redacted[..300];
            log?.Invoke($"LLM: HTTP {status}: {preview}");

            if (status == 400 && useSchema && attempts < 3)
            {
                useSchema = false; // схема не принята шлюзом — откатываемся на json_object
                continue;
            }

            if (status == 429 && attempts < 4)
            {
                var waitSec = 3.0;
                if (!string.IsNullOrWhiteSpace(retryAfterHeader) &&
                    double.TryParse(retryAfterHeader, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWait))
                {
                    waitSec = parsedWait;
                }
                waitSec = Math.Clamp(waitSec, 1.0, 20.0);
                if ((deadlineUtc - DateTimeOffset.UtcNow).TotalSeconds > waitSec + 10.0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(waitSec));
                    continue;
                }
            }

            if ((status == 502 || status == 503) && attempts < 3 && (deadlineUtc - DateTimeOffset.UtcNow).TotalSeconds > 15.0)
            {
                Thread.Sleep(1500);
                continue;
            }

            throw new LlmException($"API модели вернуло HTTP {status}");
        }
    }

    public static string StripFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = trimmed.IndexOf('\n');
            trimmed = nl >= 0 ? trimmed[(nl + 1)..] : "";
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
                trimmed = trimmed[..lastFence];
        }
        return trimmed.Trim();
    }

    public static JsonElement ParseEnvelope(string envelopeJson)
    {
        try
        {
            using var envDoc = JsonDocument.Parse(envelopeJson);
            var root = envDoc.RootElement;
            var content = root
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                throw new LlmException("модель вернула пустой content");

            using var innerDoc = JsonDocument.Parse(StripFences(content));
            if (innerDoc.RootElement.ValueKind != JsonValueKind.Object)
                throw new LlmException("модель вернула не JSON-объект");

            return innerDoc.RootElement.Clone();
        }
        catch (LlmException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LlmException("модель вернула ответ в неожиданном формате", ex);
        }
    }
}
