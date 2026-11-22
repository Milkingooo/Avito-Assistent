using System.Globalization;
using System.Text.RegularExpressions;

namespace BuyerAssistant.Core;

/// <summary>
/// Резервный разбор диалога правилами и детерминированный помощник для grounding ответа модели.
/// Покрывает все формулировки из CONTRACT.md.
/// </summary>
public static class HeuristicParser
{
    // Границы и числовой паттерн (аналог Python r"\d{1,3}(?:[ \u00A0]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?")
    public const string NumPattern = @"\d{1,3}(?:[ \u00A0]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?";
    public const string BoundaryLeft = @"(?<![а-яёa-z\d])";
    public const string BoundaryRight = @"(?![а-яёa-z])";

    private const string OpWords =
        @"(?:ширин\w*\s*)?(?:(?:не\s+)?(?:шире|уже|более|менее|больше|меньше|выше|ниже)|не\s+(?:более|менее|больше|меньше)|максимум|минимум|от|до)?";

    public static readonly Regex NumRegex = new(NumPattern, RegexOptions.Compiled);

    private static readonly Regex RangeRegex = new(
        BoundaryLeft + $@"от\s*(?<a>{NumPattern})\s*(?:[а-яёa-z]+\s*)?до\s*(?<b>{NumPattern})\s*(?<unit>см|cm|мм|м|метр\w*|к|k|кельвин\w*){BoundaryRight}",
        RegexOptions.Compiled);

    private static readonly Regex LengthRegex = new(
        BoundaryLeft + $@"(?<op>{OpWords})\s*(?<half>пол)?\s*(?<num>{NumPattern})?\s*(?<unit>см|cm|сантиметр\w*|мм|м|метр\w*){BoundaryRight}",
        RegexOptions.Compiled);

    private static readonly Regex TempRegex = new(
        BoundaryLeft + $@"(?<op>{OpWords})\s*(?<num>{NumPattern})\s*(?<unit>к|k|кельвин\w*){BoundaryRight}",
        RegexOptions.Compiled);

    public static readonly IReadOnlyDictionary<string, Regex> CityStemRegexes = new Dictionary<string, Regex>
    {
        ["taganrog"] = new(BoundaryLeft + @"(?:таганрог|таганрож)", RegexOptions.Compiled),
        ["rostov-na-donu"] = new(BoundaryLeft + @"(?:ростов|рнд)", RegexOptions.Compiled),
        ["azov"] = new(BoundaryLeft + @"азов", RegexOptions.Compiled)
    };

    private static readonly Regex OtherCityHintRegex = new(
        BoundaryLeft + @"(?:москв\w*|петербург\w*|питер\w*|казан\w*|новосибирск\w*|екатеринбург\w*|краснодар\w*|сочи|воронеж\w*|самар\w*|нижн\w*\s+новгород\w*|батайск\w*|шахт\w*|новочеркасск\w*|волгодонск\w*)",
        RegexOptions.Compiled);

    public static readonly Regex UnsupportedCategoryRegex = new(
        BoundaryLeft + @"(?:стул\w*|табурет\w*|диван\w*|шкаф\w*|кроват\w*|стеллаж\w*|комод\w*|люстр\w*|пуфик\w*|пуф\w*|тумб\w*|кресло-мешок)",
        RegexOptions.Compiled);

    public static readonly IReadOnlyDictionary<string, Regex> CategoryStemRegexes = new Dictionary<string, Regex>
    {
        ["table"] = new(BoundaryLeft + @"стол", RegexOptions.Compiled),
        ["chair"] = new(BoundaryLeft + @"кресл", RegexOptions.Compiled),
        ["lamp"] = new(BoundaryLeft + @"(?:ламп|светильник|торшер)", RegexOptions.Compiled)
    };

    private static readonly (string Suffix, string Op)[] OpTable =
    [
        ("не шире", "lte"),
        ("не выше", "lte"),
        ("не более", "lte"),
        ("не больше", "lte"),
        ("максимум", "lte"),
        ("до", "lte"),
        ("не уже", "gte"),
        ("не менее", "gte"),
        ("не меньше", "gte"),
        ("минимум", "gte"),
        ("от", "gte"),
        ("уже", "lt"),
        ("меньше", "lt"),
        ("менее", "lt"),
        ("ниже", "lt"),
        ("шире", "gt"),
        ("больше", "gt"),
        ("более", "gt"),
        ("выше", "gt")
    ];

    private static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingWidthRegex = new(@"^ширин\w*\s*", RegexOptions.Compiled);
    private static readonly Regex BareNumMessageRegex = new($@"^\s*(?:{NumPattern})\s*$", RegexOptions.Compiled);
    private static readonly Regex KThousandRegex = new($@"^\s*(?:тыс|т\.?\s?р{BoundaryRight}|[кk]{BoundaryRight})", RegexOptions.Compiled);
    private static readonly Regex MillionRegex = new(@"^\s*(?:млн|миллион)", RegexOptions.Compiled);
    private static readonly Regex RubAfterRegex = new($@"^\s*(?:₽|руб|р\.?{BoundaryRight})", RegexOptions.Compiled);
    private static readonly Regex BudgetBeforeRegex = new(@"(?:бюджет\w*|до|за\s+все?|не\s+более|не\s+дороже|максимум|в\s+пределах)\W*$", RegexOptions.Compiled);
    private static readonly Regex NoAdjustRegex = new(@"без\s+регулир|не\s+регулир|нерегулир", RegexOptions.Compiled);
    private static readonly Regex AdjustRegex = new(@"регулир", RegexOptions.Compiled);

    /// <summary>
    /// Признак ДОБАВЛЕНИЯ товара к уже собранному комплекту («ещё лампу», «добавь кресло», «плюс лампа», «и ещё»).
    /// </summary>
    public static readonly Regex AdditiveCategoryRegex = new(
        BoundaryLeft + @"(?:еще|добав\w*|плюс|дополнительно|к\s+нему|к\s+ней|к\s+ним|также)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Признак УДАЛЕНИЯ конкретной категории из комплекта («без лампы», «убери кресло», «кроме стола»).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Regex> RemoveCategoryRegexes = new Dictionary<string, Regex>
    {
        ["table"] = new(BoundaryLeft + @"(?:без|убер\w*|удал\w*|исключ\w*|не\s+нуж\w*|кроме)\s+(?:[а-яё]+\s+)?стол\w*", RegexOptions.Compiled),
        ["chair"] = new(BoundaryLeft + @"(?:без|убер\w*|удал\w*|исключ\w*|не\s+нуж\w*|кроме)\s+(?:[а-яё]+\s+)?кресл\w*", RegexOptions.Compiled),
        ["lamp"] = new(BoundaryLeft + @"(?:без|убер\w*|удал\w*|исключ\w*|не\s+нуж\w*|кроме)\s+(?:[а-яё]+\s+)?(?:ламп\w*|светильник\w*|торшер\w*)", RegexOptions.Compiled)
    };

    /// <summary>
    /// Признак явной ЗАМЕНЫ набора категорий («вместо», «теперь найди», «найди другой», «только кресло», «давай лучше лампу»).
    /// </summary>
    public static readonly Regex ExplicitReplaceCategoryRegex = new(
        BoundaryLeft + @"(?:вместо|замен\w*|друг(?:ой|ую|ое|ие)|теперь|заново|сначала|передумал\w*|только|лишь|(?:найди|подбери|покажи|ищу|нуж(?:ен|на|но|ны)|хочу)\b|(?:давай|лучше)\s+(?:просто\s+|только\s+)?(?:стол|кресл|ламп|светильник|торшер))",
        RegexOptions.Compiled);

    public static string NormalizeText(string input) =>
        SpacesRegex.Replace(input.ToLowerInvariant().Replace('ё', 'е'), " ").Trim();

    public static decimal ParseDecimal(string text)
    {
        var cleaned = text.Replace(" ", "").Replace("\u00A0", "").Replace(',', '.');
        return decimal.Parse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string ResolveOp(string? rawOp)
    {
        var normalized = SpacesRegex.Replace((rawOp ?? "").Trim(), " ");
        normalized = LeadingWidthRegex.Replace(normalized, "");
        foreach (var (suffix, op) in OpTable)
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
                return op;
        }
        return "eq";
    }

    private static string MaskSpan(string text, int index, int length)
    {
        return string.Concat(text.AsSpan(0, index), new string(' ', length), text.AsSpan(index + length));
    }

    private static decimal UnitFactorCm(string unit)
    {
        if (unit.StartsWith("мм", StringComparison.Ordinal)) return 0.1m;
        if (unit is "см" or "cm" || unit.StartsWith("сантиметр", StringComparison.Ordinal)) return 1m;
        return 100m; // м, метр...
    }

    public static (List<Constraint> Constraints, string MaskedText) ExtractConstraints(string text)
    {
        var found = new List<Constraint>();

        foreach (Match m in RangeRegex.Matches(text))
        {
            var unit = m.Groups["unit"].Value;
            var a = ParseDecimal(m.Groups["a"].Value);
            var b = ParseDecimal(m.Groups["b"].Value);
            if (unit is "к" or "k" || unit.StartsWith("кельвин", StringComparison.Ordinal))
            {
                if (a >= 1000m)
                {
                    found.Add(Constraint.Numeric("color_temperature_k", "gte", a));
                    found.Add(Constraint.Numeric("color_temperature_k", "lte", b));
                    text = MaskSpan(text, m.Index, m.Length);
                }
            }
            else
            {
                var k = UnitFactorCm(unit);
                found.Add(Constraint.Numeric("width_cm", "gte", a * k));
                found.Add(Constraint.Numeric("width_cm", "lte", b * k));
                text = MaskSpan(text, m.Index, m.Length);
            }
        }

        foreach (Match m in TempRegex.Matches(text))
        {
            var val = ParseDecimal(m.Groups["num"].Value);
            if (val >= 1000m) // «15к» — это тысячи рублей, а не кельвины
            {
                found.Add(Constraint.Numeric("color_temperature_k", ResolveOp(m.Groups["op"].Value), val));
                text = MaskSpan(text, m.Index, m.Length);
            }
        }

        foreach (Match m in LengthRegex.Matches(text))
        {
            var unit = m.Groups["unit"].Value;
            decimal val;
            if (m.Groups["num"].Success && m.Groups["num"].Length > 0)
            {
                val = ParseDecimal(m.Groups["num"].Value) * UnitFactorCm(unit);
            }
            else if (unit.StartsWith("метр", StringComparison.Ordinal))
            {
                val = m.Groups["half"].Success && m.Groups["half"].Length > 0 ? 50m : 100m;
            }
            else
            {
                continue;
            }

            found.Add(Constraint.Numeric("width_cm", ResolveOp(m.Groups["op"].Value), val));
            text = MaskSpan(text, m.Index, m.Length);
        }

        if (NoAdjustRegex.IsMatch(text))
        {
            found.Add(Constraint.Boolean("adjustable_height", false));
        }
        else if (AdjustRegex.IsMatch(text))
        {
            found.Add(Constraint.Boolean("adjustable_height", true));
        }

        return (found, text);
    }

    private static int? ExtractBudget(string text, bool bareMessage)
    {
        int? value = null;
        foreach (Match m in NumRegex.Matches(text))
        {
            var number = ParseDecimal(m.Value);
            var afterLen = Math.Min(12, text.Length - (m.Index + m.Length));
            var after = text.Substring(m.Index + m.Length, afterLen);
            var beforeStart = Math.Max(0, m.Index - 20);
            var before = text.Substring(beforeStart, m.Index - beforeStart);

            decimal? mult = null;
            if (KThousandRegex.IsMatch(after))
                mult = 1000m;
            else if (MillionRegex.IsMatch(after))
                mult = 1_000_000m;
            else if (RubAfterRegex.IsMatch(after) || BudgetBeforeRegex.IsMatch(before))
                mult = 1m;
            else if (bareMessage)
                mult = 1m;

            if (mult.HasValue)
            {
                var product = number * mult.Value;
                if (product >= 0 && product <= int.MaxValue)
                    value = (int)product;
            }
        }

        if (value is null)
        {
            var leftovers = NumRegex.Matches(text).Select(m => ParseDecimal(m.Value)).ToList();
            if (leftovers.Count == 1 && leftovers[0] >= 1000m && leftovers[0] <= int.MaxValue)
            {
                value = (int)leftovers[0];
            }
        }

        return value;
    }

    public static Conditions Parse(IReadOnlyList<ChatMessage> messages)
    {
        var cond = new Conditions();
        var activeCats = new List<string>();

        foreach (var message in messages)
        {
            if (message.Role != "user") continue;

            var text = NormalizeText(message.Text);
            var (constraints, rest) = ExtractConstraints(text);

            var matchedCities = CityStemRegexes
                .Where(kv => kv.Value.IsMatch(rest))
                .Select(kv => kv.Key)
                .ToList();

            if (matchedCities.Count == 1)
            {
                cond.CityId = matchedCities[0];
                cond.CityProblem = false;
            }
            else if (matchedCities.Count > 1)
            {
                cond.CityId = null;
                cond.CityProblem = true;
            }
            else if (OtherCityHintRegex.IsMatch(rest))
            {
                cond.CityId = null;
                cond.CityProblem = true;
            }

            var bare = BareNumMessageRegex.IsMatch(rest);
            var budget = ExtractBudget(rest, bare);
            if (budget.HasValue)
            {
                cond.BudgetRub = budget.Value;
            }

            // 1. Проверяем явное удаление категорий («без лампы», «убери кресло»)
            var removedInTurn = new HashSet<string>(StringComparer.Ordinal);
            var maskedForCats = rest;
            foreach (var (cat, remRx) in RemoveCategoryRegexes)
            {
                foreach (Match rm in remRx.Matches(maskedForCats))
                {
                    removedInTurn.Add(cat);
                    maskedForCats = MaskSpan(maskedForCats, rm.Index, rm.Length);
                }
            }

            if (removedInTurn.Count > 0)
            {
                activeCats.RemoveAll(c => removedInTurn.Contains(c));
                cond.Constraints.RemoveAll(c => removedInTurn.Contains(DomainConstants.AttrCategory[c.Attribute]));
            }

            // 2. Ищем категории, упомянутые в текущей реплике (в порядке появления в тексте)
            var mentionedWithPos = new List<(string Cat, int Index)>();
            foreach (var (cat, stemRegex) in CategoryStemRegexes)
            {
                var m = stemRegex.Match(maskedForCats);
                if (m.Success)
                {
                    mentionedWithPos.Add((cat, m.Index));
                }
            }
            // Если покупатель задал ограничение по характеристике («шире 90 см»), соответствующая категория тоже относится к реплике
            foreach (var c in constraints)
            {
                var attrCat = DomainConstants.AttrCategory[c.Attribute];
                if (mentionedWithPos.All(x => x.Cat != attrCat) && !removedInTurn.Contains(attrCat))
                {
                    mentionedWithPos.Add((attrCat, int.MaxValue - 1));
                }
            }

            var turnCats = mentionedWithPos
                .OrderBy(x => x.Index)
                .Select(x => x.Cat)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (turnCats.Count > 0)
            {
                if (activeCats.Count == 0)
                {
                    activeCats = turnCats;
                }
                else
                {
                    var isAdditive = AdditiveCategoryRegex.IsMatch(maskedForCats);
                    // Уточнение характеристики («стол не шире 90 см» или «и чтобы кресло было с регулировкой»):
                    // если категория упомянута ТОЛЬКО для задания своей характеристики и нет явных слов замены («теперь», «вместо», «только», «другой», «найди»)
                    var turnCatsWithConstraint = constraints
                        .Select(c => DomainConstants.AttrCategory[c.Attribute])
                        .ToHashSet(StringComparer.Ordinal);
                    var isConstraintSpecificationOnly = constraints.Count > 0
                        && turnCats.All(tc => turnCatsWithConstraint.Contains(tc))
                        && !ExplicitReplaceCategoryRegex.IsMatch(maskedForCats);

                    if (isAdditive || isConstraintSpecificationOnly)
                    {
                        foreach (var tc in turnCats)
                        {
                            if (!activeCats.Contains(tc, StringComparer.Ordinal))
                                activeCats.Add(tc);
                        }
                    }
                    else
                    {
                        // Покупатель назвал другой предмет / новый состав — старые предметы забываются!
                        activeCats = turnCats;
                        cond.Constraints.RemoveAll(c => !activeCats.Contains(DomainConstants.AttrCategory[c.Attribute], StringComparer.Ordinal));
                    }
                }
            }

            var replacedAttrs = constraints.Select(c => c.Attribute).ToHashSet(StringComparer.Ordinal);
            cond.Constraints.RemoveAll(c => replacedAttrs.Contains(c.Attribute));
            cond.Constraints.AddRange(constraints.Where(c =>
                activeCats.Count == 0 || activeCats.Contains(DomainConstants.AttrCategory[c.Attribute], StringComparer.Ordinal)));
        }

        cond.Categories = activeCats;

        var lastUser = messages.LastOrDefault(m => m.Role == "user")?.Text ?? "";
        var lastNorm = NormalizeText(lastUser);
        cond.CategoryProblem = StrictUnsupportedRegex.IsMatch(lastNorm);

        return cond;
    }

    public static readonly Regex StrictUnsupportedRegex = new(
        BoundaryLeft + @"(?:стул\w*|табурет\w*|диван\w*|шкаф\w*|кроват\w*|стеллаж\w*|комод\w*|люстр\w*|пуфик\w*|пуф\w*)",
        RegexOptions.Compiled);
}
