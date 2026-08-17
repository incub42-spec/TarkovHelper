using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TarkovHelper.Models;
using static TarkovHelper.Interop.NativeMethods;

namespace TarkovHelper.Services;

/// <summary>
/// Распознавание состояния убежища с экрана (хоткей F10). Понимает два экрана игры:
///  - общий вид убежища: уровни показаны цифровыми бейджами «01/02» рядом с названиями
///    станций (нижняя панель и подписи иконок) — за одно нажатие считываются все
///    видимые станции;
///  - окно конкретной станции: явная надпись «УРОВЕНЬ N» или кнопка «Построить».
/// Все распознанные строки пишутся в hideout-ocr-debug.log для калибровки.
/// </summary>
internal static partial class HideoutScanner
{
    public sealed record StationLevel(HideoutStation Station, int Level);
    public sealed record Result(List<StationLevel> Found, List<HideoutStation> NoLevel);

    [GeneratedRegex(@"(?i)(?:УРОВЕНЬ|УРОВ|УР|LEVEL|LVL)\W{0,4}(\d{1,2})")]
    private static partial Regex LevelRegex();

    [GeneratedRegex(@"(?i)ПОСТРОИТЬ|НЕ ПОСТРОЕНО|CONSTRUCT|NOT BUILT")]
    private static partial Regex NotBuiltRegex();

    public static async Task<Result> ScanAsync(GameData data, POINT cursor)
    {
        // сканируем монитор, на котором курсор
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var r = info.rcMonitor;
        var monitorWidth = r.Right - r.Left;

        // масштаб 3 — мелкие цифровые бейджи читаются надёжнее
        var lines = await ScreenOcr.RecognizeLayoutAsync(
            r.Left, r.Top, monitorWidth, r.Bottom - r.Top, scaleHint: 3);

        AppendDebug(lines);

        // Цифровые бейджи уровней: короткие строки вида «01», «02», «4».
        // Тёмная иконка сливается с первым символом, и OCR читает «01» как «21»/«а1»/«62»,
        // но последняя цифра всегда верна, а уровни станций однозначные — берём её.
        var badges = new List<(int Value, double X, double Y)>();
        foreach (var l in lines)
        {
            var clean = l.Text.Trim();
            if (clean.Length > 3) continue;
            var digits = new string(clean.Where(char.IsDigit).ToArray());
            if (digits.Length is < 1 or > 2) continue;
            badges.Add((digits[^1] - '0', l.X, l.Y));
        }

        // совпадения названий станций: лучшее (самое верхнее) вхождение на станцию
        var nameHits = new Dictionary<string, (HideoutStation St, double X, double Y)>();
        foreach (var line in lines)
        {
            var norm = ItemMatcher.Normalize(line.Text);
            if (norm.Length < 3) continue;
            foreach (var s in data.Stations)
            {
                foreach (var name in Names(s))
                {
                    var nn = ItemMatcher.Normalize(name);
                    if (nn.Length < 3) continue;
                    // короткие названия («Тир») — только точное совпадение, иначе ловим подстроки
                    var hit = norm == nn || (nn.Length >= 5 && norm.Contains(nn));
                    if (!hit) continue;
                    if (!nameHits.TryGetValue(s.Id, out var ex) || line.Y < ex.Y)
                        nameHits[s.Id] = (s, line.X, line.Y);
                }
            }
        }

        var found = new List<StationLevel>();
        var noLevel = new List<HideoutStation>();

        // окно одной станции: явная надпись «УРОВЕНЬ N» или кнопка «Построить»
        if (nameHits.Count == 1)
        {
            var only = nameHits.Values.First();
            foreach (var line in lines)
            {
                var m = LevelRegex().Match(line.Text);
                if (!m.Success) continue;
                var val = int.Parse(m.Groups[1].Value);
                if (val <= MaxLevel(only.St))
                {
                    found.Add(new StationLevel(only.St, val));
                    break;
                }
            }
            if (found.Count == 0 && lines.Any(l => NotBuiltRegex().IsMatch(l.Text)))
                found.Add(new StationLevel(only.St, 0));
        }

        // Общий вид: бейдж станции всегда чуть левее и ниже начала её названия
        // (низ иконки; в нижней панели и у подписей на карте смещение одинаковое,
        // ~37x36 px при ширине экрана 2000). Бейджи вне этой зоны — чужие.
        if (found.Count == 0)
        {
            var dxMin = monitorWidth * 0.004;
            var dxMax = monitorWidth * 0.055;
            var dyMin = monitorWidth * 0.004;
            var dyMax = monitorWidth * 0.045;

            foreach (var (st, x, y) in nameHits.Values)
            {
                var best = (Value: 0, Dist: double.MaxValue);
                foreach (var b in badges)
                {
                    if (b.Value > MaxLevel(st)) continue;
                    var dx = x - b.X; // бейдж левее названия
                    var dy = b.Y - y; // и ниже него
                    if (dx < dxMin || dx > dxMax || dy < dyMin || dy > dyMax) continue;
                    var d = dx * dx + dy * dy;
                    if (d < best.Dist)
                        best = (b.Value, d);
                }
                if (best.Dist < double.MaxValue)
                    found.Add(new StationLevel(st, best.Value));
                else
                    noLevel.Add(st);
            }
        }

        return new Result(found, noLevel);
    }

    private static IEnumerable<string?> Names(HideoutStation s)
    {
        yield return s.Name;
        yield return s.NameEn;
        foreach (var a in s.Aliases)
            yield return a;
    }

    private static int MaxLevel(HideoutStation s) =>
        s.Levels.Count == 0 ? 99 : s.Levels.Max(l => l.Level);

    private static void AppendDebug(IEnumerable<ScreenOcr.Line> lines)
    {
        try
        {
            var file = DataStore.HideoutOcrDebugFile;
            if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                File.Delete(file);
            File.AppendAllText(file,
                $"===== скан {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n" +
                string.Join("\n", lines.Select(l => $"  x={l.X,6:F0} y={l.Y,6:F0} | {l.Text}")) + "\n");
        }
        catch
        {
            // отладочный лог не должен мешать сканированию
        }
    }
}
