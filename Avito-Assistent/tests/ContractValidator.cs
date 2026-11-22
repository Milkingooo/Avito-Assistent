using System.Text.Json;
using BuyerAssistant.Core;

namespace BuyerAssistant.Tests;

/// <summary>
/// Независимый от BundleMatcher проверяющий контракта (раздел 4 CONTRACT.md и schemas/response.schema.json).
/// Работает с «сырыми» JsonElement каталога и ответа, без использования классов решения.
/// </summary>
public static class ContractValidator
{
    public sealed record WantSpec(
        string City,
        int Budget,
        string[] Cats,
        (string Attr, string Op, object Value)[] Cons);

    private static readonly Dictionary<string, string> AttrCat = new()
    {
        ["width_cm"] = "table",
        ["adjustable_height"] = "chair",
        ["color_temperature_k"] = "lamp"
    };

    public static void ValidateResponseSchema(string responseJson)
    {
        JsonDuplicateKeyChecker.ValidateStrictJson(System.Text.Encoding.UTF8.GetBytes(responseJson));
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("response должен быть JSON-объектом");

        if (!root.TryGetProperty("status", out var stEl) || stEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("нет строкового поля status");

        var status = stEl.GetString();
        var rootProps = root.EnumerateObject().Select(p => p.Name).ToHashSet();

        if (status == "ok")
        {
            if (!rootProps.SetEquals(["status", "options"]))
                throw new InvalidOperationException($"недопустимые поля для ok: {string.Join(",", rootProps)}");

            var opts = root.GetProperty("options");
            if (opts.ValueKind != JsonValueKind.Array || opts.GetArrayLength() < 1 || opts.GetArrayLength() > 3)
                throw new InvalidOperationException("options должен быть массивом длины 1..3");

            foreach (var opt in opts.EnumerateArray())
            {
                if (opt.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("элемент options должен быть объектом");
                var optProps = opt.EnumerateObject().Select(p => p.Name).ToHashSet();
                if (!optProps.SetEquals(["listing_ids", "total_rub", "explanation"]))
                    throw new InvalidOperationException($"недопустимые поля варианта: {string.Join(",", optProps)}");

                var ids = opt.GetProperty("listing_ids");
                if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() < 1 || ids.GetArrayLength() > 3)
                    throw new InvalidOperationException("listing_ids должен быть массивом 1..3");

                var seen = new HashSet<string>();
                foreach (var idEl in ids.EnumerateArray())
                {
                    if (idEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(idEl.GetString()))
                        throw new InvalidOperationException("listing_id должен быть непустой строкой");
                    if (!seen.Add(idEl.GetString()!))
                        throw new InvalidOperationException("повтор listing_id внутри варианта");
                }

                var tot = opt.GetProperty("total_rub");
                if (tot.ValueKind != JsonValueKind.Number || !tot.TryGetInt32(out var totVal) || totVal < 1)
                    throw new InvalidOperationException("total_rub должен быть целым >= 1");

                var exp = opt.GetProperty("explanation");
                if (exp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(exp.GetString()))
                    throw new InvalidOperationException("explanation должен быть непустой строкой");
            }
        }
        else if (status == "clarify")
        {
            if (!rootProps.SetEquals(["status", "questions"]))
                throw new InvalidOperationException($"недопустимые поля для clarify: {string.Join(",", rootProps)}");

            var qs = root.GetProperty("questions");
            if (qs.ValueKind != JsonValueKind.Array || qs.GetArrayLength() < 1 || qs.GetArrayLength() > 3)
                throw new InvalidOperationException("questions должен быть массивом длины 1..3");

            var allowedFields = new HashSet<string> { "budget_rub", "city_id", "categories" };
            var seenFields = new HashSet<string>();
            foreach (var q in qs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("вопрос должен быть объектом");
                var qProps = q.EnumerateObject().Select(p => p.Name).ToHashSet();
                if (!qProps.SetEquals(["field", "text"]))
                    throw new InvalidOperationException("вопрос должен иметь только field и text");

                var f = q.GetProperty("field").GetString() ?? "";
                if (!allowedFields.Contains(f))
                    throw new InvalidOperationException($"недопустимый field: {f}");
                if (!seenFields.Add(f))
                    throw new InvalidOperationException($"повтор field в clarify: {f}");

                var t = q.GetProperty("text");
                if (t.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(t.GetString()))
                    throw new InvalidOperationException("text вопроса пуст");
            }
        }
        else if (status == "no_match")
        {
            if (!rootProps.SetEquals(["status", "message"]))
                throw new InvalidOperationException($"недопустимые поля для no_match: {string.Join(",", rootProps)}");

            var msg = root.GetProperty("message");
            if (msg.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(msg.GetString()))
                throw new InvalidOperationException("message пуст");
        }
        else
        {
            throw new InvalidOperationException($"неизвестный status: {status}");
        }
    }

    private static bool Cmp(string op, decimal a, decimal b) => op switch
    {
        "lt" => a < b,
        "lte" => a <= b,
        "gt" => a > b,
        "gte" => a >= b,
        "eq" => a == b,
        _ => throw new ArgumentException(op)
    };

    private static bool RawRowSatisfies(JsonElement row, (string Attr, string Op, object Value)[] cons)
    {
        var cat = row.GetProperty("category").GetString()!;
        var attrs = row.GetProperty("attributes");
        foreach (var (attr, op, want) in cons)
        {
            if (AttrCat[attr] != cat) continue;
            if (!attrs.TryGetProperty(attr, out var v))
                return false;

            if (attr == "adjustable_height")
            {
                var wantBool = (bool)want;
                if (wantBool && v.ValueKind != JsonValueKind.True) return false;
                if (!wantBool && v.ValueKind != JsonValueKind.False) return false;
            }
            else
            {
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var decVal))
                    return false;
                var wantDec = Convert.ToDecimal(want, System.Globalization.CultureInfo.InvariantCulture);
                if (!Cmp(op, decVal, wantDec))
                    return false;
            }
        }
        return true;
    }

    private static List<JsonElement[]> BruteForceValidSets(List<JsonElement> validRows, WantSpec want)
    {
        var perCat = want.Cats.Select(cat =>
            validRows.Where(r =>
                r.GetProperty("category").GetString() == cat &&
                r.GetProperty("city_id").GetString() == want.City &&
                RawRowSatisfies(r, want.Cons)).ToList()).ToList();

        var results = new List<JsonElement[]>();
        if (perCat.Count == 0 || perCat.Any(l => l.Count == 0))
            return results;

        var current = new JsonElement[perCat.Count];
        void Rec(int idx, long sum)
        {
            if (idx == perCat.Count)
            {
                if (sum <= want.Budget)
                    results.Add((JsonElement[])current.Clone());
                return;
            }
            foreach (var r in perCat[idx])
            {
                var p = r.GetProperty("price_rub").GetInt32();
                if (sum + p <= want.Budget)
                {
                    current[idx] = r;
                    Rec(idx + 1, sum + p);
                }
            }
        }
        Rec(0, 0);
        return results;
    }

    private static List<JsonElement> FilterValidCatalogRows(JsonElement catalogArray)
    {
        var list = new List<JsonElement>();
        var seen = new HashSet<string>();
        var allowedCats = new HashSet<string> { "table", "chair", "lamp" };
        foreach (var row in catalogArray.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(idEl.GetString())) continue;
            var id = idEl.GetString()!;
            if (!seen.Add(id)) continue;
            if (!row.TryGetProperty("category", out var cEl) || cEl.ValueKind != JsonValueKind.String || !allowedCats.Contains(cEl.GetString()!)) continue;
            if (!row.TryGetProperty("title", out var tEl) || tEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tEl.GetString())) continue;
            if (!row.TryGetProperty("description", out var dEl) || dEl.ValueKind != JsonValueKind.String) continue;
            if (!row.TryGetProperty("price_rub", out var pEl) || pEl.ValueKind != JsonValueKind.Number || !pEl.TryGetInt32(out var p) || p <= 0) continue;
            if (!row.TryGetProperty("city_id", out var ciEl) || ciEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(ciEl.GetString())) continue;
            if (!row.TryGetProperty("attributes", out var aEl) || aEl.ValueKind != JsonValueKind.Object) continue;
            list.Add(row);
        }
        return list;
    }

    public static List<string> CheckResponse(
        AssistantResponse resp,
        JsonElement rawCatalogArray,
        WantSpec want,
        bool cheapestFirst = true)
    {
        var errors = new List<string>();
        var rows = FilterValidCatalogRows(rawCatalogArray);
        var byId = rows.ToDictionary(r => r.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var allowed = BruteForceValidSets(rows, want);

        if (resp.Status == "ok")
        {
            var opts = resp.Options ?? [];
            if (opts.Count < 1 || opts.Count > 3)
                errors.Add("options: должно быть от 1 до 3");

            var seenCompositions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var opt in opts)
            {
                var ids = opt.ListingIds;
                if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                    errors.Add($"повтор ID в варианте [{string.Join(",", ids)}]");

                if (ids.Any(i => !byId.ContainsKey(i)))
                {
                    errors.Add($"несуществующий ID в [{string.Join(",", ids)}]");
                    continue;
                }

                var items = ids.Select(i => byId[i]).ToList();
                var itemCats = items.Select(r => r.GetProperty("category").GetString()!).OrderBy(x => x).ToArray();
                var wantCats = want.Cats.OrderBy(x => x).ToArray();
                if (!itemCats.SequenceEqual(wantCats))
                    errors.Add($"состав категорий [{string.Join(",", ids)}] не совпадает с запросом [{string.Join(",", want.Cats)}]");

                foreach (var r in items)
                {
                    var rid = r.GetProperty("id").GetString()!;
                    var rCity = r.GetProperty("city_id").GetString()!;
                    if (rCity != want.City)
                        errors.Add($"{rid}: чужой город {rCity}");
                    if (!RawRowSatisfies(r, want.Cons))
                        errors.Add($"{rid}: не удовлетворяет ограничениям");
                }

                var total = items.Sum(r => r.GetProperty("price_rub").GetInt32());
                if (opt.TotalRub != total)
                    errors.Add($"total_rub {opt.TotalRub} != {total}");
                if (total > want.Budget)
                    errors.Add($"total {total} > бюджета {want.Budget}");
                if (string.IsNullOrWhiteSpace(opt.Explanation))
                    errors.Add("пустое explanation");

                var compKey = string.Join("|", ids.OrderBy(x => x, StringComparer.Ordinal));
                if (!seenCompositions.Add(compKey))
                    errors.Add($"варианты с одинаковым составом {compKey}");
            }

            if (allowed.Count == 0)
            {
                errors.Add("ok, хотя допустимых комплектов нет");
            }
            else if (cheapestFirst && opts.Count > 0)
            {
                var minAllowed = allowed.Min(c => c.Sum(r => r.GetProperty("price_rub").GetInt32()));
                if (opts[0].TotalRub != minAllowed)
                    errors.Add($"первый вариант {opts[0].TotalRub} не самый дешёвый ({minAllowed})");
            }
        }
        else if (resp.Status == "no_match")
        {
            if (allowed.Count > 0)
                errors.Add($"no_match, хотя допустимых комплектов {allowed.Count}");
            if (resp.Options is not null || resp.Questions is not null || string.IsNullOrWhiteSpace(resp.Message))
                errors.Add("no_match: лишние поля или пустое message");
        }

        return errors;
    }
}
