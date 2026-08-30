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
        Region Area, int LinesRead)
    {
        public int Total => Completed.Count + Active.Count + Failed.Count + Unknown.Count;
    }

    [GeneratedRegex("(?i)заверш|выполн|complet")]
    private static partial Regex DoneRegex();

    [GeneratedRegex("(?i)активн|active")]
    private static partial Regex ActiveRegex();

    [GeneratedRegex("(?i)провал|fail")]
    private static partial Regex FailedRegex();

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
                var score = ItemMatcher.Similarity(text, progress.NameOf(q));
                if (score > bestScore) { bestScore = score; best = q; }
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

        return new Result(completed, active, failed, unknown, new Region(x, y, w, h), lines.Count);
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
