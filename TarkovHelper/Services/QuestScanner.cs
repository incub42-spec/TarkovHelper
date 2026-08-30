using System.IO;
using System.Text.RegularExpressions;
using TarkovHelper.Models;
using static TarkovHelper.Interop.NativeMethods;

namespace TarkovHelper.Services;

/// <summary>
/// Распознавание списка квестов у торговца. Игра нигде не хранит на диске,
/// какие квесты сданы: в логах есть только уведомления о сдаче, и те живут
/// недолго. Зато список виден на экране — по галочке «Завершенные» игра
/// показывает сданные, и их можно прочитать целиком.
/// </summary>
internal static partial class QuestScanner
{
    public sealed record Region(int X, int Y, int W, int H);

    /// <summary>
    /// Найденное в списке. Completed — со статусом «завершено», Active — со
    /// статусом «активно!», Unknown — название узнали, а статуса в строке нет.
    /// </summary>
    public sealed record Result(
        List<Quest> Completed, List<Quest> Active, List<Quest> Failed, List<Quest> Unknown,
        Region Area, int LinesRead, int StatusMarks)
    {
        public int Total => Completed.Count + Active.Count + Failed.Count + Unknown.Count;

        /// <summary>Все узнанные квесты кадра.</summary>
        public IEnumerable<Quest> Seen => Completed.Concat(Active).Concat(Failed).Concat(Unknown);

        /// <summary>Чей это список: торговец, которому принадлежит большинство строк.</summary>
        public string? Trader => Seen
            .GroupBy(q => q.TraderName)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        /// <summary>
        /// В кадре список без завершённых — значит игра показывает ровно те
        /// квесты, которые сейчас выданы или доступны. Тогда отсутствие
        /// квеста в кадре само по себе информация.
        /// </summary>
        public bool IsAvailableList => Completed.Count == 0 && Total >= 3;

        /// <summary>
        /// Строки со статусом, которые не удалось привязать к квесту базы.
        /// Это либо не распознанное название, либо задание, которого в базе
        /// нет вовсе, — событийные и «Выйти с локации» туда не попадают.
        /// </summary>
        public int Unmatched => Math.Max(0, StatusMarks - Total);
    }

    [GeneratedRegex("(?i)заверш|выполн|complet")]
    private static partial Regex DoneRegex();

    [GeneratedRegex("(?i)активн|active")]
    private static partial Regex ActiveRegex();

    [GeneratedRegex("(?i)провал|fail")]
    private static partial Regex FailedRegex();

    /// <summary>Хвост «. Часть 2» в конце названия.</summary>
    [GeneratedRegex(@"(?i)[\s.,-]*(часть|part)\s*\d+\s*$")]
    private static partial Regex PartSuffixRegex();

    /// <summary>Название без номера части: игра показывает его короче базы.</summary>
    private static string WithoutPart(string name) => PartSuffixRegex().Replace(name, "").Trim();

    /// <summary>
    /// Читает левую колонку со списком заданий. Игра показывает завершённые и
    /// активные вперемешку, а статус пишет в той же строке справа от названия —
    /// без него сканирование пометило бы сданными и те, что сейчас в работе.
    /// </summary>
    public static async Task<Result> ScanAsync(GameData data, Progress progress, POINT cursor)
    {
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var r = info.rcMonitor;
        var width = r.Right - r.Left;
        var height = r.Bottom - r.Top;

        // список заданий занимает левую треть экрана под лентой торговцев
        var x = r.Left;
        var y = r.Top + (int)(height * 0.22);
        var w = (int)(width * 0.36);
        var h = (int)(height * 0.76);

        var lines = await ScreenOcr.RecognizeLayoutAsync(x, y, w, h, scaleHint: 2, bothLanguages: true);

        var doneMarks = new List<(double X, double Y)>();
        var activeMarks = new List<(double X, double Y)>();
        var failedMarks = new List<(double X, double Y)>();
        foreach (var l in lines)
        {
            if (FailedRegex().IsMatch(l.Text)) failedMarks.Add((l.X, l.Y));
            else if (DoneRegex().IsMatch(l.Text)) doneMarks.Add((l.X, l.Y));
            else if (ActiveRegex().IsMatch(l.Text)) activeMarks.Add((l.X, l.Y));
        }

        var completed = new List<Quest>();
        var active = new List<Quest>();
        var failed = new List<Quest>();
        var unknown = new List<Quest>();
        var seen = new HashSet<string>();
        var debug = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (text.Length < 5) continue;
            if (DoneRegex().IsMatch(text) || ActiveRegex().IsMatch(text) ||
                FailedRegex().IsMatch(text)) continue;

            Quest? best = null;
            var bestScore = 0.0;
            foreach (var q in data.Quests)
            {
                if (!progress.Fits(q.Faction)) continue;
                var score = Score(text, q, progress);
                if (score > bestScore) { bestScore = score; best = q; }
            }

            // Игра порой показывает название без номера части: «Операция
            // "Водолей"» вместо «Операция "Водолей". Часть 1». Посимвольно это
            // всего 0.67, поэтому сравниваем ещё и без хвоста с номером, а из
            // частей берём первую несданную — их проходят по порядку.
            if (bestScore < 0.78)
            {
                var stripped = WithoutPart(text);
                Quest? byPart = null;
                var partScore = 0.0;
                foreach (var q in data.Quests)
                {
                    if (!progress.Fits(q.Faction)) continue;
                    var score = Math.Max(
                        ItemMatcher.Similarity(stripped, WithoutPart(progress.NameOf(q))),
                        q.NameAlt == null ? 0 : ItemMatcher.Similarity(stripped, WithoutPart(q.NameAlt)));
                    if (score <= partScore) continue;
                    if (progress.CompletedQuests.Contains(q.Id) && byPart != null) continue;
                    partScore = score;
                    byPart = q;
                }

                if (byPart != null && partScore >= 0.85)
                {
                    var family = data.Quests
                        .Where(q => progress.Fits(q.Faction) &&
                                    (ItemMatcher.Similarity(stripped, WithoutPart(progress.NameOf(q))) >= 0.85 ||
                                     (q.NameAlt != null &&
                                      ItemMatcher.Similarity(stripped, WithoutPart(q.NameAlt)) >= 0.85)))
                        .OrderBy(q => progress.NameOf(q), StringComparer.CurrentCulture)
                        .ToList();
                    best = family.FirstOrDefault(q => !progress.CompletedQuests.Contains(q.Id)) ?? family[0];
                    bestScore = partScore;
                }
            }

            // статус ищем в той же строке правее названия
            const double rowTolerance = 22;
            var isDone = doneMarks.Any(m => Math.Abs(m.Y - line.Y) <= rowTolerance && m.X > line.X);
            var isActive = activeMarks.Any(m => Math.Abs(m.Y - line.Y) <= rowTolerance && m.X > line.X);
            var isFailed = failedMarks.Any(m => Math.Abs(m.Y - line.Y) <= rowTolerance && m.X > line.X);
            var status = isFailed ? "провален"
                : isActive ? "активен"
                : isDone ? "завершён" : "без статуса";

            debug.AppendLine($"  x={line.X,5:F0} y={line.Y,5:F0} | {text}" +
                             $"  => {(best == null ? "нет" : best.Name)} ({bestScore:F2}, {status})");

            if (best == null || bestScore < 0.78 || !seen.Add(best.Id)) continue;

            if (isFailed) failed.Add(best);
            else if (isActive) active.Add(best);
            else if (isDone) completed.Add(best);
            else unknown.Add(best);
        }

        AppendDebug($"===== скан квестов {DateTime.Now:HH:mm:ss} область=({x},{y} {w}x{h}) " +
                    $"строк={lines.Count} завершено={completed.Count} активных={active.Count} " +
                    $"без статуса={unknown.Count}\n" + debug);

        return new Result(completed, active, failed, unknown, new Region(x, y, w, h), lines.Count,
            doneMarks.Count + activeMarks.Count + failedMarks.Count);
    }

    /// <summary>
    /// Похожесть строки на название квеста. Сравниваем и со свежим именем, и с
    /// прежним из локали: у игрока может стоять клиент, где квест ещё не
    /// переименован, а лишний вариант сравнения ничего не портит.
    /// </summary>
    private static double Score(string text, Quest q, Progress progress)
    {
        var score = ItemMatcher.Similarity(text, progress.NameOf(q));
        if (q.NameAlt != null)
            score = Math.Max(score, ItemMatcher.Similarity(text, q.NameAlt));
        return score;
    }

    private static void AppendDebug(string text)
    {
        try
        {
            var file = Path.Combine(DataStore.RootDir, "quest-ocr-debug.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Delete(file);
            File.AppendAllText(file, text);
        }
        catch
        {
            // отладочный лог не должен мешать сканированию
        }
    }
}
