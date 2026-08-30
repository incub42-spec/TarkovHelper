using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TarkovHelper.Services;

/// <summary>
/// Распознавание облачным Yandex Vision OCR. Встроенный движок Windows на
/// коротких русских названиях ошибается дорого: «БАДы» он читает как «6AAbl»,
/// целиком латиницей, и связать такую строку с базой уже нечем. Облачный
/// читает кириллицу заметно точнее.
///
/// Включается вручную и только для сканирования списков: кадр области уходит
/// в облако, и делать это на каждое нажатие F9 в рейде ни к чему — там важнее
/// мгновенный ответ.
/// </summary>
public static class YandexOcr
{
    private const string Endpoint = "https://ocr.api.cloud.yandex.net/ocr/v1/recognizeText";

    // Результат, пришедший позже, чем он был нужен, не нужен вовсе.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// Синхронных запросов разрешён один в секунду: два подряд — и второй
    /// получит отказ. F11 нажимают очередями, поэтому частим сами.
    /// </summary>
    private static DateTime _lastCall = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1100);

    /// <summary>Последняя ошибка запроса — показываем в настройках, а не молчим.</summary>
    public static string? LastError { get; private set; }

    public static bool IsConfigured(AppSettingsView settings) =>
        !string.IsNullOrWhiteSpace(settings.Key) && !string.IsNullOrWhiteSpace(settings.FolderId);

    /// <summary>Что нужно для запроса: ключ и каталог.</summary>
    public sealed record AppSettingsView(string? Key, string? FolderId);

    /// <summary>
    /// Распознаёт PNG и возвращает строки с координатами в пикселях картинки.
    /// Возвращает null, если сервис недоступен — тогда работает встроенный OCR.
    /// </summary>
    public static async Task<List<ScreenOcr.Line>?> RecognizeAsync(
        byte[] png, AppSettingsView settings, CancellationToken ct = default)
    {
        if (!IsConfigured(settings)) return null;

        var since = DateTime.UtcNow - _lastCall;
        if (since < MinInterval)
        {
            LastError = "не чаще одного запроса в секунду — кадр прочитан встроенным движком";
            return null;
        }
        _lastCall = DateTime.UtcNow;

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                mimeType = "image/png",
                languageCodes = new[] { "*" }, // язык определяется сам: в списках и кириллица, и латиница
                // «page» читает кадр как одну колонку и на экране с боковой
                // панелью мешает блоки в поток; у списка квестов справа всегда
                // карточка задания, поэтому нужна сортировка по колонкам
                model = "page-column-sort",
                content = Convert.ToBase64String(png),
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {settings.Key}");
            request.Headers.TryAddWithoutValidation("x-folder-id", settings.FolderId);
            // не разрешаем облаку хранить кадры для обучения
            request.Headers.TryAddWithoutValidation("x-data-logging-enabled", "false");

            using var response = await Http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)response.StatusCode}: {Shorten(text)}";
                return null;
            }

            var lines = Parse(text);
            LastError = lines.Count == 0 ? "в ответе нет текста" : null;
            return lines;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Ответ приходит блоками и строками; у каждой строки — рамка из четырёх
    /// вершин. Нам нужен её левый верхний угол.
    /// </summary>
    private static List<ScreenOcr.Line> Parse(string json)
    {
        var result = new List<ScreenOcr.Line>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("result", out var root)) return result;
        if (!root.TryGetProperty("textAnnotation", out var annotation)) return result;
        if (!annotation.TryGetProperty("blocks", out var blocks) ||
            blocks.ValueKind != JsonValueKind.Array) return result;

        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("lines", out var lines) ||
                lines.ValueKind != JsonValueKind.Array) continue;

            foreach (var line in lines.EnumerateArray())
            {
                var text = line.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (text.Length == 0) continue;

                double x = 0, y = 0;
                if (line.TryGetProperty("boundingBox", out var box) &&
                    box.TryGetProperty("vertices", out var vertices) &&
                    vertices.ValueKind == JsonValueKind.Array)
                {
                    var first = true;
                    foreach (var v in vertices.EnumerateArray())
                    {
                        var vx = Num(v, "x");
                        var vy = Num(v, "y");
                        if (first) { x = vx; y = vy; first = false; }
                        else { x = Math.Min(x, vx); y = Math.Min(y, vy); }
                    }
                }

                result.Add(new ScreenOcr.Line(text, x, y));
            }
        }
        return result;
    }

    /// <summary>Координаты приходят строками — «1234», а не числом.</summary>
    private static double Num(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), out var d) ? d : 0,
            _ => 0,
        };
    }

    private static string Shorten(string s) =>
        s.Length <= 200 ? s : s[..200] + "…";
}
