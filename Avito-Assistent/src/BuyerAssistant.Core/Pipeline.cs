using System.Text.Encodings.Web;
using System.Text.Json;

namespace BuyerAssistant.Core;

public sealed class RequestValidationException(string message, Exception? inner = null) : Exception(message, inner);

public delegate Conditions ExtractorDelegate(
    IReadOnlyList<ChatMessage> messages,
    LlmSettings settings,
    DateTimeOffset deadlineUtc,
    Action<string>? log,
    bool allowFallback);

/// <summary>
/// Валидация входного запроса по CONTRACT.md §3 / schemas/request.schema.json и конвейер обработки.
/// </summary>
public static class AssistantPipeline
{
    public static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static List<ChatMessage> ValidateRequestBytes(byte[] rawBytes)
    {
        ReadOnlySpan<byte> span = rawBytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        JsonDocument doc;
        try
        {
            JsonDuplicateKeyChecker.ValidateStrictJson(span);
            doc = JsonDocument.Parse(span.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (Exception ex)
        {
            throw new RequestValidationException($"некорректный JSON запроса: {ex.Message}", ex);
        }

        using (doc)
        {
            return ValidateRequestElement(doc.RootElement);
        }
    }

    public static List<ChatMessage> ValidateRequestElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new RequestValidationException("ожидается JSON-объект запроса");

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name != "messages")
                throw new RequestValidationException($"запрещённое поле верхнего уровня: '{prop.Name}'");
        }

        if (!root.TryGetProperty("messages", out var msgsEl) || msgsEl.ValueKind != JsonValueKind.Array)
            throw new RequestValidationException("ожидается объект с непустым массивом messages");

        var list = new List<ChatMessage>();
        foreach (var m in msgsEl.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
                throw new RequestValidationException("каждое сообщение должно быть JSON-объектом");

            foreach (var prop in m.EnumerateObject())
            {
                if (prop.Name is not ("role" or "text"))
                    throw new RequestValidationException($"запрещённое поле сообщения: '{prop.Name}'");
            }

            if (!m.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
                throw new RequestValidationException("поле role обязательно и должно быть строкой");
            if (!m.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                throw new RequestValidationException("поле text обязательно и должно быть строкой");

            var role = roleEl.GetString()!;
            var text = textEl.GetString()!;

            if (role is not ("user" or "assistant"))
                throw new RequestValidationException($"недопустимая роль: '{role}'");
            if (string.IsNullOrWhiteSpace(text))
                throw new RequestValidationException("поле text не может быть пустым");

            list.Add(new ChatMessage(role, text));
        }

        if (list.Count == 0)
            throw new RequestValidationException("массив messages не может быть пустым");
        if (list.Count > 20)
            throw new RequestValidationException("массив messages превышает лимит в 20 сообщений");

        // CONTRACT.md §3: Первое и последнее сообщения принадлежат user, роли чередуются
        if (list[0].Role != "user")
            throw new RequestValidationException("первая реплика должна принадлежать user");
        if (list[^1].Role != "user")
            throw new RequestValidationException("последняя реплика должна принадлежать user");

        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Role == list[i - 1].Role)
                throw new RequestValidationException("роли сообщений должны чередоваться (user / assistant)");
        }

        return list;
    }

    public static AssistantResponse Process(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<Listing> catalog,
        LlmSettings settings,
        DateTimeOffset deadlineUtc,
        Action<string>? log = null,
        bool allowFallback = true,
        ExtractorDelegate? extractor = null)
    {
        var activeExtractor = extractor ?? ConditionExtractor.Extract;
        var cond = activeExtractor(messages, settings, deadlineUtc, log, allowFallback);

        var consDesc = string.Join(", ", cond.Constraints.Select(c =>
            $"({c.Attribute}, {c.Op}, {(c.Attribute == "adjustable_height" ? c.BoolValue?.ToString() : c.NumericValue?.ToString())})"));
        log?.Invoke($"условия: city={cond.CityId} budget={cond.BudgetRub} categories=[{string.Join(",", cond.Categories)}] constraints=[{consDesc}] keywords=[{string.Join(",", cond.Keywords)}]");

        var questions = Conditions.GetMissingQuestions(cond);
        if (questions.Count > 0)
        {
            return AssistantResponse.Clarify(questions);
        }

        return ResponseBuilder.Solve(catalog, cond);
    }

    public static (AssistantResponse Response, Conditions ExtractedConditions) ProcessWithConditions(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<Listing> catalog,
        LlmSettings settings,
        DateTimeOffset deadlineUtc,
        Action<string>? log = null,
        bool allowFallback = true,
        ExtractorDelegate? extractor = null)
    {
        var activeExtractor = extractor ?? ConditionExtractor.Extract;
        var cond = activeExtractor(messages, settings, deadlineUtc, log, allowFallback);

        var questions = Conditions.GetMissingQuestions(cond);
        if (questions.Count > 0)
        {
            return (AssistantResponse.Clarify(questions), cond);
        }

        return (ResponseBuilder.Solve(catalog, cond), cond);
    }

    public static string SerializeResponse(AssistantResponse response) =>
        JsonSerializer.Serialize(response, OutputJsonOptions);
}
