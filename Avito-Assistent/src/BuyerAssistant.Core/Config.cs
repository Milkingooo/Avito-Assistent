using System.Text;

namespace BuyerAssistant.Core;

/// <summary>
/// Настройки подключения к модели: переменные окружения &gt; локальный .env (в Git не попадает).
/// В коде нет ни рабочего ключа, ни фиксированного URL шлюза (CONTRACT.md §1, LLM_API.md).
/// </summary>
public sealed class LlmSettings
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "google/gemma-4-31B-it";
    public string ApiKey { get; set; } = "";
    public string UserAgent { get; set; } = "hackathon-team/1.0";

    private static readonly string[] KnownKeys = ["LLM_BASE_URL", "LLM_MODEL", "LLM_API_KEY", "LLM_USER_AGENT"];

    public static LlmSettings Load(string? envFilePath = null)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LLM_BASE_URL"] = "",
            ["LLM_MODEL"] = "google/gemma-4-31B-it",
            ["LLM_API_KEY"] = "",
            ["LLM_USER_AGENT"] = "hackathon-team/1.0"
        };

        var candidatePaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(envFilePath))
        {
            candidatePaths.Add(envFilePath);
        }
        candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
        candidatePaths.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Avito-Assistent", ".env")));
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
            candidatePaths.Add(Path.Combine(exeDir, ".env"));
            candidatePaths.Add(Path.GetFullPath(Path.Combine(exeDir, "..", ".env")));
        }
        candidatePaths.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".env")));
        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, ".env"));

        foreach (var path in candidatePaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                    var eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0) continue;
                    var key = line[..eqIdx].Trim();
                    var val = line[(eqIdx + 1)..].Trim().Trim('"').Trim('\'');
                    if (KnownKeys.Contains(key) && !string.IsNullOrEmpty(val))
                    {
                        map[key] = val;
                    }
                }
                break;
            }
            catch (IOException)
            {
                // игнорируем недоступный .env
            }
        }

        // Переменные окружения всегда приоритетнее .env (проверяющий передает свои значения в финальной проверке)
        foreach (var key in KnownKeys)
        {
            var envVal = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(envVal))
            {
                map[key] = envVal;
            }
        }

        return new LlmSettings
        {
            BaseUrl = map["LLM_BASE_URL"],
            Model = map["LLM_MODEL"],
            ApiKey = map["LLM_API_KEY"],
            UserAgent = map["LLM_USER_AGENT"]
        };
    }
}
