using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuyerAssistant.Core;

var builder = WebApplication.CreateBuilder(args);

// Принимаем любые хосты/origin прокси (0.0.0.0 и preview-поддомены)
builder.WebHost.ConfigureKestrel(options =>
{
    var portStr = Environment.GetEnvironmentVariable("PORT") ?? "5000";
    var port = int.TryParse(portStr, out var p) ? p : 5000;
    options.ListenAnyIP(port);
});

var app = builder.Build();

// Безопасность для встраивания в live-preview без X-Frame-Options: DENY и без блокировки по Host
app.UseDefaultFiles();
app.UseStaticFiles();

var jsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = false
};

// Определяем путь к репозиторию ядра (Avito-Assistent)
string ResolveRepoRoot()
{
    var envCatalog = Environment.GetEnvironmentVariable("CATALOG_PATH");
    if (!string.IsNullOrWhiteSpace(envCatalog) && File.Exists(envCatalog))
        return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(envCatalog))) ?? Directory.GetCurrentDirectory();

    var candidates = new[]
    {
        Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "Avito-Assistent")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Avito-Assistent")),
        "/home/user/Avito-Assistent"
    };

    return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "data", "catalog.json")))
           ?? candidates[0];
}

var repoRoot = ResolveRepoRoot();
var defaultCatalogPath = Environment.GetEnvironmentVariable("CATALOG_PATH")
                         ?? Path.Combine(repoRoot, "data", "catalog.json");

string ResolveEnvPath()
{
    var localEnv = Path.Combine(app.Environment.ContentRootPath, ".env");
    if (File.Exists(localEnv)) return localEnv;
    return Path.Combine(repoRoot, ".env");
}

// Кэш каталога с перестроением при изменении содержимого файла (CONTRACT.md §1)
var cacheLock = new object();
string? cachedHash = null;
List<Listing> cachedListings = [];
List<CatalogShowcaseItem> cachedShowcase = [];

(List<Listing> Listings, List<CatalogShowcaseItem> Showcase) GetCatalog(string path)
{
    var bytes = File.ReadAllBytes(path);
    var hash = Convert.ToHexString(SHA256.HashData(bytes));

    lock (cacheLock)
    {
        if (cachedHash == hash)
            return (cachedListings, cachedShowcase);

        var listings = CatalogLoader.ParseBytes(bytes, path);
        var showcase = CatalogLoader.LoadShowcase(path);
        cachedHash = hash;
        cachedListings = listings;
        cachedShowcase = showcase;
        return (listings, showcase);
    }
}

app.MapGet("/api/health", () =>
{
    var settings = LlmSettings.Load(ResolveEnvPath());
    var hasKey = !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.BaseUrl);
    return Results.Json(new
    {
        ok = true,
        catalog_path = defaultCatalogPath,
        llm_configured = hasKey,
        llm_model = settings.Model,
        fallback_enabled = Environment.GetEnvironmentVariable("LLM_FALLBACK") != "0"
    }, jsonOptions);
});

app.MapGet("/api/catalog", (string? city_id, string? category) =>
{
    var (_, showcase) = GetCatalog(defaultCatalogPath);
    IEnumerable<CatalogShowcaseItem> query = showcase;
    if (!string.IsNullOrWhiteSpace(city_id))
        query = query.Where(x => x.CityId == city_id);
    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(x => x.Category == category);

    var items = query.Select(x => new
    {
        id = x.Id,
        category = x.Category,
        category_name = DomainConstants.Categories.GetValueOrDefault(x.Category, x.Category),
        title = x.Title,
        description = x.Description,
        price_rub = x.PriceRub,
        city_id = x.CityId,
        city_name = DomainConstants.Cities.GetValueOrDefault(x.CityId, x.CityId),
        attributes = x.RawAttributes
    }).ToList();

    return Results.Json(new
    {
        cities = DomainConstants.CityOrder.Select(id => new { id, name = DomainConstants.Cities[id] }),
        categories = DomainConstants.CategoryOrder.Select(id => new { id, name = DomainConstants.Categories[id] }),
        total = items.Count,
        items
    }, jsonOptions);
});

// Строгий эндпоинт контракта: вход schemas/request.schema.json -> выход schemas/response.schema.json
app.MapPost("/api/contract", async (HttpContext ctx) =>
{
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var rawBytes = ms.ToArray();

    List<ChatMessage> messages;
    List<Listing> catalog;
    try
    {
        messages = AssistantPipeline.ValidateRequestBytes(rawBytes);
        (catalog, _) = GetCatalog(defaultCatalogPath);
    }
    catch (Exception ex) when (ex is RequestValidationException or CatalogException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 400);
    }

    var allowFallback = Environment.GetEnvironmentVariable("LLM_FALLBACK") != "0";
    try
    {
        var settings = LlmSettings.Load(ResolveEnvPath());
        var response = AssistantPipeline.Process(
            messages,
            catalog,
            settings,
            DateTimeOffset.UtcNow.AddSeconds(100),
            log: msg => Console.Error.WriteLine($"[contract] {msg}"),
            allowFallback: allowFallback);

        var json = AssistantPipeline.SerializeResponse(response);
        return Results.Content(json, "application/json; charset=utf-8");
    }
    catch (LlmException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

// Расширенный эндпоинт для интерактивного Web-интерфейса (возвращает и чистый ответ по CONTRACT.md, и обогащённые карточки каталога + диагностику)
app.MapPost("/api/chat", async (HttpContext ctx) =>
{
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var rawBytes = ms.ToArray();

    List<ChatMessage> messages;
    List<Listing> catalog;
    List<CatalogShowcaseItem> showcase;
    try
    {
        messages = AssistantPipeline.ValidateRequestBytes(rawBytes);
        (catalog, showcase) = GetCatalog(defaultCatalogPath);
    }
    catch (Exception ex) when (ex is RequestValidationException or CatalogException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 400);
    }

    var logs = new List<string>();
    var allowFallback = Environment.GetEnvironmentVariable("LLM_FALLBACK") != "0";
    try
    {
        var settings = LlmSettings.Load(ResolveEnvPath());
        var (contractResponse, extracted) = AssistantPipeline.ProcessWithConditions(
            messages,
            catalog,
            settings,
            DateTimeOffset.UtcNow.AddSeconds(100),
            log: msg => logs.Add(msg),
            allowFallback: allowFallback);

        var byId = showcase.ToDictionary(x => x.Id, StringComparer.Ordinal);
        string assistantReplyText;
        if (contractResponse.Status == "clarify")
        {
            assistantReplyText = string.Join(" ", contractResponse.Questions!.Select(q => q.Text));
        }
        else if (contractResponse.Status == "no_match")
        {
            assistantReplyText = contractResponse.Message!;
        }
        else
        {
            assistantReplyText = string.Join(" ", contractResponse.Options!.Select((o, idx) =>
                $"Вариант {idx + 1}: {string.Join(", ", o.ListingIds)}, {o.TotalRub} ₽."));
        }

        var enrichedOptions = contractResponse.Options?.Select(opt => new
        {
            listing_ids = opt.ListingIds,
            total_rub = opt.TotalRub,
            explanation = opt.Explanation,
            listings = opt.ListingIds
                .Where(id => byId.ContainsKey(id))
                .Select(id =>
                {
                    var item = byId[id];
                    return new
                    {
                        id = item.Id,
                        category = item.Category,
                        category_name = DomainConstants.Categories.GetValueOrDefault(item.Category, item.Category),
                        title = item.Title,
                        description = item.Description,
                        price_rub = item.PriceRub,
                        city_id = item.CityId,
                        city_name = DomainConstants.Cities.GetValueOrDefault(item.CityId, item.CityId),
                        attributes = item.RawAttributes
                    };
                }).ToList()
        }).ToList();

        return Results.Json(new
        {
            contract_response = contractResponse,
            assistant_text = assistantReplyText,
            extracted = new
            {
                city_id = extracted.CityId,
                city_name = extracted.CityId is not null ? DomainConstants.Cities.GetValueOrDefault(extracted.CityId) : null,
                city_problem = extracted.CityProblem,
                budget_rub = extracted.BudgetRub,
                categories = extracted.Categories,
                category_problem = extracted.CategoryProblem,
                keywords = extracted.Keywords,
                constraints = extracted.Constraints.Select(c => new
                {
                    attribute = c.Attribute,
                    op = c.Op,
                    value = c.Attribute == "adjustable_height" ? (object?)c.BoolValue : c.NumericValue
                }).ToList()
            },
            enriched_options = enrichedOptions,
            logs
        }, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        });
    }
    catch (LlmException ex)
    {
        return Results.Json(new { error = $"Сервис модели недоступен: {ex.Message}", logs }, statusCode: 502);
    }
});

app.Run();
