using System.Globalization;

namespace BuyerAssistant.Core;

/// <summary>
/// Сборка ответов ok и no_match. Тексты строятся строго по структурированным полям каталога —
/// ни заголовки, ни описания объявлений в объяснения не попадают (защита от инъекций и галлюцинаций).
/// </summary>
public static class ResponseBuilder
{
    private static readonly IReadOnlyDictionary<string, string> OpText = new Dictionary<string, string>
    {
        ["lt"] = "менее",
        ["lte"] = "не более",
        ["gt"] = "более",
        ["gte"] = "не менее",
        ["eq"] = "ровно"
    };

    public static string FormatRub(long amount)
    {
        var formatted = amount.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
        return $"{formatted} ₽";
    }

    public static string FormatNumber(decimal value)
    {
        if (value == decimal.Truncate(value))
        {
            return ((long)decimal.Truncate(value)).ToString(CultureInfo.InvariantCulture);
        }
        return (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
    }

    private static string ConstraintText(Constraint c)
    {
        if (c.Attribute == "adjustable_height")
        {
            return "регулировка высоты " + (c.BoolValue == true ? "есть" : "отсутствует");
        }
        return $"{DomainConstants.AttrLabel[c.Attribute]} {OpText[c.Op]} {FormatNumber(c.NumericValue!.Value)} {DomainConstants.AttrUnit[c.Attribute]}";
    }

    private static string DescribeItem(Listing item, Conditions cond)
    {
        var text = $"{DomainConstants.Categories[item.Category]} {item.Id} — {FormatRub(item.PriceRub)}";
        var attr = DomainConstants.AttrByCategory[item.Category];

        string? fact = null;
        if (item.Attributes.TryGetValue(attr, out var raw))
        {
            if (attr == "adjustable_height" && raw.Kind == AttrValueKind.Boolean)
            {
                fact = raw.BoolValue ? "регулировка высоты есть" : "регулировки высоты нет";
            }
            else if (attr != "adjustable_height" && raw.Kind == AttrValueKind.Number)
            {
                fact = $"{DomainConstants.AttrLabel[attr]} {FormatNumber(raw.NumericValue)} {DomainConstants.AttrUnit[attr]}";
            }
        }

        if (fact is not null)
        {
            var wanted = cond.Constraints.Where(c => c.Attribute == attr).ToList();
            if (wanted.Count > 0)
            {
                var joined = string.Join("», «", wanted.Select(ConstraintText));
                fact += $" (требование «{joined}» выполнено по данным каталога)";
            }
            text += $": {fact}";
        }

        return text;
    }

    private static string KeywordNote(Listing item, Conditions cond)
    {
        var found = BundleMatcher.MatchedKeywords(item, cond.Keywords);
        if (found.Count == 0) return "";
        return " [название соответствует запросу: «" + string.Join("», «", found) + "»]";
    }

    public static AssistantResponse BuildOk(
        IReadOnlyList<Listing> catalog,
        Conditions cond,
        IReadOnlyList<BundleMatcher.CandidateBundle> chosen)
    {
        var byId = catalog.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var options = new List<BundleOption>(chosen.Count);
        var budget = cond.BudgetRub!.Value;
        var cityName = DomainConstants.Cities[cond.CityId!];

        for (var i = 0; i < chosen.Count; i++)
        {
            var bundle = chosen[i];
            var items = bundle.Ids.Select(id => byId[id]).ToList();
            var lead = i == 0
                ? (bundle.Relevance > 0
                    ? "Лучшее совпадение с запросом по ключевым словам среди подходящих комплектов."
                    : "Самый дешёвый из подходящих комплектов.")
                : "Другой набор товаров, тоже в рамках бюджета.";

            var parts = string.Join("; ", items.Select(x => DescribeItem(x, cond) + KeywordNote(x, cond)));
            var explanation = $"{lead} {parts}. Все товары в городе {cityName}. " +
                              $"Итого {FormatRub(bundle.TotalRub)} при бюджете {FormatRub(budget)} (остаётся {FormatRub(budget - bundle.TotalRub)}).";

            options.Add(new BundleOption(bundle.Ids.ToList(), bundle.TotalRub, explanation));
        }

        return AssistantResponse.Ok(options);
    }

    public static AssistantResponse Solve(IReadOnlyList<Listing> catalog, Conditions cond)
    {
        var perCat = BundleMatcher.CandidatesByCategory(catalog, cond);
        var lists = cond.Categories.Select(c => perCat[c].Ok).ToList();
        var relevance = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var lst in lists)
        {
            foreach (var item in lst)
            {
                relevance[item.Id] = BundleMatcher.MatchedKeywords(item, cond.Keywords).Count;
            }
        }

        var sets = BundleMatcher.EnumerateSets(lists, cond.BudgetRub!.Value, relevance);
        if (sets.Count > 0)
        {
            return BuildOk(catalog, cond, BundleMatcher.PickOptions(sets));
        }

        return AssistantResponse.NoMatch(ExplainNoMatch(catalog, cond, perCat));
    }

    private static string ExplainNoMatch(
        IReadOnlyList<Listing> catalog,
        Conditions cond,
        Dictionary<string, (List<Listing> InCity, List<Listing> Ok)> perCat)
    {
        var city = DomainConstants.Cities[cond.CityId!];
        var problems = new List<string>();

        foreach (var cat in cond.Categories)
        {
            var (inCity, ok) = perCat[cat];
            var name = DomainConstants.Categories[cat].ToLowerInvariant();
            if (inCity.Count == 0)
            {
                problems.Add($"в городе {city} в каталоге нет объявлений категории «{name}»");
            }
            else if (ok.Count == 0)
            {
                var wanted = cond.Constraints.Where(c => DomainConstants.AttrCategory[c.Attribute] == cat).ToList();
                var attr = DomainConstants.AttrByCategory[cat];
                var unknownCount = inCity.Count(x => !x.Attributes.ContainsKey(attr));
                var text = $"среди {inCity.Count} объявлений категории «{name}» в городе {city} нет подходящих под условие ({string.Join("; ", wanted.Select(ConstraintText))})";
                if (unknownCount > 0)
                {
                    text += $"; у {unknownCount} из них значение не указано в характеристиках, поэтому соответствие подтвердить нельзя";
                }
                problems.Add(text);
            }
        }

        if (problems.Count > 0)
        {
            const string tail = "Можно изменить или смягчить условие, выбрать другой город или убрать часть категорий.";
            return "Полный комплект не найден: " + string.Join("; ", problems) + ". " + tail;
        }

        var cheapest = cond.Categories.Sum(c => (long)perCat[c].Ok[0].PriceRub);
        var message = $"Подходящие товары есть, но самый дешёвый полный комплект стоит {FormatRub(cheapest)}, " +
                      $"а бюджет — {FormatRub(cond.BudgetRub!.Value)}. Можно увеличить бюджет минимум до {FormatRub(cheapest)}, " +
                      "ослабить ограничения или убрать часть категорий.";

        var altCities = CitiesWithFullSet(catalog, cond);
        if (altCities.Count > 0)
        {
            message += " Комплект в рамках бюджета есть также в другом городе: " +
                       string.Join(", ", altCities.Select(c => DomainConstants.Cities[c])) + ".";
        }

        return message;
    }

    private static List<string> CitiesWithFullSet(IReadOnlyList<Listing> catalog, Conditions cond)
    {
        var found = new List<string>();
        foreach (var cityId in DomainConstants.CityOrder)
        {
            if (cityId == cond.CityId) continue;
            var probe = new Conditions
            {
                CityId = cityId,
                BudgetRub = cond.BudgetRub,
                Categories = cond.Categories,
                Constraints = cond.Constraints
            };
            var perCat = BundleMatcher.CandidatesByCategory(catalog, probe);
            if (cond.Categories.All(c => perCat[c].Ok.Count > 0) &&
                cond.Categories.Sum(c => (long)perCat[c].Ok[0].PriceRub) <= cond.BudgetRub!.Value)
            {
                found.Add(cityId);
            }
        }
        return found;
    }
}
