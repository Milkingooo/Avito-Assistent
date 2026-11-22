using System.Text.Json.Serialization;

namespace BuyerAssistant.Core;

/// <summary>
/// Справочники и модели условий покупателя (общие для LLM-разбора и fallback-парсера).
/// </summary>
public static class DomainConstants
{
    public static readonly IReadOnlyDictionary<string, string> Cities = new Dictionary<string, string>
    {
        ["taganrog"] = "Таганрог",
        ["rostov-na-donu"] = "Ростов-на-Дону",
        ["azov"] = "Азов"
    };

    public static readonly string[] CityOrder = ["taganrog", "rostov-na-donu", "azov"];

    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["table"] = "Стол",
        ["chair"] = "Кресло",
        ["lamp"] = "Лампа"
    };

    public static readonly string[] CategoryOrder = ["table", "chair", "lamp"];

    public static readonly IReadOnlyDictionary<string, string> AttrCategory = new Dictionary<string, string>
    {
        ["width_cm"] = "table",
        ["adjustable_height"] = "chair",
        ["color_temperature_k"] = "lamp"
    };

    public static readonly IReadOnlyDictionary<string, string> AttrByCategory = new Dictionary<string, string>
    {
        ["table"] = "width_cm",
        ["chair"] = "adjustable_height",
        ["lamp"] = "color_temperature_k"
    };

    public static readonly IReadOnlyDictionary<string, string> AttrLabel = new Dictionary<string, string>
    {
        ["width_cm"] = "ширина",
        ["adjustable_height"] = "регулировка высоты",
        ["color_temperature_k"] = "цветовая температура"
    };

    public static readonly IReadOnlyDictionary<string, string> AttrUnit = new Dictionary<string, string>
    {
        ["width_cm"] = "см",
        ["color_temperature_k"] = "К"
    };

    public static readonly HashSet<string> AllowedOps = ["lt", "lte", "gt", "gte", "eq"];
}

public sealed record Constraint(
    string Attribute,
    string Op,
    decimal? NumericValue = null,
    bool? BoolValue = null)
{
    public static Constraint Numeric(string attribute, string op, decimal value) =>
        new(attribute, op, NumericValue: value, BoolValue: null);

    public static Constraint Boolean(string attribute, bool value) =>
        new(attribute, "eq", NumericValue: null, BoolValue: value);

    public bool EqualsValue(Constraint? other)
    {
        if (other is null) return false;
        return Attribute == other.Attribute
            && Op == other.Op
            && NumericValue == other.NumericValue
            && BoolValue == other.BoolValue;
    }
}

public sealed class Conditions
{
    public string? CityId { get; set; }
    public bool CityProblem { get; set; }
    public int? BudgetRub { get; set; }
    public List<string> Categories { get; set; } = [];
    public bool CategoryProblem { get; set; }
    public List<Constraint> Constraints { get; set; } = [];
    public List<string> Keywords { get; set; } = [];

    public static List<ClarifyQuestion> GetMissingQuestions(Conditions cond)
    {
        var cities = string.Join(", ", DomainConstants.CityOrder.Select(id => DomainConstants.Cities[id]));
        var cats = string.Join(", ", DomainConstants.CategoryOrder.Select(id => DomainConstants.Categories[id].ToLowerInvariant()));
        var questions = new List<ClarifyQuestion>();

        if (cond.BudgetRub is null)
        {
            questions.Add(new ClarifyQuestion("budget_rub", "Какой у вас бюджет на весь комплект (в рублях)?"));
        }

        if (cond.CityId is null)
        {
            var text = cond.CityProblem
                ? $"Не удалось однозначно определить город: в каталоге есть только {cities}. В каком из них вы хотите купить?"
                : $"В каком городе вы хотите купить товары? Доступны: {cities}.";
            questions.Add(new ClarifyQuestion("city_id", text));
        }

        if (cond.Categories.Count == 0 || cond.CategoryProblem)
        {
            var text = cond.CategoryProblem
                ? $"Часть названных товаров в каталоге отсутствует. Доступны категории: {cats}. Что из этого вам нужно?"
                : $"Какие товары вам нужны? Доступны категории: {cats}.";
            questions.Add(new ClarifyQuestion("categories", text));
        }

        return questions;
    }
}

public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("text")] string Text);

public sealed record ClarifyQuestion(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("text")] string Text);

public sealed record BundleOption(
    [property: JsonPropertyName("listing_ids")] List<string> ListingIds,
    [property: JsonPropertyName("total_rub")] int TotalRub,
    [property: JsonPropertyName("explanation")] string Explanation);

public sealed class AssistantResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BundleOption>? Options { get; init; }

    [JsonPropertyName("questions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ClarifyQuestion>? Questions { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    public static AssistantResponse Clarify(List<ClarifyQuestion> questions) =>
        new() { Status = "clarify", Questions = questions };

    public static AssistantResponse Ok(List<BundleOption> options) =>
        new() { Status = "ok", Options = options };

    public static AssistantResponse NoMatch(string message) =>
        new() { Status = "no_match", Message = message };
}
