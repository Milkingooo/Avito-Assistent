using System.Text;
using BuyerAssistant.Core;

namespace BuyerAssistant.Cli;

/// <summary>
/// Точка входа контракта:
/// ./run --catalog /absolute/path/catalog.json &lt; request.json &gt; response.json
/// Также поддерживает интерактивный демо-чат: ./run --chat [--catalog path] [--verbose]
/// </summary>
public static class Program
{
    private const int TimeBudgetSeconds = 100; // CONTRACT.md §1: лимит 120 с на запуск, держим запас

    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        string? catalogPath = null;
        var chatMode = false;
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--catalog")
            {
                if (i + 1 >= args.Length)
                {
                    LogError("ошибка аргументов: после --catalog ожидается путь к файлу");
                    return 2;
                }
                catalogPath = args[++i];
            }
            else if (arg.StartsWith("--catalog=", StringComparison.Ordinal))
            {
                catalogPath = arg["--catalog=".Length..];
            }
            else if (arg == "--chat")
            {
                chatMode = true;
            }
            else if (arg is "--verbose" or "-v")
            {
                verbose = true;
            }
            else
            {
                LogError($"неизвестный аргумент: {arg}");
                return 2;
            }
        }

        if (chatMode)
        {
            catalogPath ??= Path.Combine(Directory.GetCurrentDirectory(), "data", "catalog.json");
            return RunInteractiveChat(catalogPath, verbose);
        }

        if (string.IsNullOrWhiteSpace(catalogPath))
        {
            LogError("ошибка аргументов: укажите --catalog <путь_к_каталогу>");
            return 2;
        }

        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(TimeBudgetSeconds);

        List<ChatMessage> messages;
        List<Listing> catalog;
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            stdin.CopyTo(ms);
            messages = AssistantPipeline.ValidateRequestBytes(ms.ToArray());
            catalog = CatalogLoader.Load(catalogPath, LogError);
        }
        catch (Exception ex) when (ex is RequestValidationException or CatalogException or IOException)
        {
            LogError($"ошибка входных данных: {ex.Message}");
            return 2;
        }

        var allowFallback = Environment.GetEnvironmentVariable("LLM_FALLBACK") != "0";
        AssistantResponse response;
        try
        {
            var settings = LlmSettings.Load();
            response = AssistantPipeline.Process(messages, catalog, settings, deadlineUtc, LogError, allowFallback);
        }
        catch (LlmException ex)
        {
            LogError($"ошибка модели: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            LogError($"внутренняя ошибка: {ex.Message}");
            return 1;
        }

        var json = AssistantPipeline.SerializeResponse(response) + "\n";
        var utf8Out = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(utf8Out, 0, utf8Out.Length);
        stdout.Flush();
        return 0;
    }

    private static void LogError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.Flush();
    }

    private static int RunInteractiveChat(string catalogPath, bool verbose)
    {
        List<Listing> catalog;
        try
        {
            catalog = CatalogLoader.Load(catalogPath, LogError);
        }
        catch (CatalogException ex)
        {
            LogError(ex.Message);
            return 2;
        }

        var byId = catalog.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var settings = LlmSettings.Load();
        Action<string>? log = verbose ? msg => LogError("  [лог] " + msg) : null;
        var history = new List<ChatMessage>();

        Console.WriteLine("ИИ-помощник покупателя (C#). Диалог запоминается; /new — начать заново; пустая строка или Ctrl+C — выход.\n");
        while (true)
        {
            Console.Write("Вы: ");
            var line = Console.ReadLine();
            if (line is null)
            {
                Console.WriteLine();
                return 0;
            }
            var text = line.Trim();
            if (string.IsNullOrEmpty(text))
                return 0;

            if (text == "/new")
            {
                history.Clear();
                Console.WriteLine("— новый диалог —");
                continue;
            }

            history.Add(new ChatMessage("user", text));
            AssistantResponse response;
            try
            {
                response = AssistantPipeline.Process(history, catalog, settings, DateTimeOffset.UtcNow.AddSeconds(100), log);
            }
            catch (LlmException ex)
            {
                Console.WriteLine($"Помощник: сервис модели недоступен ({ex.Message})");
                history.RemoveAt(history.Count - 1);
                continue;
            }

            string reply;
            if (response.Status == "clarify")
            {
                reply = string.Join(" ", response.Questions!.Select(q => q.Text));
                Console.WriteLine("Помощник: " + reply);
            }
            else if (response.Status == "no_match")
            {
                reply = response.Message!;
                Console.WriteLine("Помощник: " + reply);
            }
            else
            {
                Console.WriteLine("Помощник: нашёл подходящие комплекты.");
                var lines = new List<string>();
                for (var i = 0; i < response.Options!.Count; i++)
                {
                    var opt = response.Options[i];
                    Console.WriteLine($"\n  Вариант {i + 1} — {ResponseBuilder.FormatRub(opt.TotalRub)}");
                    foreach (var lid in opt.ListingIds)
                    {
                        var item = byId[lid];
                        Console.WriteLine($"    • [{lid}] {item.Title} — {ResponseBuilder.FormatRub(item.PriceRub)}");
                    }
                    Console.WriteLine("    " + opt.Explanation);
                    lines.Add($"Вариант {i + 1}: {string.Join(", ", opt.ListingIds)}, {opt.TotalRub} ₽");
                }
                reply = string.Join(" ", lines);
                Console.WriteLine();
            }

            history.Add(new ChatMessage("assistant", reply));
        }
    }
}
