using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TarkovHelper.Services;

/// <summary>
/// Русские названия с фанатской вики: у английской статьи есть межъязыковая
/// ссылка на русскую («Fresh Stock» → «Обновление ассортимента»). Нужно для
/// контента, который вышел после обновления локали SPT.
/// </summary>
public static class WikiTitles
{
    private const string Api = "https://escapefromtarkov.fandom.com/api.php";
    private const int BatchSize = 50; // предел MediaWiki на количество заголовков

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.0");
        return c;
    }

    /// <summary>
    /// Возвращает «английский заголовок → русский». Отсутствующие переводы просто
    /// не попадают в результат; ошибки сети не считаются фатальными.
    /// Разобранные названия кешируются на диске: вики опрашивается только
    /// для новых заголовков.
    /// </summary>
    public static async Task<Dictionary<string, string>> ResolveAsync(
        IEnumerable<string> englishTitles, CancellationToken ct = default)
    {
        var result = LoadCache();
        var titles = englishTitles
            .Where(t => !string.IsNullOrWhiteSpace(t) && !result.ContainsKey(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0) return result;

        var added = 0;
        for (var i = 0; i < titles.Count; i += BatchSize)
        {
            var batch = titles.Skip(i).Take(BatchSize).ToList();
            try
            {
                var before = result.Count;
                await ResolveBatchAsync(batch, result, ct);
                added += result.Count - before;
            }
            catch
            {
                // вики недоступна — остаёмся с английскими названиями
            }
        }

        if (added > 0) SaveCache(result);
        return result;
    }

    private static string CacheFile =>
        Path.Combine(DataStore.RootDir, "wiki-names.json");

    private static Dictionary<string, string> LoadCache()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(CacheFile));
                if (loaded != null)
                    return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // повреждённый кеш просто игнорируем
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveCache(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(DataStore.RootDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(map,
                new JsonSerializerOptions { WriteIndented = false }));
        }
        catch
        {
            // без кеша просто будем спрашивать вики заново
        }
    }

    private static async Task ResolveBatchAsync(
        List<string> batch, Dictionary<string, string> result, CancellationToken ct)
    {
        var url = $"{Api}?action=query&format=json&prop=langlinks&lllang=ru&lllimit=500" +
                  "&redirects=1&titles=" + Uri.EscapeDataString(string.Join("|", batch));

        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("query", out var query)) return;

        // MediaWiki может переписать заголовок (нормализация, редиректы) —
        // храним обратное соответствие, чтобы вернуть исходный ключ
        var backMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var listName in new[] { "normalized", "redirects" })
        {
            if (!query.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var m in list.EnumerateArray())
            {
                var from = m.GetProperty("from").GetString();
                var to = m.GetProperty("to").GetString();
                if (from != null && to != null) backMap[to] = from;
            }
        }

        if (!query.TryGetProperty("pages", out var pages)) return;
        foreach (var page in pages.EnumerateObject())
        {
            var v = page.Value;
            if (!v.TryGetProperty("langlinks", out var links) || links.ValueKind != JsonValueKind.Array)
                continue;
            var ru = links.EnumerateArray().FirstOrDefault().TryGetProperty("*", out var ruName)
                ? ruName.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(ru)) continue;

            var title = v.GetProperty("title").GetString() ?? "";
            // ключ — тот заголовок, который мы запрашивали
            var key = backMap.TryGetValue(title, out var original) ? original : title;
            result[key] = ru!;
            result[title] = ru!;
        }
    }
}
