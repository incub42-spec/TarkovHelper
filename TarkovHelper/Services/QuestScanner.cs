using TarkovHelper.Models;
using static TarkovHelper.Interop.NativeMethods;

namespace TarkovHelper.Services;

/// <summary>
/// Распознавание списка квестов у торговца. Игра нигде не хранит на диске,
/// какие квесты сданы: в логах есть только уведомления о сдаче, и те живут
/// недолго. Зато список виден на экране — по галочке «Завершенные» игра
/// показывает сданные, и их можно прочитать целиком.
/// </summary>
internal static class QuestScanner
{
    public sealed record Region(int X, int Y, int W, int H);
    public sealed record Result(List<Quest> Matched, Region Area, int LinesRead);

    /// <summary>
    /// Читает левую колонку со списком заданий и сопоставляет строки с
    /// названиями квестов. Порог высокий: ошибиться здесь дороже, чем
    /// не узнать квест — отметка сдачи меняет весь список нужного лута.
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
        var y = r.Top + (int)(height * 0.25);
        var w = (int)(width * 0.33);
        var h = (int)(height * 0.72);

        var lines = await ScreenOcr.RecognizeLayoutAsync(x, y, w, h, scaleHint: 2, bothLanguages: true);

        var matched = new List<Quest>();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (text.Length < 5) continue;

            // «активно!» и заголовки уровней лояльности названиями не являются
            Quest? best = null;
            var bestScore = 0.0;
            foreach (var q in data.Quests)
            {
                if (!progress.Fits(q.Faction)) continue;
                var score = ItemMatcher.Similarity(text, progress.NameOf(q));
                if (score > bestScore) { bestScore = score; best = q; }
            }

            if (best != null && bestScore >= 0.85 && !matched.Contains(best))
                matched.Add(best);
        }

        return new Result(matched, new Region(x, y, w, h), lines.Count);
    }
}
