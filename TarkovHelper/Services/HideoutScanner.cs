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
    /// <summary>How — каким способом получен результат, показывается в оверлее.</summary>
    public sealed record Result(List<StationLevel> Found, List<HideoutStation> NoLevel, string How = "");
    /// <summary>Снятая область экрана — оверлей подсвечивает её для отладки.</summary>
    public sealed record Region(int X, int Y, int W, int H);

    [GeneratedRegex(@"(?i)(?:УРОВЕНЬ|УРОВ|УР|LEVEL|LVL)\W{0,4}(\d{1,2})")]
    private static partial Regex LevelRegex();

    [GeneratedRegex(@"(?i)ПОСТРОИТЬ|НЕ ПОСТРОЕНО|CONSTRUCT|NOT BUILT")]
    private static partial Regex NotBuiltRegex();

    public static async Task<Result> ScanAsync(
        GameData data, POINT cursor, Action<Region>? onRegion = null)
    {
        // сканируем монитор, на котором курсор
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var r = info.rcMonitor;
        var monitorWidth = r.Right - r.Left;

        // Основной способ — одна станция под курсором. Наводим на её плитку
        // (иконка с цифрой уровня + название) и жмём хоткей: маленькая область
        // распознаётся куда надёжнее, чем вся панель разом, а станции набираются
        // по одной. Остальные проходы — запасные.
        var cell = await ScanCellAsync(data, r, cursor, onRegion);
        if (cell.Found.Count > 0 || cell.NoLevel.Count > 0) return cell;

        // Затем вся нижняя панель убежища: там у каждой станции подписаны название
        // и статус — уровень цифрой либо «Заблокировано». Это надёжнее иконок
        // на карте; панель прокручивается, поэтому сканировать можно частями.
        var panel = await ScanBottomPanelAsync(data, r, onRegion);
        if (panel.Found.Count > 0) return panel;

        onRegion?.Invoke(new Region(r.Left, r.Top, monitorWidth, r.Bottom - r.Top));

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

        return new Result(found, noLevel, "весь экран");
    }

    /// <summary>
    /// Точечный разбор одной станции: берём небольшой прямоугольник вокруг курсора,
    /// чтобы в кадр попала ровно одна плитка (иконка с цифрой уровня и название).
    /// Соседние станции в область не влезают, поэтому перепутать уровень нельзя.
    /// </summary>
    private static async Task<Result> ScanCellAsync(
        GameData data, RECT r, POINT cursor, Action<Region>? onRegion)
    {
        var width = r.Right - r.Left;
        var height = r.Bottom - r.Top;

        // ~13% экрана в каждую сторону: одна плитка панели с запасом на подпись
        var w = (int)(width * 0.13);
        var h = (int)(height * 0.13);
        var x = Math.Clamp(cursor.X - w / 2, r.Left, Math.Max(r.Left, r.Right - w));
        var y = Math.Clamp(cursor.Y - h / 2, r.Top, Math.Max(r.Top, r.Bottom - h));

        // масштаб 4: область маленькая, можно увеличить сильнее обычного
        var lines = await ScreenOcr.RecognizeLayoutAsync(x, y, w, h, scaleHint: 4);
        onRegion?.Invoke(new Region(x, y, w, h));

        AppendDebug(lines, "ячейка под курсором");

        // координаты курсора внутри снятой области
        double cx = cursor.X - x, cy = cursor.Y - y;

        var names = MatchNames(data, lines);
        if (names.Count == 0)
            return new Result(new List<StationLevel>(), new List<HideoutStation>(), "ячейка под курсором");

        // если в кадр всё же попало два названия — берём то, что ближе к курсору
        var pick = names.Values
            .OrderBy(n => (n.X - cx) * (n.X - cx) + (n.Y - cy) * (n.Y - cy))
            .First();

        var badges = new List<(int Value, double X, double Y)>();
        var locked = false;
        foreach (var l in lines)
        {
            var clean = l.Text.Trim();
            if (LockedRegex().IsMatch(clean)) { locked = true; continue; }

            // явная надпись «УРОВЕНЬ N» в окне станции — она точнее бейджа
            var m = LevelRegex().Match(clean);
            if (m.Success)
            {
                var val = int.Parse(m.Groups[1].Value);
                if (val <= MaxLevel(pick.St))
                    return new Result(
                        new List<StationLevel> { new(pick.St, val) },
                        new List<HideoutStation>(), "ячейка под курсором");
            }

            if (clean.Length > 3) continue;
            var digits = new string(clean.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 1 and <= 2)
                badges.Add((digits[^1] - '0', l.X, l.Y));
        }

        if (locked || lines.Any(l => NotBuiltRegex().IsMatch(l.Text)))
            return new Result(
                new List<StationLevel> { new(pick.St, 0) },
                new List<HideoutStation>(), "ячейка под курсором");

        // область охватывает одну плитку, поэтому подходит любой бейдж в кадре:
        // берём ближайший к названию с допустимым для станции значением
        var best = (Value: 0, Dist: double.MaxValue);
        foreach (var b in badges)
        {
            if (b.Value > MaxLevel(pick.St)) continue;
            var d = (b.X - pick.X) * (b.X - pick.X) + (b.Y - pick.Y) * (b.Y - pick.Y);
            if (d < best.Dist) best = (b.Value, d);
        }

        if (best.Dist < double.MaxValue)
            return new Result(
                new List<StationLevel> { new(pick.St, best.Value) },
                new List<HideoutStation>(), "ячейка под курсором");

        if (MaxLevel(pick.St) == 1) // одноуровневая станция и не заблокирована
            return new Result(
                new List<StationLevel> { new(pick.St, 1) },
                new List<HideoutStation>(), "ячейка под курсором");

        return new Result(
            new List<StationLevel>(),
            new List<HideoutStation> { pick.St }, "ячейка под курсором");
    }

    /// <summary>
    /// Разбор нижней панели убежища. У каждой станции там: иконка с цифрой
    /// уровня, справа название, а у недоступных — подпись «Заблокировано».
    /// Станции без уровней (Круг сектантов, Тренажёрный зал) считаются
    /// построенными, если рядом нет «Заблокировано».
    /// </summary>
    private static async Task<Result> ScanBottomPanelAsync(
        GameData data, RECT r, Action<Region>? onRegion)
    {
        var width = r.Right - r.Left;
        var height = r.Bottom - r.Top;

        // полоса панели: над нижним меню игры, примерно нижние 14% экрана
        var top = r.Top + (int)(height * 0.855);
        var stripHeight = (int)(height * 0.115);
        var lines = await ScreenOcr.RecognizeLayoutAsync(r.Left, top, width, stripHeight, scaleHint: 3);
        onRegion?.Invoke(new Region(r.Left, top, width, stripHeight));

        AppendDebug(lines, "нижняя панель");

        var badges = new List<(int Value, double X, double Y)>();
        var locks = new List<(double X, double Y)>();
        foreach (var l in lines)
        {
            var clean = l.Text.Trim();
            if (LockedRegex().IsMatch(clean))
            {
                locks.Add((l.X, l.Y));
                continue;
            }
            if (clean.Length > 3) continue;
            var digits = new string(clean.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 1 and <= 2)
                badges.Add((digits[^1] - '0', l.X, l.Y));
        }

        var found = new List<StationLevel>();
        var noLevel = new List<HideoutStation>();
        var cell = width * 0.075; // ширина ячейки станции в панели

        foreach (var (st, x, y) in MatchNames(data, lines).Values)
        {
            // «Заблокировано» пишется прямо под названием, с тем же левым краем
            var locked = locks.Any(p => Math.Abs(p.X - x) <= cell && p.Y > y && p.Y - y < height * 0.05);
            if (locked)
            {
                found.Add(new StationLevel(st, 0));
                continue;
            }

            // цифра уровня — на иконке слева и чуть ниже начала названия
            var best = (Value: 0, Dist: double.MaxValue);
            foreach (var b in badges)
            {
                if (b.Value > MaxLevel(st)) continue;
                var dx = x - b.X;
                var dy = b.Y - y;
                if (dx < 0 || dx > cell || dy < -height * 0.01 || dy > height * 0.05) continue;
                var d = dx * dx + dy * dy;
                if (d < best.Dist) best = (b.Value, d);
            }

            if (best.Dist < double.MaxValue)
                found.Add(new StationLevel(st, best.Value));
            else if (MaxLevel(st) == 1)
                found.Add(new StationLevel(st, 1)); // одноуровневая и не заблокирована
            else
                noLevel.Add(st);
        }

        return new Result(found, noLevel, "нижняя панель");
    }

    /// <summary>Названия станций среди распознанных строк: станция -> позиция.</summary>
    private static Dictionary<string, (HideoutStation St, double X, double Y)> MatchNames(
        GameData data, List<ScreenOcr.Line> lines)
    {
        var hits = new Dictionary<string, (HideoutStation St, double X, double Y)>();
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
                    var hit = norm == nn || (nn.Length >= 5 && norm.Contains(nn));
                    if (!hit) continue;
                    if (!hits.TryGetValue(s.Id, out var ex) || line.Y < ex.Y)
                        hits[s.Id] = (s, line.X, line.Y);
                }
            }
        }
        return hits;
    }

    [GeneratedRegex(@"(?i)заблокир|locked")]
    private static partial Regex LockedRegex();

    private static IEnumerable<string?> Names(HideoutStation s)
    {
        yield return s.Name;
        yield return s.NameEn;
        foreach (var a in s.Aliases)
            yield return a;
    }

    private static int MaxLevel(HideoutStation s) =>
        s.Levels.Count == 0 ? 99 : s.Levels.Max(l => l.Level);

    private static void AppendDebug(IEnumerable<ScreenOcr.Line> lines, string what = "весь экран")
    {
        try
        {
            var file = DataStore.HideoutOcrDebugFile;
            if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                File.Delete(file);
            File.AppendAllText(file,
                $"===== скан {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({what}) =====\n" +
                string.Join("\n", lines.Select(l => $"  x={l.X,6:F0} y={l.Y,6:F0} | {l.Text}")) + "\n");
        }
        catch
        {
            // отладочный лог не должен мешать сканированию
        }
    }
}
