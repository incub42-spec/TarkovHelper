using System.IO;
using TarkovHelper.Models;
using static TarkovHelper.Interop.NativeMethods;

namespace TarkovHelper.Services;

/// <summary>
/// Снимок списка квестов у торговца. Игра нигде не хранит на диске, какие
/// квесты сданы: в логах есть только уведомления о сдаче, и те живут недолго.
/// Зато список виден на экране — по галочке «Завершенные» игра показывает
/// сданные, и их можно прочитать целиком. Разбор прочитанного — в
/// <see cref="QuestMatcher"/>.
/// </summary>
internal static class QuestScanner
{
    /// <summary>
    /// Читает левую колонку со списком заданий. Игра показывает завершённые и
    /// активные вперемешку, а статус пишет в той же строке справа от названия —
    /// без него сканирование пометило бы сданными и те, что сейчас в работе.
    /// </summary>
    public static async Task<QuestMatcher.Result> ScanAsync(GameData data, Progress progress, POINT cursor)
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

        var lines = await ScreenOcr.RecognizeLayoutAsync(x, y, w, h, scaleHint: 2, bothLanguages: true, preferCloud: true);
        var result = QuestMatcher.Match(
            lines.Select(l => new QuestMatcher.Line(l.Text, l.X, l.Y)).ToList(),
            data, progress, new QuestMatcher.Region(x, y, w, h));

        AppendDebug($"===== скан квестов {DateTime.Now:HH:mm:ss} область=({x},{y} {w}x{h}) " +
                    $"строк={lines.Count} завершено={result.Completed.Count} " +
                    $"активных={result.Active.Count} без статуса={result.Unknown.Count}\n" +
                    result.Log);

        return result;
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
