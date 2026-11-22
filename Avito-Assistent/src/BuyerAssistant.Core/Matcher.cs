using System.Text.RegularExpressions;

namespace BuyerAssistant.Core;

/// <summary>
/// Детерминированный подбор: фильтры по городу и характеристикам, перебор комплектов в рамках бюджета,
/// ранжирование по релевантности заголовка (ключевые слова покупателя) и выбор до 3 разнообразных вариантов.
/// </summary>
public static class BundleMatcher
{
    private static readonly Regex TokenRegex = new(@"[a-zа-яе0-9]+", RegexOptions.Compiled);

    private static bool Compare(string op, decimal actual, decimal wanted) => op switch
    {
        "lt" => actual < wanted,
        "lte" => actual <= wanted,
        "gt" => actual > wanted,
        "gte" => actual >= wanted,
        _ => actual == wanted
    };

    /// <summary>
    /// Проверяет соответствие объявления ограничениям по структурированным полям.
    /// Отсутствующее поле = неизвестное значение -> не подходит (CONTRACT.md §2).
    /// false и 0 являются валидными значениями, а не отсутствием ключа!
    /// </summary>
    public static bool Satisfies(Listing listing, IReadOnlyList<Constraint> constraints)
    {
        foreach (var c in constraints)
        {
            if (!DomainConstants.AttrCategory.TryGetValue(c.Attribute, out var targetCat) || targetCat != listing.Category)
                continue;

            if (!listing.Attributes.TryGetValue(c.Attribute, out var raw))
                return false;

            if (c.Attribute == "adjustable_height")
            {
                if (raw.Kind != AttrValueKind.Boolean || c.BoolValue is null || raw.BoolValue != c.BoolValue.Value)
                    return false;
            }
            else
            {
                if (raw.Kind != AttrValueKind.Number || c.NumericValue is null || !Compare(c.Op, raw.NumericValue, c.NumericValue.Value))
                    return false;
            }
        }
        return true;
    }

    public static string KeywordStem(string word)
    {
        var w = word.ToLowerInvariant().Replace('ё', 'е');
        if (w.Length <= 4) return w;
        return w.Length <= 6 ? w[..^2] : w[..^3];
    }

    /// <summary>
    /// Какие проверенные ключевые слова запроса встречаются в заголовке объявления.
    /// Заголовок не влияет на цену, бюджет, город или ограничения, а также не попадает в текст ответа.
    /// </summary>
    public static List<string> MatchedKeywords(Listing listing, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0) return [];
        var truncatedTitle = listing.Title.Length <= 200 ? listing.Title : listing.Title[..200];
        var normTitle = truncatedTitle.ToLowerInvariant().Replace('ё', 'е');
        var tokens = TokenRegex.Matches(normTitle).Select(m => m.Value).ToList();

        var matched = new List<string>();
        foreach (var k in keywords)
        {
            var stem = KeywordStem(k);
            if (tokens.Any(t => t.StartsWith(stem, StringComparison.Ordinal)))
            {
                matched.Add(k);
            }
        }
        return matched;
    }

    public static Dictionary<string, (List<Listing> InCity, List<Listing> Ok)> CandidatesByCategory(
        IReadOnlyList<Listing> catalog,
        Conditions cond)
    {
        var result = new Dictionary<string, (List<Listing> InCity, List<Listing> Ok)>(StringComparer.Ordinal);
        foreach (var cat in cond.Categories)
        {
            var inCity = catalog.Where(x => x.Category == cat && x.CityId == cond.CityId).ToList();
            var ok = inCity
                .Where(x => Satisfies(x, cond.Constraints))
                .OrderBy(x => x.PriceRub)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .ToList();
            result[cat] = (inCity, ok);
        }
        return result;
    }

    public sealed record CandidateBundle(int TotalRub, string[] Ids, int Relevance);

    public static List<CandidateBundle> EnumerateSets(
        IReadOnlyList<List<Listing>> lists,
        int budgetRub,
        IReadOnlyDictionary<string, int>? relevanceById = null)
    {
        if (lists.Count == 0 || lists.Any(l => l.Count == 0) || budgetRub < 0)
            return [];

        var n = lists.Count;
        var tailMin = new long[n + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            tailMin[i] = tailMin[i + 1] + lists[i][0].PriceRub;
        }

        var output = new List<CandidateBundle>();
        var currentIds = new string[n];

        void Recurse(int idx, long runningTotal)
        {
            if (idx == n)
            {
                var relSum = 0;
                if (relevanceById is not null)
                {
                    foreach (var id in currentIds)
                    {
                        if (relevanceById.TryGetValue(id, out var r))
                            relSum += r;
                    }
                }
                output.Add(new CandidateBundle((int)runningTotal, (string[])currentIds.Clone(), relSum));
                return;
            }

            foreach (var item in lists[idx])
            {
                var nextTotal = runningTotal + item.PriceRub;
                if (nextTotal + tailMin[idx + 1] > budgetRub)
                    break; // списки отсортированы по цене по возрастанию

                currentIds[idx] = item.Id;
                Recurse(idx + 1, nextTotal);
            }
        }

        Recurse(0, 0);
        return output;
    }

    private static int CompareIdTuples(string[] a, string[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var cmp = string.CompareOrdinal(a[i], b[i]);
            if (cmp != 0) return cmp;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static List<CandidateBundle> PickOptions(IReadOnlyList<CandidateBundle> sets, int limit = 3)
    {
        if (sets.Count == 0 || limit <= 0)
            return [];

        // Первый вариант: максимум релевантности, при равенстве — минимальная цена, затем лексикографически ID
        var first = sets[0];
        for (var i = 1; i < sets.Count; i++)
        {
            var s = sets[i];
            if (CompareFirst(s, first) < 0)
                first = s;
        }

        var chosen = new List<CandidateBundle> { first };
        var pool = sets.Where(s => !ReferenceEquals(s, first)).ToList();

        while (pool.Count > 0 && chosen.Count < limit)
        {
            CandidateBundle best = pool[0];
            var bestDist = MinHammingDistance(best, chosen);

            for (var i = 1; i < pool.Count; i++)
            {
                var candidate = pool[i];
                var candDist = MinHammingDistance(candidate, chosen);
                if (CompareSubsequent(candidate, candDist, best, bestDist) < 0)
                {
                    best = candidate;
                    bestDist = candDist;
                }
            }

            chosen.Add(best);
            pool.Remove(best);
        }

        return chosen;
    }

    private static int MinHammingDistance(CandidateBundle candidate, List<CandidateBundle> chosen)
    {
        var minDist = int.MaxValue;
        foreach (var c in chosen)
        {
            var diff = 0;
            for (var i = 0; i < candidate.Ids.Length; i++)
            {
                if (!string.Equals(candidate.Ids[i], c.Ids[i], StringComparison.Ordinal))
                    diff++;
            }
            if (diff < minDist)
                minDist = diff;
        }
        return minDist;
    }

    private static int CompareFirst(CandidateBundle a, CandidateBundle b)
    {
        var relCmp = b.Relevance.CompareTo(a.Relevance); // -s.Relevance
        if (relCmp != 0) return relCmp;
        var priceCmp = a.TotalRub.CompareTo(b.TotalRub);
        if (priceCmp != 0) return priceCmp;
        return CompareIdTuples(a.Ids, b.Ids);
    }

    private static int CompareSubsequent(CandidateBundle a, int distA, CandidateBundle b, int distB)
    {
        var distCmp = distB.CompareTo(distA); // -dist
        if (distCmp != 0) return distCmp;
        return CompareFirst(a, b);
    }
}
