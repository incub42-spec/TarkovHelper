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
    /// <summary>How — каким способом получен результат, Note — пояснение для оверлея.</summary>
    public sealed record Result(
        List<StationLevel> Found, List<HideoutStation> NoLevel, string How = "", string? Note = null);
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

        // Основной способ — две маленькие области вместо всего экрана:
        // плитка станции под курсором в нижней панели и шапка открытого окна
        // станции справа сверху. Обе читаются надёжно, а сверка их между собой
        // страхует от того, что курсор стоит не на той станции.
        var cell = await ScanCellAsync(data, r, cursor, onRegion);
        var header = await ScanDetailHeaderAsync(data, r, onRegion);
        var cross = CrossCheck(cell, header);
        if (cross != null) return cross;

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
        var nameHits = MatchNames(data, lines, monitorWidth * 0.006, monitorWidth * 0.06);

        var found = new List<StationLevel>();
        var noLevel = new List<HideoutStation>();

        // окно одной станции: непостроенная подписана «Построить»/«Заблокировано»
        if (nameHits.Count == 1 &&
            lines.Any(l => NotBuiltRegex().IsMatch(l.Text) || LockedRegex().IsMatch(l.Text)))
            found.Add(new StationLevel(nameHits.Values.First().St, 0));

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
    /// Разбор плитки под курсором. Область берём с запасом — курсор может стоять
    /// на краю плитки или на её иконке, — поэтому в кадр попадают и соседние
    /// станции. Из распознанных названий выбираем ближайшее к курсору, а цифру
    /// уровня ищем только рядом с ним: чужие бейджи так не подхватятся.
    /// </summary>
    private static async Task<Result> ScanCellAsync(
        GameData data, RECT r, POINT cursor, Action<Region>? onRegion)
    {
        var width = r.Right - r.Left;
        var height = r.Bottom - r.Top;

        var w = (int)(width * 0.22);
        var h = (int)(height * 0.15);
        var x = Math.Clamp(cursor.X - w / 2, r.Left, Math.Max(r.Left, r.Right - w));
        var y = Math.Clamp(cursor.Y - h / 2, r.Top, Math.Max(r.Top, r.Bottom - h));

        // масштаб 4: область маленькая, можно увеличить сильнее обычного
        var lines = await ScreenOcr.RecognizeLayoutAsync(x, y, w, h, scaleHint: 4);
        onRegion?.Invoke(new Region(x, y, w, h));

        AppendDebug(lines, "ячейка под курсором");

        var cell = width * 0.075; // ширина плитки станции в нижней панели
        var match = MatchNearCursor(data, lines, cursor.X - x, cursor.Y - y, height * 0.012, cell);
        return await ParseSingle(data, lines, "плитка под курсором", match, cell,
            (nx, ny, max) => ReadBadgeAsync(nx, ny, x, y, cell, height, max));
    }

    /// <summary>
    /// Станция в кадре с несколькими плитками: берём ту, чьё название ближе к
    /// курсору (по горизонтали — плитки стоят в ряд). Если построчно не нашлось
    /// ни одной, пробуем склеить весь кадр: значит станция в нём одна, просто
    /// название разорвано на строки.
    /// </summary>
    private static (HideoutStation? St, double X, double Y) MatchNearCursor(
        GameData data, List<ScreenOcr.Line> lines, double cx, double cy, double joinDy, double joinDx)
    {
        var hits = MatchNames(data, lines, joinDy, joinDx);
        if (hits.Count == 0) return MatchInCell(data, lines);

        var pick = hits.Values
            .OrderBy(h => Math.Abs(h.X - cx) + Math.Abs(h.Y - cy) * 0.5)
            .First();
        return (pick.St, pick.X, pick.Y);
    }

    /// <summary>
    /// Шапка открытого окна станции: справа игра показывает название и статус
    /// («Заблокировано» либо уровень). Окно по высоте плавает вместе с размером
    /// содержимого, поэтому полосу берём с запасом, но не доходя до блока
    /// «Требования для постройки» — там чужие названия станций и их цифры.
    /// </summary>
    private static async Task<Result> ScanDetailHeaderAsync(
        GameData data, RECT r, Action<Region>? onRegion)
    {
        var width = r.Right - r.Left;
        var height = r.Bottom - r.Top;

        // окно прижато к правому краю и растёт вверх от нижней панели, поэтому
        // берём всю правую часть экрана без самой панели внизу
        var x = r.Left + (int)(width * 0.34);
        var y = r.Top + (int)(height * 0.05);
        var w = width - (int)(width * 0.34);
        var h = (int)(height * 0.84);

        var lines = await ScreenOcr.RecognizeLayoutAsync(x, y, w, h, scaleHint: 3);
        onRegion?.Invoke(new Region(x, y, w, h));

        AppendDebug(lines, "шапка окна станции");

        var none = new Result(new List<StationLevel>(), new List<HideoutStation>(), "окно станции");

        // заголовок — самое верхнее название в полосе: ниже идёт описание станции,
        // в котором тоже может попасться название другой
        var hits = MatchNames(data, lines, height * 0.012, width * 0.12);
        (HideoutStation? St, double X, double Y) match;
        if (hits.Count > 0)
        {
            var top = hits.Values.OrderBy(v => v.Y).First();
            match = (top.St, top.X, top.Y);
        }
        else
        {
            match = MatchInCell(data, lines);
        }
        if (match.St == null) return none;

        var st = match.St;
        var max = MaxLevel(st);

        // статус пишется вплотную под заголовком — описание станции уже не берём
        var band = lines
            .Where(l => l.Y >= match.Y - height * 0.02 && l.Y <= match.Y + height * 0.05)
            .ToList();

        Result One(int level) => new(
            new List<StationLevel> { new(st, level) }, new List<HideoutStation>(), "окно станции");

        if (band.Any(l => LockedRegex().IsMatch(l.Text)) ||
            lines.Any(l => NotBuiltRegex().IsMatch(l.Text)))
            return One(0);

        // Самая нижняя надпись «УРОВЕНЬ N» в окне — вкладка следующего уровня,
        // построенный на единицу меньше (у станции с цифрой 02 внизу «УРОВЕНЬ 3»).
        // Крупный текст читается несравнимо надёжнее мелкой цифры на иконке.
        var body = lines.Where(l => l.Y > match.Y + height * 0.02).ToList();
        foreach (var l in body.OrderByDescending(l => l.Y))
        {
            var m = LevelRegex().Match(l.Text);
            if (!m.Success) continue;
            var next = int.Parse(m.Groups[1].Value);
            if (next >= 1 && next <= max + 1) return One(Math.Min(next - 1, max));
            break;
        }

        // Вкладки следующего уровня нет, а тело окна прочиталось — станция
        // прокачана до максимума (у «Склада» 04 вместо неё «Склад максимального размера»)
        if (body.Count >= 3) return One(max);

        return await ParseSingle(data, band, "окно станции", match, double.MaxValue,
            (nx, ny, m) => ReadBadgeAsync(nx, ny, x, y, width * 0.075, height, m));
    }

    /// <summary>
    /// Статус уже найденной станции: «Заблокировано»/«Построить», затем цифровой
    /// бейдж на иконке, затем надпись «УРОВЕНЬ N». Именно в таком порядке:
    /// в окне станции «УРОВЕНЬ N» внизу — это вкладка следующего уровня, а не
    /// построенный, построенный показан цифрой на иконке рядом с названием.
    /// maxDx — насколько далеко по горизонтали от названия можно брать строки:
    /// в кадре с соседними плитками это отсекает их подписи и цифры.
    /// readBadge — отдельный проход по иконке, если цифру не видно в общем кадре.
    /// </summary>
    private static async Task<Result> ParseSingle(GameData data, List<ScreenOcr.Line> lines, string how,
        (HideoutStation? St, double X, double Y) match, double maxDx,
        Func<double, double, int, Task<int?>>? readBadge = null)
    {
        var none = new Result(new List<StationLevel>(), new List<HideoutStation>(), how);

        if (match.St == null) return none;
        var st = match.St;

        var near = double.IsInfinity(maxDx) || maxDx == double.MaxValue
            ? lines
            : lines.Where(l => Math.Abs(l.X - match.X) <= maxDx).ToList();

        Result One(int level) => new(
            new List<StationLevel> { new(st, level) }, new List<HideoutStation>(), how);

        var badges = new List<(int Value, double X, double Y)>();
        var locked = false;
        foreach (var l in near)
        {
            var clean = l.Text.Trim();
            // «Заблокировано» пишется прямо под своим названием, поэтому берём
            // его строго рядом: у соседней плитки оно уже не наше
            if (LockedRegex().IsMatch(clean))
            {
                if (Math.Abs(l.X - match.X) <= maxDx * 0.45) locked = true;
                continue;
            }

            // «УРОВЕНЬ N» внизу окна станции не читаем: это вкладка следующего
            // уровня, а не построенного (у станции с цифрой 02 там написано 3)

            if (clean.Length > 3) continue;
            var digits = new string(clean.Where(char.IsDigit).ToArray());
            // «0» в одиночку — это половина «01»/«02»: вторая цифра потерялась,
            // а уровень 0 бейджем не показывают, у непостроенных там замок
            if (digits.Length is >= 1 and <= 2 && digits != "0")
                badges.Add((digits[^1] - '0', l.X, l.Y));
        }

        if (locked || near.Any(l => NotBuiltRegex().IsMatch(l.Text)))
            return One(0);

        // в кадре одна станция, поэтому подходит любой бейдж с допустимым
        // значением — берём ближайший к названию
        var best = (Value: 0, Dist: double.MaxValue);
        foreach (var b in badges)
        {
            if (b.Value > MaxLevel(st)) continue;
            var d = (b.X - match.X) * (b.X - match.X) + (b.Y - match.Y) * (b.Y - match.Y);
            if (d < best.Dist) best = (b.Value, d);
        }
        if (best.Dist < double.MaxValue) return One(best.Value);

        // цифру на иконке общий проход часто не вытягивает: она мелкая и светлая
        // на тёмном шестиграннике — снимаем её отдельно, крупно и контрастно
        if (readBadge != null)
        {
            var badge = await readBadge(match.X, match.Y, MaxLevel(st));
            if (badge != null) return One(badge.Value);
        }

        if (MaxLevel(st) == 1) return One(1); // одноуровневая и не заблокирована

        return new Result(new List<StationLevel>(), new List<HideoutStation> { st }, how);
    }

    /// <summary>
    /// Отдельный проход по иконке станции ради цифры уровня. Иконка стоит слева
    /// от названия, цифра — в её нижней части. Снимаем этот пятачок с большим
    /// увеличением и порогом по яркости, иначе цифра теряется.
    /// nameX/nameY — координаты названия внутри области, originX/originY — её угол.
    /// </summary>
    private static async Task<int?> ReadBadgeAsync(
        double nameX, double nameY, int originX, int originY, double cell, int height, int maxLevel)
    {
        var bx = (int)(originX + nameX - cell * 0.85);
        var by = (int)(originY + nameY - height * 0.012);
        var bw = (int)(cell * 0.9);
        var bh = (int)(height * 0.055);
        if (bw < 8 || bh < 8) return null;

        var lines = await ScreenOcr.RecognizeLayoutAsync(bx, by, bw, bh, scaleHint: 6, binarize: true);
        AppendDebug(lines, "иконка уровня");

        foreach (var l in lines)
        {
            var digits = new string(l.Text.Where(char.IsDigit).ToArray());
            if (digits.Length is < 1 or > 2 || digits == "0") continue;
            // «01» с тёмной иконкой читается как «21»/«61», но последняя цифра верна
            var val = digits[^1] - '0';
            if (val <= maxLevel) return val;
        }
        return null;
    }

    /// <summary>
    /// Сверка двух областей. Если и плитка под курсором, и шапка окна дали одну
    /// и ту же станцию — результат подтверждён. Если станции разные, ничего не
    /// сохраняем: скорее всего курсор стоит не на той плитке, о чём и сообщаем.
    /// null — ни одна область станцию не узнала, дальше идут запасные проходы.
    /// </summary>
    private static Result? CrossCheck(Result cell, Result header)
    {
        var cellSt = Single(cell);
        var headSt = Single(header);

        if (cellSt == null && headSt == null) return null;

        if (cellSt != null && headSt != null && cellSt.Id != headSt.Id)
            return new Result(
                new List<StationLevel>(),
                new List<HideoutStation> { cellSt, headSt },
                "сверка",
                $"Под курсором «{cellSt.Name}», а открыто окно «{headSt.Name}». " +
                "Наведите курсор на плитку той же станции.");

        // уровень берём из окна станции: там он подписан текстом, а не бейджем
        var level = header.Found.FirstOrDefault() ?? cell.Found.FirstOrDefault();
        if (level == null)
            return new Result(
                new List<StationLevel>(),
                new List<HideoutStation> { (cellSt ?? headSt)! },
                "сверка");

        if (cellSt != null && headSt != null)
        {
            var other = header.Found.Count > 0 ? cell.Found.FirstOrDefault() : header.Found.FirstOrDefault();
            var note = other != null && other.Level != level.Level
                ? $"Плитка и окно показали разный уровень ({cell.Found.FirstOrDefault()?.Level} и " +
                  $"{header.Found.FirstOrDefault()?.Level}) — сохранён из окна станции."
                : "Подтверждено плиткой и окном станции.";
            return new Result(new List<StationLevel> { level }, new List<HideoutStation>(), "сверка", note);
        }

        return new Result(
            new List<StationLevel> { level }, new List<HideoutStation>(),
            cellSt != null ? "плитка под курсором" : "окно станции",
            cellSt != null ? "Окно станции не распозналось — уровень взят с плитки."
                           : "Плитка под курсором не распозналась — уровень взят из окна.");
    }

    /// <summary>Единственная станция из результата области, если она там есть.</summary>
    private static HideoutStation? Single(Result r) =>
        r.Found.FirstOrDefault()?.Station ?? r.NoLevel.FirstOrDefault();

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
            // «0» в одиночку — это половина «01»/«02»: вторая цифра потерялась,
            // а уровень 0 бейджем не показывают, у непостроенных там замок
            if (digits.Length is >= 1 and <= 2 && digits != "0")
                badges.Add((digits[^1] - '0', l.X, l.Y));
        }

        var found = new List<StationLevel>();
        var noLevel = new List<HideoutStation>();
        var cell = width * 0.075; // ширина ячейки станции в панели

        foreach (var (st, x, y) in MatchNames(data, lines, height * 0.012, cell).Values)
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

    /// <summary>
    /// Названия станций среди распознанных строк: станция -> позиция.
    /// Длинные названия OCR разбивает на соседние строки («Воздушный» и
    /// «Фильтратор» рядом в одной ячейке), поэтому каждую строку пробуем ещё и
    /// склеенной с соседями справа: joinDy — допуск по высоте, joinDx — по ширине.
    /// </summary>
    private static Dictionary<string, (HideoutStation St, double X, double Y)> MatchNames(
        GameData data, List<ScreenOcr.Line> lines, double joinDy, double joinDx)
    {
        var hits = new Dictionary<string, (HideoutStation St, double X, double Y, int Score)>();
        foreach (var line in lines)
        {
            var variants = new List<string> { line.Text };
            foreach (var other in lines)
            {
                if (ReferenceEquals(other, line)) continue;
                var dx = other.X - line.X;
                if (dx <= 0 || dx > joinDx || Math.Abs(other.Y - line.Y) > joinDy) continue;
                variants.Add(line.Text + " " + other.Text);
            }

            foreach (var v in variants)
            {
                var norm = ItemMatcher.Normalize(v);
                if (norm.Length < 3) continue;
                foreach (var s in data.Stations)
                {
                    var score = Names(s).Max(n => NameScore(norm, n));
                    if (score == 0) continue;
                    // на станцию берём самое верхнее вхождение, при равенстве — самое полное
                    if (!hits.TryGetValue(s.Id, out var ex) || score > ex.Score ||
                        (score == ex.Score && line.Y < ex.Y))
                        hits[s.Id] = (s, line.X, line.Y, score);
                }
            }
        }
        return hits.ToDictionary(kv => kv.Key, kv => (kv.Value.St, kv.Value.X, kv.Value.Y));
    }

    /// <summary>
    /// Насколько распознанный текст похож на название станции: сколько слов
    /// названия в нём нашлось (0 — не подходит). Слова сравниваются как множество
    /// и с поправкой на склонения: в игре «Биткоин ферма», у нас «Ферма биткоинов».
    /// </summary>
    private static int NameScore(string ocrNorm, string? name)
    {
        var words = ItemMatcher.Normalize(name)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .ToArray();
        if (words.Length == 0) return 0;

        var ocrWords = ocrNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // односложные названия («Тир», «Генератор») — только точное слово,
        // иначе цепляем куски чужих строк
        if (words.Length == 1)
            return ocrWords.Contains(words[0]) ? 1 : 0;

        foreach (var w in words)
            if (!ocrWords.Any(o => WordMatches(o, w)))
                return 0;
        return words.Length;
    }

    /// <summary>Слова одного корня: «биткоинов» = «биткоин», «воздуха» = «воздушный».</summary>
    private static bool WordMatches(string a, string b)
    {
        if (a == b) return true;
        var common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common]) common++;
        var shorter = Math.Min(a.Length, b.Length);
        return common >= 5 && common * 10 >= shorter * 6;
    }

    /// <summary>
    /// Станция в кадре одной ячейки. Здесь текст можно склеивать целиком: в области
    /// заведомо одна плитка, поэтому разрыв названия на строки не мешает.
    /// Возвращает станцию и точку названия (от неё ищем цифру уровня).
    /// </summary>
    private static (HideoutStation? St, double X, double Y) MatchInCell(
        GameData data, List<ScreenOcr.Line> lines)
    {
        var blob = ItemMatcher.Normalize(string.Join(" ", lines.Select(l => l.Text)));
        if (blob.Length < 3) return (null, 0, 0);

        HideoutStation? best = null;
        var bestScore = 0;
        foreach (var s in data.Stations)
        {
            var score = Names(s).Max(n => NameScore(blob, n));
            if (score > bestScore) { bestScore = score; best = s; }
        }
        if (best == null) return (null, 0, 0);

        // якорь — первая строка, в которой встретилось слово из названия
        foreach (var line in lines)
        {
            var norm = ItemMatcher.Normalize(line.Text);
            if (Names(best).Any(n => NameScore(norm, n) > 0) ||
                Names(best).Any(n => ItemMatcher.Normalize(n)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(w => w.Length >= 3 && norm.Split(' ').Any(o => WordMatches(o, w)))))
                return (best, line.X, line.Y);
        }
        return (best, lines[0].X, lines[0].Y);
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
