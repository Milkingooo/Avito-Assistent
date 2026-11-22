using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BuyerAssistant.Core;

public sealed class CatalogException(string message, Exception? inner = null) : Exception(message, inner);

public enum AttrValueKind
{
    String,
    Number,
    Boolean,
    Invalid
}

public readonly record struct AttrValue(
    AttrValueKind Kind,
    decimal NumericValue = 0m,
    bool BoolValue = false,
    string? StringValue = null)
{
    public static AttrValue FromNumber(decimal d) => new(AttrValueKind.Number, NumericValue: d);
    public static AttrValue FromBool(bool b) => new(AttrValueKind.Boolean, BoolValue: b);
    public static AttrValue FromString(string s) => new(AttrValueKind.String, StringValue: s);
    public static AttrValue Invalid() => new(AttrValueKind.Invalid);
}

/// <summary>
/// Объявление из каталога. Описания (description) намеренно НЕ сохраняются в логику подбора,
/// чтобы исключить prompt injection и расхождения с структурированными полями (CONTRACT.md §2, §5).
/// </summary>
public sealed record Listing(
    string Id,
    string Category,
    string Title,
    int PriceRub,
    string CityId,
    IReadOnlyDictionary<string, AttrValue> Attributes);

/// <summary>
/// Полное представление объявления только для отображения витрины в Web UI (не передается в подбор и модель).
/// </summary>
public sealed record CatalogShowcaseItem(
    string Id,
    string Category,
    string Title,
    string Description,
    int PriceRub,
    string CityId,
    IReadOnlyDictionary<string, object?> RawAttributes);

public static class JsonDuplicateKeyChecker
{
    /// <summary>
    /// Проверяет отсутствие повторяющихся ключей объектов на всех уровнях JSON (CONTRACT.md §1).
    /// Также гарантирует строгий RFC 8259 без NaN/Infinity/комментариев/висячих запятых.
    /// </summary>
    public static void ValidateStrictJson(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });

        var objectKeyFrames = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectKeyFrames.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objectKeyFrames.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var prop = reader.GetString()!;
                    if (!objectKeyFrames.Peek().Add(prop))
                    {
                        throw new JsonException($"Повторяющийся ключ JSON-объекта: '{prop}'");
                    }
                    break;
            }
        }
    }
}

public static class CatalogLoader
{
    public static List<Listing> Load(string path, Action<string>? log = null)
    {
        byte[] rawBytes;
        try
        {
            rawBytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new CatalogException($"не удалось прочитать каталог {path}: {ex.Message}", ex);
        }

        return ParseBytes(rawBytes, path, log);
    }

    public static List<Listing> ParseBytes(byte[] rawBytes, string sourceName = "catalog.json", Action<string>? log = null)
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
            throw new CatalogException($"не удалось разобрать JSON каталога {sourceName}: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new CatalogException("каталог должен быть JSON-массивом объявлений");
            }

            var items = new List<Listing>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var problem = CheckRow(row, seen, out var listing);
                if (problem is not null)
                {
                    log?.Invoke($"каталог: запись #{index} пропущена ({problem})");
                }
                else if (listing is not null)
                {
                    seen.Add(listing.Id);
                    items.Add(listing);
                }
                index++;
            }

            return items;
        }
    }

    public static List<CatalogShowcaseItem> LoadShowcase(string path)
    {
        var rawBytes = File.ReadAllBytes(path);
        ReadOnlySpan<byte> span = rawBytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        using var doc = JsonDocument.Parse(span.ToArray());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<CatalogShowcaseItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (CheckRow(row, seen, out var listing) is not null || listing is null)
                continue;

            seen.Add(listing.Id);
            var desc = row.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.String
                ? dEl.GetString() ?? ""
                : "";

            var rawAttrs = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (row.TryGetProperty("attributes", out var aEl) && aEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in aEl.EnumerateObject())
                {
                    rawAttrs[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                        JsonValueKind.Number when prop.Value.TryGetDecimal(out var dec) => dec,
                        JsonValueKind.String => prop.Value.GetString(),
                        _ => null
                    };
                }
            }

            result.Add(new CatalogShowcaseItem(
                listing.Id,
                listing.Category,
                listing.Title,
                desc,
                listing.PriceRub,
                listing.CityId,
                rawAttrs));
        }

        return result;
    }

    private static string? CheckRow(JsonElement row, HashSet<string> seen, out Listing? listing)
    {
        listing = null;
        if (row.ValueKind != JsonValueKind.Object)
            return "не объект";

        if (!row.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return "некорректный id";
        var id = idEl.GetString()!;
        if (string.IsNullOrEmpty(id))
            return "некорректный id";
        if (seen.Contains(id))
            return $"повтор id {id}";

        if (!row.TryGetProperty("category", out var catEl) || catEl.ValueKind != JsonValueKind.String)
            return $"неизвестная категория у {id}";
        var category = catEl.GetString()!;
        if (!DomainConstants.Categories.ContainsKey(category))
            return $"неизвестная категория у {id}";

        if (!row.TryGetProperty("title", out var titleEl) || titleEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(titleEl.GetString()))
            return $"некорректный title у {id}";
        var title = titleEl.GetString()!;

        if (!row.TryGetProperty("description", out var descEl) || descEl.ValueKind != JsonValueKind.String)
            return $"отсутствует или некорректен description у {id}";

        if (!row.TryGetProperty("price_rub", out var priceEl) || priceEl.ValueKind != JsonValueKind.Number)
            return $"некорректная цена у {id}";
        if (!priceEl.TryGetInt32(out var priceRub) || priceRub <= 0)
        {
            // Проверяем также случай 100.0 vs 100.5: в JSON Schema integer это число без дробной части > 0
            if (priceEl.TryGetDecimal(out var decPrice) && decPrice > 0 && decPrice == decimal.Truncate(decPrice) && decPrice <= int.MaxValue)
            {
                priceRub = (int)decPrice;
            }
            else
            {
                return $"некорректная цена у {id}";
            }
        }

        if (!row.TryGetProperty("city_id", out var cityEl) || cityEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cityEl.GetString()))
            return $"некорректный city_id у {id}";
        var cityId = cityEl.GetString()!;

        if (!row.TryGetProperty("attributes", out var attrsEl) || attrsEl.ValueKind != JsonValueKind.Object)
            return $"attributes не объект у {id}";

        var attrs = new Dictionary<string, AttrValue>(StringComparer.Ordinal);
        foreach (var prop in attrsEl.EnumerateObject())
        {
            attrs[prop.Name] = ParseAttributeValue(prop.Value);
        }

        // description намеренно отбрасывается!
        listing = new Listing(id, category, title, priceRub, cityId, attrs);
        return null;
    }

    private static AttrValue ParseAttributeValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.True => AttrValue.FromBool(true),
            JsonValueKind.False => AttrValue.FromBool(false),
            JsonValueKind.String => AttrValue.FromString(el.GetString() ?? ""),
            JsonValueKind.Number => TryReadDecimal(el, out var d) ? AttrValue.FromNumber(d) : AttrValue.Invalid(),
            _ => AttrValue.Invalid()
        };
    }

    private static bool TryReadDecimal(JsonElement el, out decimal value)
    {
        if (el.TryGetDecimal(out value))
            return true;
        if (el.TryGetDouble(out var dbl) && double.IsFinite(dbl))
        {
            return decimal.TryParse(dbl.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        value = 0m;
        return false;
    }
}
