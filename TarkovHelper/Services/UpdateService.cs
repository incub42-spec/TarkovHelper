using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace TarkovHelper.Services;

/// <summary>
/// Обновление приложения из релизов GitHub: проверка версии, скачивание нового
/// exe и замена текущего. Работает только для самодостаточной сборки одним файлом
/// (именно она выкладывается в релизы).
/// </summary>
public static class UpdateService
{
    private const string ReleasesApi =
        "https://api.github.com/repos/incub42-spec/TarkovHelper/releases/latest";

    /// <summary>Найденное обновление.</summary>
    public sealed record Available(Version Version, string DownloadUrl, string Notes);

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub API отклоняет запросы без User-Agent
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper");
        return c;
    }

    /// <summary>
    /// Спрашивает GitHub о последнем релизе. Возвращает null, если версия
    /// актуальна; бросает исключение только при явных ошибках сети.
    /// </summary>
    public static async Task<Available?> CheckAsync(CancellationToken ct = default)
    {
        using var doc = await Http.GetFromJsonAsync<JsonDocument>(ReleasesApi, ct)
            ?? throw new InvalidOperationException("GitHub вернул пустой ответ.");

        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            throw new InvalidOperationException($"Не удалось разобрать версию релиза «{tag}».");

        if (latest <= CurrentVersion) return null;

        var url = root.GetProperty("assets").EnumerateArray()
            .Where(a => (a.GetProperty("name").GetString() ?? "").EndsWith(".exe",
                StringComparison.OrdinalIgnoreCase))
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault();
        if (url == null)
            throw new InvalidOperationException("В релизе нет exe-файла.");

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new Available(latest, url, notes);
    }

    /// <summary>
    /// Скачивает новую версию и подменяет текущий exe. Windows не даёт перезаписать
    /// запущенный файл, но позволяет его переименовать — старый уходит в *.old
    /// и удаляется при следующем запуске.
    /// </summary>
    public static async Task DownloadAndApplyAsync(
        Available update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Обновление доступно только для собранного exe (не для запуска через dotnet run).");

        var tempPath = exePath + ".new";
        await DownloadAsync(update.DownloadUrl, tempPath, progress, ct);

        var oldPath = exePath + ".old";
        if (File.Exists(oldPath)) File.Delete(oldPath);
        File.Move(exePath, oldPath);          // работает даже для запущенного файла
        File.Move(tempPath, exePath);

        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
    }

    private static async Task DownloadAsync(
        string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
    }

    /// <summary>Удаляет остатки прошлого обновления. Вызывать при старте.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath == null) return;
            foreach (var junk in new[] { exePath + ".old", exePath + ".new" })
                if (File.Exists(junk)) File.Delete(junk);
        }
        catch
        {
            // мусор от обновления не должен мешать запуску
        }
    }
}
