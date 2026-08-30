using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovHelper.Models;

// Разбор схрона по скриншотам окна «Поиск предметов». Окно показывает вещи
// сеткой с подписями: в правом верхнем углу ячейки — короткое имя, в правом
// нижнем — стак или ресурс. Значит опись можно снять текстом, а не сверкой
// иконок, — и именно поэтому скриншоты снимают из поиска, а не из схрона.
//
// Страницы поиска не перекрываются, поэтому количества просто складываются.

var scratch = Environment.GetEnvironmentVariable("STASH_SCRATCH")
              ?? Path.Combine(Path.GetTempPath(), "stash-scan");
var cacheDir = Path.Combine(scratch, "ocr-cache");
Directory.CreateDirectory(cacheDir);

var appData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TarkovHelper");

var folder = args.FirstOrDefault(a => !a.StartsWith("--"))
             ?? Path.Combine(scratch, "stash");
var apply = args.Contains("--apply");
var probeOnly = args.Contains("--probe");
var dumpPage = args.FirstOrDefault(a => a.StartsWith("--dump="))?[7..];

// Рамку сетки можно задать руками: «--rect x,y,w,h». Окно поиска не двигали
// между снимками, а автопоиск по фону спотыкается на плотно забитых страницах,
// где коричневого просвета не остаётся вовсе.
var rectArg = args.FirstOrDefault(a => a.StartsWith("--rect="));
var fixedRect = Rectangle.Empty;
if (rectArg != null)
{
    var p = rectArg[7..].Split(',').Select(int.Parse).ToArray();
    fixedRect = new Rectangle(p[0], p[1], p[2], p[3]);
}

Console.OutputEncoding = Encoding.UTF8;

var files = Directory.GetFiles(folder, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
if (files.Count == 0) { Console.WriteLine($"нет файлов в {folder}"); return; }

var report = new StringBuilder();
void Say(string line) { Console.WriteLine(line); report.AppendLine(line); }

Say($"страниц: {files.Count}");

// ---------- настройки и база ----------

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
using var settingsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(appData, "progress.json")));
var root = settingsDoc.RootElement;
var key = root.GetProperty("YandexOcrKey").GetString()!;
var folderId = root.GetProperty("YandexFolderId").GetString()!;

var data = JsonSerializer.Deserialize<GameData>(
    File.ReadAllText(Path.Combine(appData, "data-pve.json")), jsonOpts)!;
Say($"база: {data.Items.Count} предметов");

// ---------- разбор страниц ----------

const string DogtagLabel = "\u0410\u0440\u043c\u0435\u0439\u0441\u043a\u0438\u0439 \u0436\u0435\u0442\u043e\u043d";

var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
var seenAs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

foreach (var file in files)
{
    var page = Path.GetFileNameWithoutExtension(file);
    using var bitmap = new Bitmap(file);
    var rect = GridDetector.Detect(bitmap, fixedRect);
    if (rect.IsEmpty) { Say($"{page}: сетка не найдена"); continue; }

    if (probeOnly)
    {
        Say($"{page}: сетка {rect.X},{rect.Y} {rect.Width}x{rect.Height}");
        continue;
    }

    var lines = await Ocr.ReadAsync(bitmap, rect, file, cacheDir, key, folderId);
    if (dumpPage != null && page == dumpPage)
        foreach (var r in CellReader.Dump(lines, rect.Width, rect.Height))
            Say("  " + r);

    var cells = CellReader.Read(lines, rect.Width, rect.Height);

    foreach (var cell in cells)
    {
        // жетон подписан ником убитого: имя ни о чём не говорит, важно само число
        var label = cell.Dogtag ? DogtagLabel : cell.Label;
        counts.TryGetValue(label, out var have);
        counts[label] = have + cell.Count;
        seenAs.TryGetValue(label, out var n);
        seenAs[label] = n + 1;
    }

    Say($"{page}: сетка {rect.Width}x{rect.Height}, строк OCR {lines.Count}, ячеек {cells.Count}");
}

if (probeOnly) { File.WriteAllText(Path.Combine(scratch, "report.txt"), report.ToString()); return; }

// ---------- сопоставление с базой ----------

var matcher = new ShortNameIndex(data);
var matched = new Dictionary<string, long>();      // itemId -> сколько
var guessed = new List<string>();
var unknown = new List<string>();

foreach (var (label, count) in counts.OrderByDescending(p => p.Value))
{
    var found = matcher.Find(label, count > 5);
    if (found.Count == 0) { unknown.Add($"{label} ×{count}"); continue; }

    var item = found[0];
    if (found.Count > 1)
        guessed.Add($"{label} ×{count} → {item.Name}  (ещё {found.Count - 1}: " +
                    string.Join(", ", found.Skip(1).Take(3).Select(i => i.Name)) + ")");

    matched.TryGetValue(item.Id, out var have);
    matched[item.Id] = have + count;
}

Say("");
Say($"распознано подписей: {counts.Count}, из них сопоставлено {counts.Count - unknown.Count}");
Say($"предметов в схроне (по записям базы): {matched.Count}, штук всего: {matched.Values.Sum()}");

Say("");
Say("=== не найдено в базе ===");
foreach (var u in unknown) Say("  " + u);

Say("");
Say("=== имя подошло нескольким записям, взята первая ===");
foreach (var g in guessed) Say("  " + g);

Say("");
Say("=== что записываем (топ 60) ===");
var byName = matched
    .Select(p => (Item: data.Items.First(i => i.Id == p.Key), p.Value))
    .OrderByDescending(p => p.Value)
    .ToList();
foreach (var (item, n) in byName.Take(60)) Say($"  {n,8}  {item.Name}");

// ---------- запись ----------

if (apply)
{
    var progressPath = Path.Combine(appData, "progress.json");
    File.Copy(progressPath, progressPath + ".bak-stash", overwrite: true);

    var settings = JsonSerializer.Deserialize<AppSettings>(
        File.ReadAllText(progressPath), jsonOpts)!;
    var profile = settings.Profiles.First(p => p.Name == settings.ActiveProfile);

    profile.Stash.Clear();
    foreach (var (id, n) in matched)
        profile.Stash[id] = (int)Math.Min(int.MaxValue, n);

    File.WriteAllText(progressPath,
        JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    Say("");
    Say($"записано в профиль «{profile.Name}»: {profile.Stash.Count} строк");
}
else
{
    Say("");
    Say("это разведка; чтобы записать в профиль, добавь --apply");
}

File.WriteAllText(Path.Combine(scratch, "report.txt"), report.ToString());

// ================= вспомогательные =================

/// <summary>Слово, прочитанное OCR, вместе с рамкой в координатах кадра.</summary>
internal sealed record OcrLine(string Text, double Left, double Top, double Right, double Bottom);

/// <summary>Ячейка сетки: что за предмет, сколько его и не жетон ли это.</summary>
internal sealed record Cell(string Label, long Count, bool Dogtag);

/// <summary>
/// Где на кадре сетка окна поиска. Фон её ячеек — коричневый, и такого больше
/// нигде в интерфейсе нет: схрон за окном серо-синий. По нему и находим рамку,
/// не привязываясь к положению окна и разрешению экрана.
/// </summary>
internal static class GridDetector
{
    private static bool IsCellBackground(int r, int g, int b) =>
        r is >= 26 and <= 46 && g is >= 16 and <= 34 && b is >= 8 and <= 26 &&
        r > g && g > b && r - b >= 10;

    public static Rectangle Detect(Bitmap bitmap, Rectangle forced)
    {
        if (!forced.IsEmpty) return forced;

        var w = bitmap.Width;
        var h = bitmap.Height;
        var cols = new int[w];
        var rows = new int[h];

        var mask = new bool[w, h];
        var bits = bitmap.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (var y = 0; y < h; y++)
                {
                    var line = (byte*)bits.Scan0 + y * bits.Stride;
                    for (var x = 0; x < w; x++)
                    {
                        var b = line[x * 4];
                        var g = line[x * 4 + 1];
                        var r = line[x * 4 + 2];
                        if (!IsCellBackground(r, g, b)) continue;
                        mask[x, y] = true;
                        cols[x]++;
                    }
                }
            }
        }
        finally { bitmap.UnlockBits(bits); }

        // Ширина берётся по столбцам: фон окна виден в просветах между иконками
        // на сотнях строк, а случайные коричневые пиксели в картинках — нет.
        var x0 = FirstAbove(cols, h / 12);
        var x1 = LastAbove(cols, h / 12);
        if (x0 < 0 || x1 - x0 < 200) return Rectangle.Empty;

        // По высоте так не выйдет: ряд, забитый предметами целиком, фона не
        // показывает вовсе. Поэтому ищем самый длинный участок строк, где фон
        // хоть где-то виден, разрешая разрывы в пару рядов ячеек.
        for (var y = 0; y < h; y++)
        {
            var n = 0;
            for (var x = x0; x <= x1; x++) if (mask[x, y]) n++;
            rows[y] = n;
        }

        var gapLimit = (x1 - x0) / 4;   // два ряда ячеек: сетка 12×12
        int bestStart = -1, bestEnd = -1, start = -1, lastSeen = -1;
        for (var y = 0; y < h; y++)
        {
            if (rows[y] == 0) continue;
            if (start < 0 || y - lastSeen > gapLimit)
            {
                if (start >= 0 && lastSeen - start > bestEnd - bestStart)
                {
                    bestStart = start; bestEnd = lastSeen;
                }
                start = y;
            }
            lastSeen = y;
        }
        if (start >= 0 && lastSeen - start > bestEnd - bestStart)
        {
            bestStart = start; bestEnd = lastSeen;
        }
        if (bestStart < 0 || bestEnd - bestStart < 200) return Rectangle.Empty;

        return new Rectangle(x0, bestStart, x1 - x0 + 1, bestEnd - bestStart + 1);
    }

    private static int FirstAbove(int[] profile, int threshold)
    {
        for (var i = 0; i < profile.Length; i++) if (profile[i] >= threshold) return i;
        return -1;
    }

    private static int LastAbove(int[] profile, int threshold)
    {
        for (var i = profile.Length - 1; i >= 0; i--) if (profile[i] >= threshold) return i;
        return -1;
    }
}

/// <summary>Облачное распознавание с кешем: повторный прогон не стоит денег.</summary>
internal static class Ocr
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static DateTime _last = DateTime.MinValue;

    public static async Task<List<OcrLine>> ReadAsync(
        Bitmap bitmap, Rectangle rect, string file, string cacheDir, string key, string folderId)
    {
        var cache = Path.Combine(cacheDir, Path.GetFileNameWithoutExtension(file) + ".json");
        string json;
        if (File.Exists(cache))
        {
            json = await File.ReadAllTextAsync(cache);
        }
        else
        {
            // подписи мелкие — увеличиваем вдвое, распознаётся заметно чище
            using var crop = new Bitmap(rect.Width * 2, rect.Height * 2);
            using (var g = Graphics.FromImage(crop))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, new Rectangle(0, 0, crop.Width, crop.Height), rect,
                    GraphicsUnit.Pixel);
            }
            using var ms = new MemoryStream();
            crop.Save(ms, ImageFormat.Png);

            var since = DateTime.UtcNow - _last;
            if (since < TimeSpan.FromMilliseconds(1200))
                await Task.Delay(TimeSpan.FromMilliseconds(1200) - since);
            _last = DateTime.UtcNow;

            var body = JsonSerializer.Serialize(new
            {
                mimeType = "image/png",
                languageCodes = new[] { "*" },
                model = "page",
                content = Convert.ToBase64String(ms.ToArray()),
            });
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://ocr.api.cloud.yandex.net/ocr/v1/recognizeText")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {key}");
            request.Headers.TryAddWithoutValidation("x-folder-id", folderId);
            request.Headers.TryAddWithoutValidation("x-data-logging-enabled", "false");

            using var response = await Http.SendAsync(request);
            json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"OCR {(int)response.StatusCode}: {json[..Math.Min(300, json.Length)]}");
            await File.WriteAllTextAsync(cache, json);
        }

        return Parse(json);
    }

    private static List<OcrLine> Parse(string json)
    {
        var result = new List<OcrLine>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var res)) return result;
        if (!res.TryGetProperty("textAnnotation", out var ann)) return result;
        if (!ann.TryGetProperty("blocks", out var blocks)) return result;

        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("lines", out var lines)) continue;
            foreach (var line in lines.EnumerateArray())
            {
                // Берём слова, а не строки целиком: OCR охотно сшивает подписи
                // соседних ячеек в одну строку («Анальгин Анальгин»), и по
                // рамке строки уже не понять, где кончается один предмет.
                if (!line.TryGetProperty("words", out var words)) continue;
                foreach (var word in words.EnumerateArray())
                {
                    var text = word.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (text.Length == 0) continue;
                    if (!word.TryGetProperty("boundingBox", out var box) ||
                        !box.TryGetProperty("vertices", out var vs)) continue;

                    double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
                    foreach (var v in vs.EnumerateArray())
                    {
                        var x = Num(v, "x");
                        var y = Num(v, "y");
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y);
                        x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    }
                    // координаты кадра были удвоены перед отправкой
                    result.Add(new OcrLine(text.Trim(), x0 / 2, y0 / 2, x1 / 2, y1 / 2));
                }
            }
        }
        return result;
    }

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var d) ? d : 0,
            _ => 0,
        };
    }
}

/// <summary>
/// Разбор прочитанного в ячейки сетки 12x12.
///
/// Игра рисует в ячейке две подписи: короткое имя прижато к правому верхнему
/// углу, стак или ресурс — к правому нижнему. Всё, что прижато к левому краю,
/// предметом не является: это калибр заряженного оружия, уровень на жетоне и
/// надписи, напечатанные на самой картинке. По выравниванию их и отделяем.
/// </summary>
internal static partial class CellReader
{
    public const int Cells = 12;

    [GeneratedRegex(@"^[\d]+$")]
    private static partial Regex StackRegex();

    [GeneratedRegex(@"^[\d]+\s*/\s*[\d]+$")]
    private static partial Regex ResourceRegex();

    [GeneratedRegex(@"^\S{1,2}\s+([\d]+\s*/\s*[\d]+)$")]
    private static partial Regex IconResourceRegex();

    /// <summary>Кусок текста, собранный из слов одной ячейки и одной строки.</summary>
    private sealed record Run(string Text, int Col, int Row, double Top, bool RightAligned);

    public static List<Cell> Read(List<OcrLine> words, int width, int height)
    {
        var cellW = width / (double)Cells;
        var cellH = height / (double)Cells;

        // В ячейке две строки: подпись предмета по верхнему краю и стак или
        // ресурс по нижнему. Высота внутри ячейки разделяет их надёжнее любых
        // отступов — рамки слов OCR отдаёт с ошибкой в полтора десятка пикселей.
        var labels = new Dictionary<(int Col, int Row), List<OcrLine>>();
        var stacks = new Dictionary<(int Col, int Row), string>();
        var marks = new HashSet<(int Col, int Row)>();

        foreach (var word in words)
        {
            var centerY = (word.Top + word.Bottom) / 2;
            var row = Math.Clamp((int)(centerY / cellH), 0, Cells - 1);
            var top = centerY - row * cellH < cellH * 0.42;

            // К какому краю ячейки слово прижато. Подпись и стак — к правому,
            // уровень убитого на жетоне и калибр заряженного оружия — к левому.
            // Сравниваем оба расстояния: по одному правому краю уровень «27»
            // не отличить от стака соседней ячейки.
            var rightCol = Math.Clamp((int)Math.Round(word.Right / cellW) - 1, 0, Cells - 1);
            var leftCol = Math.Clamp((int)Math.Round(word.Left / cellW), 0, Cells - 1);
            var rightGap = Math.Abs(word.Right - (rightCol + 1) * cellW);
            var leftGap = Math.Abs(word.Left - leftCol * cellW);

            if (top)
            {
                // Подпись из двух слов («Luger CCI») занимает ячейку целиком:
                // первое прижато к левому краю, второе к правому. Слово
                // достаётся той ячейке, к чьему краю оно ближе, — а рамки OCR
                // ошибаются на десяток пикселей, и центр слова тут не помощник.
                var col = rightGap <= leftGap ? rightCol : leftCol;
                if (!labels.TryGetValue((col, row), out var list))
                    labels[(col, row)] = list = new List<OcrLine>();
                list.Add(word);
            }
            else if (rightGap <= leftGap)
            {
                stacks.TryAdd((rightCol, row), word.Text);
            }
            else if (StackRegex().IsMatch(word.Text))
            {
                marks.Add((leftCol, row));
            }
        }

        var cells = new List<Cell>();
        foreach (var column in labels.GroupBy(p => p.Key.Col))
        {
            var ordered = column.OrderBy(p => p.Key.Row).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var (key, list) = (ordered[i].Key, ordered[i].Value);
                var text = Collapse(Normalize(string.Join(" ",
                    list.OrderBy(w => w.Left).Select(w => w.Text))));
                if (!text.Any(char.IsLetter)) continue;
                if (IsNumber(text)) continue;

                var dogtag = marks.Contains(key);

                // Стак ищем ниже подписи в том же столбце: у предмета на
                // несколько клеток подпись сверху, а число — у нижнего края.
                long count = 1;
                if (!dogtag)
                {
                    var until = i + 1 < ordered.Count ? ordered[i + 1].Key.Row : Cells;
                    for (var row = key.Row; row < until; row++)
                        if (stacks.TryGetValue((key.Col, row), out var value) &&
                            StackRegex().IsMatch(value))
                        {
                            count = long.Parse(value);
                            break;
                        }
                }

                cells.Add(new Cell(text, count, dogtag));
            }
        }
        return cells;
    }

    /// <summary>
    /// Одна рамка OCR иногда накрывает подписи двух соседних ячеек: «Анальгин
    /// Анальгин». Если все куски одинаковы, это точно они — оставляем один.
    /// </summary>
    private static string Collapse(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && parts.All(x => x == parts[0]) ? parts[0] : text;
    }

    /// <summary>Куски строкой — для глаз, когда разбор идёт не туда.</summary>
    public static IEnumerable<string> Dump(List<OcrLine> words, int width, int height)
    {
        var cellW = width / (double)Cells;
        foreach (var band in Bands(words))
            foreach (var w in band)
                yield return $"{w.Text,-20} c{(int)(w.Left / cellW),-2} " +
                             $"x {w.Left:0}..{w.Right:0} y {w.Top:0} " +
                             $"край {Math.Round(w.Right / cellW) * cellW - w.Right:0}";
    }

    /// <summary>Слова, разложенные по строкам текста и упорядоченные слева направо.</summary>
    private static IEnumerable<List<OcrLine>> Bands(List<OcrLine> words)
    {
        var band = new List<OcrLine>();
        double top = double.MinValue;
        foreach (var word in words.OrderBy(w => w.Top).ThenBy(w => w.Left))
        {
            if (band.Count > 0 && word.Top - top > 12)
            {
                yield return band.OrderBy(w => w.Left).ToList();
                band = new List<OcrLine>();
            }
            if (band.Count == 0) top = word.Top;
            band.Add(word);
        }
        if (band.Count > 0) yield return band.OrderBy(w => w.Left).ToList();
    }

    private static bool IsNumber(string s) =>
        StackRegex().IsMatch(s) || ResourceRegex().IsMatch(s);

    /// <summary>
    /// OCR легко подменяет кириллицу похожими буквами — латинскими и греческими
    /// («BOГ-17»). Для сравнения сводим все начертания к кириллице.
    /// </summary>
    private const string Lookalikes = "ABCEHKMOPTXaceopxynАВЕЗНІКМНОРТУХавгдеікмнортух";
    private const string Cyrillic = "АВСЕНКМОРТХасеорхупАВЕЗНІКМНОРТУХавгдеікмнортух";

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Replace('\u0451', '\u0435').Replace('\u0401', '\u0415').Trim())
        {
            var i = Lookalikes.IndexOf(ch);
            sb.Append(i >= 0 ? Cyrillic[i] : ch);
        }
        return sb.ToString();
    }
}

/// <summary>Поиск предмета по короткому имени — именно его рисует игра в ячейке.</summary>
internal sealed class ShortNameIndex
{
    private readonly Dictionary<string, List<Item>> _byShort = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Item>> _byName = new(StringComparer.Ordinal);

    /// <summary>В ячейке лежала пачка — значит предмет складывается в стак.</summary>
    private bool _stacked;

    public ShortNameIndex(GameData data)
    {
        foreach (var item in data.Items)
        {
            Add(_byShort, item.ShortName, item);
            Add(_byShort, item.ShortNameEn, item);
            Add(_byName, item.Name, item);
        }
    }

    private static void Add(Dictionary<string, List<Item>> map, string? name, Item item)
    {
        var key = Key(name);
        if (key.Length == 0) return;
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Item>();
        if (!list.Contains(item)) list.Add(item);
    }

    /// <summary>
    /// Знаки в ключе не значат ничего: игра пишет «Общ 105», OCR читает
    /// «Общ-105», и это один и тот же ключ от общежития.
    /// </summary>
    private static string Key(string? s)
    {
        if (s == null) return "";
        var norm = CellReader.Normalize(s).ToLowerInvariant();
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>
    /// Ячейка узкая, и длинное короткое имя игра обрезает: «Адренали»,
    /// «Корунд-В». Поэтому после точного совпадения пробуем совпадение по
    /// началу — если обрезок ни с чем другим не путается.
    /// </summary>
    public List<Item> Find(string label, bool stacked = false)
    {
        var key = Key(label);
        _stacked = stacked;
        if (key.Length < 2) return new List<Item>();

        if (_byShort.TryGetValue(key, out var exact)) return Rank(exact);
        if (_byName.TryGetValue(key, out var byName)) return Rank(byName);

        if (key.Length >= 4)
        {
            var prefix = _byShort
                .Where(p => p.Key.StartsWith(key, StringComparison.Ordinal))
                .OrderBy(p => p.Key.Length)
                .SelectMany(p => p.Value)
                .Distinct()
                .ToList();
            if (prefix.Count > 0) return Rank(prefix);

            // одна перепутанная буква — обычное дело: «Сан306 З» читается как
            // «Сан306 3», цифра вместо буквы
            var near = _byShort
                .Where(p => Math.Abs(p.Key.Length - key.Length) <= 1 && Distance(p.Key, key) <= 1)
                .SelectMany(p => p.Value)
                .Distinct()
                .ToList();
            if (near.Count > 0) return Rank(near);

            // подпись бывает и куском полного имени: «Паштет» из «Паштет ...»
            var inName = _byName
                .Where(p => p.Key.Contains(key, StringComparison.Ordinal))
                .OrderBy(p => p.Key.Length)
                .SelectMany(p => p.Value)
                .Distinct()
                .ToList();
            if (inName.Count > 0) return Rank(inName);
        }
        return new List<Item>();
    }

    /// <summary>Расстояние Левенштейна, обрезанное на двойке: больше не нужно.</summary>
    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1),
                    prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                best = Math.Min(best, cur[j]);
            }
            if (best > 1) return 2;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Порядок кандидатов, когда имя подошло нескольким записям. Патроны в
    /// ячейке лежат россыпью — «Пачка патронов» это отдельный предмет с тем же
    /// коротким именем, и берут её реже. Дальше выигрывает более короткое
    /// полное имя: у «M4A1» это сама винтовка, а не ресивер для неё.
    /// </summary>
    private List<Item> Rank(List<Item> items) => items
        .OrderBy(i => i.Name.StartsWith(Pack, StringComparison.Ordinal) ? 1 : 0)
        .ThenBy(i => _stacked && !IsAmmo(i) ? 1 : 0)
        .ThenBy(i => i.Name.Length)
        .ToList();

    private const string Pack = "\u041f\u0430\u0447\u043a\u0430 \u043f\u0430\u0442\u0440\u043e\u043d\u043e\u0432";

    /// <summary>
    /// Патрон в базе назван калибром: «5.45x39мм БП гс», «12/70 Пиранья». Имя
    /// начинается с цифры, и по этому его видно. Нужно, чтобы стак в двести
    /// штук не уехал в «Блок питания» — короткое имя у них одно, «БП».
    /// </summary>
    private static bool IsAmmo(Item item) =>
        item.Name.Length > 0 && (char.IsDigit(item.Name[0]) || item.Name[0] == '.');
}
