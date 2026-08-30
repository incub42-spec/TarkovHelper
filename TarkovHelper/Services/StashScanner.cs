using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Опись схрона по скриншотам окна «Поиск предметов».
///
/// Сам схрон предметы не подписывает — только иконки, и сверять их пришлось бы
/// с картинками из базы. Зато окно поиска подписывает каждую ячейку: короткое
/// имя прижато к правому верхнему углу, стак — к правому нижнему. Значит опись
/// снимается текстом. Страницы поиска не перекрываются, поэтому количества
/// просто складываются.
/// </summary>
public static partial class StashScanner
{
    /// <summary>Сетка окна поиска — двенадцать ячеек в ряду и столько же рядов.</summary>
    public const int Cells = 12;

    /// <summary>Что нашлось на всех страницах.</summary>
    public sealed class Result
    {
        /// <summary>Предмет базы → сколько штук.</summary>
        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
        /// <summary>Подписи, которых в базе нет: игроку виднее, что это было.</summary>
        public List<(string Label, long Count)> Unknown { get; } = new();
        /// <summary>Имя подошло нескольким записям — взята первая.</summary>
        public List<(string Label, Item Chosen, int Others)> Guessed { get; } = new();
        /// <summary>Страницы, на которых не нашлось сетки.</summary>
        public List<string> Skipped { get; } = new();
        public int Pages { get; set; }
        public int Labels { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Ячейка сетки: что за предмет, сколько его и не жетон ли это.</summary>
    private sealed record Cell(string Label, long Count, bool Dogtag);

    private const string DogtagLabel = "Армейский жетон";

    /// <summary>
    /// Читает страницы по очереди. Каждая уходит в облако одним запросом:
    /// встроенный движок на подписи размером в десяток пикселей не годится.
    /// </summary>
    public static async Task<Result> ScanAsync(
        IReadOnlyList<string> files, GameData data, YandexOcr.AppSettingsView settings,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new Result();

        var rect = DetectGrid(files);
        if (rect.Width == 0)
        {
            result.Error = "на снимках не нашлось окна «Поиск предметов»";
            return result;
        }

        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var page = Path.GetFileNameWithoutExtension(file);
            progress?.Report(
                $"Страница {result.Pages + 1} из {files.Count}: {page}");

            BitmapSource bitmap;
            try { bitmap = Load(file); }
            catch { result.Skipped.Add(page); continue; }

            if (rect.X + rect.Width > bitmap.PixelWidth ||
                rect.Y + rect.Height > bitmap.PixelHeight)
            {
                result.Skipped.Add(page);   // снято в другом разрешении
                continue;
            }

            var words = await ReadAsync(bitmap, rect, settings, ct);
            if (words == null)
            {
                result.Error = YandexOcr.LastError ?? "облако не ответило";
                return result;
            }

            foreach (var cell in ReadCells(words, rect.Width, rect.Height))
            {
                // жетон подписан ником убитого: имя ни о чём не говорит, важно число
                var label = cell.Dogtag ? DogtagLabel : cell.Label;
                counts.TryGetValue(label, out var have);
                counts[label] = have + cell.Count;
            }
            result.Pages++;
        }

        // ---- сопоставление с базой ----
        var index = new ShortNameIndex(data);
        result.Labels = counts.Count;

        foreach (var (label, count) in counts.OrderByDescending(p => p.Value))
        {
            var found = index.Find(label, count > 5);
            if (found.Count == 0)
            {
                result.Unknown.Add((label, count));
                continue;
            }

            var item = found[0];
            if (found.Count > 1) result.Guessed.Add((label, item, found.Count - 1));

            result.Counts.TryGetValue(item.Id, out var have);
            result.Counts[item.Id] = (int)Math.Min(int.MaxValue, have + count);
        }

        return result;
    }

    // ================= кадр =================

    /// <summary>Открывает скриншот и приводит к четырём байтам на точку.</summary>
    private static BitmapSource Load(string file)
    {
        var decoded = BitmapFrame.Create(new Uri(file), BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        return decoded.Format == PixelFormats.Bgra32
            ? decoded
            : new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
    }

    /// <summary>
    /// Фон пустой ячейки окна поиска. Цвет у него один и очень узкий: красного
    /// около 34, зелёного ровно на десяток меньше, синего ещё на восемь. Широкий
    /// «просто коричневый» брать нельзя — под такое подходит и деревянная стена
    /// убежища за окном, и половина иконок.
    /// </summary>
    private static bool IsCellBackground(int r, int g, int b) =>
        r is >= 30 and <= 40 &&
        Math.Abs(r - g - 10) <= 2 &&
        Math.Abs(g - b - 8) <= 2;

    /// <summary>
    /// Где на кадре сетка окна поиска — одна на всю пачку.
    ///
    /// Окно между снимками не двигают, поэтому ищем не по одной странице, а по
    /// всем сразу: ячейка, закрытая предметом на первой странице, пуста на
    /// двадцатой, и вместе страницы прорисовывают прямоугольник сетки целиком.
    /// По одной странице этого не выходит — на забитой краевые ячейки закрыты
    /// иконками, и граница «исчезает».
    ///
    /// Дальше — не поиск границ, а поиск самого плотного скопления фона: окном
    /// известного размера (двенадцать квадратных ячеек в ряду, и занимает оно
    /// известную долю экрана) проезжаем по кадру и берём лучшее положение.
    /// </summary>
    public static Int32Rect DetectGrid(IReadOnlyList<string> files)
    {
        int w = 0, h = 0;
        int[]? hits = null;

        foreach (var file in files)
        {
            BitmapSource bitmap;
            try { bitmap = Load(file); }
            catch { continue; }   // не картинка или битый файл — не наша забота

            if (hits == null)
            {
                w = bitmap.PixelWidth;
                h = bitmap.PixelHeight;
                hits = new int[w * h];
            }
            else if (bitmap.PixelWidth != w || bitmap.PixelHeight != h)
            {
                continue;         // снято в другом разрешении, в общую картину не ложится
            }

            var stride = w * 4;
            var pixels = new byte[stride * h];
            bitmap.CopyPixels(pixels, stride, 0);

            for (var y = 0; y < h; y++)
            {
                var line = y * stride;
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    var i = line + x * 4;
                    if (IsCellBackground(pixels[i + 2], pixels[i + 1], pixels[i])) hits[row + x]++;
                }
            }
        }

        if (hits == null) return Int32Rect.Empty;

        // Размер сетки в долях экрана: интерфейс игры тянется вместе с ним, и
        // на 2560 точках сетка занимает 1008 — ровно двенадцать ячеек по 84.
        var size = (int)Math.Round(w * 1008.0 / 2560);
        size -= size % Cells;
        if (size < Cells * 6 || size > Math.Min(w, h)) return Int32Rect.Empty;

        // сумма по прямоугольнику за четыре обращения
        var sums = new long[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            var row = (y + 1) * (w + 1);
            var prev = y * (w + 1);
            for (var x = 0; x < w; x++)
                sums[row + x + 1] = sums[row + x] + sums[prev + x + 1] - sums[prev + x]
                                    + hits[y * w + x];
        }

        long Count(int x, int y) =>
            sums[(y + size) * (w + 1) + x + size] - sums[y * (w + 1) + x + size]
            - sums[(y + size) * (w + 1) + x] + sums[y * (w + 1) + x];

        int bestX = 0, bestY = 0;
        long best = 0;
        for (var y = 0; y + size <= h; y++)
        for (var x = 0; x + size <= w; x++)
        {
            var count = Count(x, y);
            if (count <= best) continue;
            best = count;
            bestX = x;
            bestY = y;
        }

        // окно поиска не открыто — на кадрах просто нет столько такого фона
        return best < (long)size * size / 20
            ? Int32Rect.Empty
            : new Int32Rect(bestX, bestY, size, size);
    }

    /// <summary>Отправляет вырезанную сетку в облако. null — сервис не ответил.</summary>
    private static async Task<List<Word>?> ReadAsync(
        BitmapSource bitmap, Int32Rect rect, YandexOcr.AppSettingsView settings,
        CancellationToken ct)
    {
        // подписи мелкие — увеличиваем вдвое, распознаётся заметно чище
        var crop = new TransformedBitmap(new CroppedBitmap(bitmap, rect), new ScaleTransform(2, 2));

        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        encoder.Save(ms);

        var words = await YandexOcr.RecognizeWordsAsync(ms.ToArray(), settings, ct);
        // координаты кадра были удвоены перед отправкой
        return words?.Select(v => new Word(v.Text, v.Left / 2, v.Top / 2, v.Right / 2, v.Bottom / 2))
            .ToList();
    }

    /// <summary>Слово с рамкой в координатах сетки.</summary>
    private sealed record Word(string Text, double Left, double Top, double Right, double Bottom);

    // ================= разбор =================

    [GeneratedRegex(@"^[\d]+$")]
    private static partial Regex StackRegex();

    [GeneratedRegex(@"^[\d]+\s*/\s*[\d]+$")]
    private static partial Regex ResourceRegex();

    [GeneratedRegex(@"^\S{1,2}\s+([\d]+\s*/\s*[\d]+)$")]
    private static partial Regex IconResourceRegex();

    /// <summary>
    /// Раскладывает прочитанное по ячейкам. В ячейке две строки: подпись
    /// предмета по верхнему краю и стак или ресурс по нижнему. Высота внутри
    /// ячейки разделяет их надёжнее любых отступов — рамки слов OCR отдаёт с
    /// ошибкой в полтора десятка пикселей.
    ///
    /// Прижатое к левому краю предметом не является: это калибр заряженного
    /// оружия, уровень на жетоне и надписи, напечатанные на самой картинке.
    /// </summary>
    private static List<Cell> ReadCells(List<Word> words, int width, int height)
    {
        var cellW = width / (double)Cells;
        var cellH = height / (double)Cells;

        var labels = new Dictionary<(int Col, int Row), List<Word>>();
        var stacks = new Dictionary<(int Col, int Row), string>();
        var marks = new HashSet<(int Col, int Row)>();

        foreach (var word in words)
        {
            var centerY = (word.Top + word.Bottom) / 2;
            var row = Math.Clamp((int)(centerY / cellH), 0, Cells - 1);
            var top = centerY - row * cellH < cellH * 0.42;

            var rightCol = Math.Clamp((int)Math.Round(word.Right / cellW) - 1, 0, Cells - 1);
            var leftCol = Math.Clamp((int)Math.Round(word.Left / cellW), 0, Cells - 1);
            var rightGap = Math.Abs(word.Right - (rightCol + 1) * cellW);
            var leftGap = Math.Abs(word.Left - leftCol * cellW);

            if (top)
            {
                // Подпись из двух слов («Luger CCI») занимает ячейку целиком:
                // первое прижато к левому краю, второе к правому. Слово
                // достаётся той ячейке, к чьему краю оно ближе.
                var col = rightGap <= leftGap ? rightCol : leftCol;
                if (!labels.TryGetValue((col, row), out var list))
                    labels[(col, row)] = list = new List<Word>();
                list.Add(word);
            }
            else if (rightGap <= leftGap)
            {
                stacks.TryAdd((rightCol, row), word.Text);
            }
            else if (StackRegex().IsMatch(word.Text))
            {
                // жетон подписан ником игрока, а уровень убитого — числом слева
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
                    list.OrderBy(v => v.Left).Select(v => v.Text))));

                // «Б 30/30» — перед числом патронов игра рисует значок магазина,
                // и OCR читает его как букву. Это не подпись предмета.
                var icon = IconResourceRegex().Match(text);
                if (icon.Success) text = icon.Groups[1].Value;

                if (!text.Any(char.IsLetter)) continue;
                if (StackRegex().IsMatch(text) || ResourceRegex().IsMatch(text)) continue;

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

    /// <summary>
    /// OCR легко подменяет кириллицу похожими буквами — латинскими и
    /// греческими. Для сравнения сводим все начертания к кириллице.
    /// </summary>
    private const string Lookalikes = "ABCEHKMOPTXaceopxynАВЕЗНІКМНОРТУХавгдеікмнортух";
    private const string Cyrillic = "АВСЕНКМОРТХасеорхупАВЕЗНІКМНОРТУХавгдеікмнортух";

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Replace('ё', 'е').Replace('Ё', 'Е').Trim())
        {
            var i = Lookalikes.IndexOf(ch);
            sb.Append(i >= 0 ? Cyrillic[i] : ch);
        }
        return sb.ToString();
    }

    // ================= сопоставление =================

    /// <summary>Поиск предмета по короткому имени — именно его рисует игра в ячейке.</summary>
    private sealed class ShortNameIndex
    {
        private readonly Dictionary<string, List<Item>> _byShort = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Item>> _byName = new(StringComparer.Ordinal);
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
            var norm = Normalize(s).ToLowerInvariant();
            var sb = new StringBuilder(norm.Length);
            foreach (var ch in norm)
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        /// <summary>
        /// Ячейка узкая, и длинное короткое имя игра обрезает: «Адренали»,
        /// «Корунд-В». Поэтому после точного совпадения пробуем совпадение по
        /// началу, потом с одной ошибкой, потом кусок полного имени.
        /// </summary>
        public List<Item> Find(string label, bool stacked = false)
        {
            _stacked = stacked;
            var key = Key(label);
            if (key.Length < 2) return new List<Item>();

            if (_byShort.TryGetValue(key, out var exact)) return Rank(exact);
            if (_byName.TryGetValue(key, out var byName)) return Rank(byName);
            if (key.Length < 4) return new List<Item>();

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

            var inName = _byName
                .Where(p => p.Key.Contains(key, StringComparison.Ordinal))
                .OrderBy(p => p.Key.Length)
                .SelectMany(p => p.Value)
                .Distinct()
                .ToList();
            return inName.Count > 0 ? Rank(inName) : new List<Item>();
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
        /// ячейке лежат россыпью — «Пачка патронов» это отдельный предмет с тем
        /// же коротким именем, и берут её реже. Дальше выигрывает более
        /// короткое полное имя: у «M4A1» это сама винтовка, а не ресивер.
        /// </summary>
        private List<Item> Rank(List<Item> items) => items
            .OrderBy(i => i.Name.StartsWith("Пачка патронов", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(i => _stacked && !IsAmmo(i) ? 1 : 0)
            .ThenBy(i => i.Name.Length)
            .ToList();

        /// <summary>
        /// Патрон в базе назван калибром: «5.45x39мм БП гс», «12/70 Пиранья».
        /// Имя начинается с цифры, и по этому его видно. Нужно, чтобы стак в
        /// двести штук не уехал в «Блок питания» — короткое имя у них одно.
        /// </summary>
        private static bool IsAmmo(Item item) =>
            item.Name.Length > 0 && (char.IsDigit(item.Name[0]) || item.Name[0] == '.');
    }
}
