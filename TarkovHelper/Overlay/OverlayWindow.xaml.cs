using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovHelper.Models;
using static TarkovHelper.Interop.NativeMethods;

namespace TarkovHelper.Overlay;

/// <summary>
/// Прозрачный кликопрозрачный оверлей на весь экран.
/// Хоткей предмета (по умолчанию F9) — распознать предмет под курсором
/// (тултип или окно осмотра) и показать подсказку; клавиши настраиваются.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int HotkeyId = 0x5454;        // F9 — предмет под курсором
    private const int HideoutHotkeyId = 0x5455; // F10 — экран станции убежища

    private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF1));
    private static readonly Brush QuestBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
    private static readonly Brush HideoutBrush = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
    private static readonly Brush BarterBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x90, 0xA4, 0xAE));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
    /// <summary>Красный: станция не распозналась или области разошлись.</summary>
    private static readonly Brush FailBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));

    private readonly DispatcherTimer _hideTimer;
    private bool _scanning;

    public static bool HotkeyRegistered { get; private set; }
    public static bool HideoutHotkeyRegistered { get; private set; }
    /// <summary>Текущий оверлей — чтобы настройки могли перерегистрировать клавиши.</summary>
    public static OverlayWindow? Current { get; private set; }

    public OverlayWindow()
    {
        InitializeComponent();
        Current = this;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Panel.Visibility = Visibility.Collapsed;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        // клики проходят сквозь оверлей, окно не забирает фокус у игры
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        RegisterMouseInput(hwnd);
        ApplyHotkeys();
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    /// <summary>
    /// Подписка на кнопки мыши через Raw Input: система сама присылает нам
    /// уведомления о нажатиях (как push-to-talk в мессенджерах). Это пассивное
    /// чтение — ни перехвата ввода, ни вмешательства в игру.
    /// </summary>
    private static void RegisterMouseInput(IntPtr hwnd)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01, // generic desktop
                usUsage = 0x02,     // mouse
                dwFlags = RIDEV_INPUTSINK, // получать события и без фокуса
                hwndTarget = hwnd,
            },
        };
        RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>
    /// Перерегистрирует горячие клавиши по текущим настройкам.
    /// Вызывается при старте и после смены клавиши в настройках.
    /// </summary>
    public void ApplyHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        UnregisterHotKey(hwnd, HotkeyId);
        UnregisterHotKey(hwnd, HideoutHotkeyId);

        // кнопки мыши регистрировать не нужно — они приходят через Raw Input
        var p = App.Services.Settings;
        HotkeyRegistered = Services.HotkeyNames.IsMouseButton(p.ItemHotkey)
            || RegisterHotKey(hwnd, HotkeyId, 0, p.ItemHotkey);
        HideoutHotkeyRegistered = Services.HotkeyNames.IsMouseButton(p.HideoutHotkey)
            || RegisterHotKey(hwnd, HideoutHotkeyId, 0, p.HideoutHotkey);
    }

    protected override void OnClosed(EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, HotkeyId);
            UnregisterHotKey(hwnd, HideoutHotkeyId);
        }
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            HandleMouseInput(lParam);
            // не помечаем handled: нажатие должно дойти до игры как обычно
        }

        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyId:
                    handled = true;
                    _ = ScanAsync();
                    break;
                case HideoutHotkeyId:
                    handled = true;
                    _ = ScanHideoutAsync();
                    break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>F10: распознать открытый в игре экран станции убежища и сохранить её уровень.</summary>
    private async Task ScanHideoutAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            GetCursorPos(out var pt);

            var data = App.Services.Data;
            if (data == null)
            {
                ShowLines(pt, ("База ещё не загружена", MutedBrush));
                return;
            }

            // прячем свою панель и рамку: прошлый результат не должен попасть в кадр
            await HidePanelForCaptureAsync();
            if (ScanFrame.Visibility == Visibility.Visible || ScanFrame2.Visibility == Visibility.Visible)
            {
                foreach (var f in new[] { ScanFrame, ScanFrame2 })
                {
                    f.BeginAnimation(OpacityProperty, null);
                    f.Visibility = Visibility.Collapsed;
                }
                await Task.Delay(100);
            }

            // снимков несколько подряд, поэтому подсвечиваем только после всех:
            // рамка, попавшая в следующий кадр, испортит распознавание
            var regions = new List<Services.HideoutScanner.Region>();
            var result = await Services.HideoutScanner.ScanAsync(data, pt, regions.Add);
            FlashRegions(regions);

            if (result.Found.Count == 0)
            {
                // название узнали, а цифру уровня — нет: подсказываем, куда навести
                if (result.NoLevel.Count > 0)
                    ShowLines(pt,
                        ($"✕ {string.Join(", ", result.NoLevel.Select(s => s.Name))} — уровень не считался",
                            FailBrush),
                        (result.Note ?? "Наведите курсор на иконку станции с цифрой уровня", MutedBrush));
                else
                    ShowLines(pt, ("✕ Станция не распознана", FailBrush),
                        ($"Наведите курсор на станцию в нижней панели убежища и нажмите " +
                         $"{Services.HotkeyNames.Describe(App.Services.Settings.HideoutHotkey)}", MutedBrush));
                return;
            }

            foreach (var f in result.Found)
            {
                App.Services.Progress.HideoutLevels[f.Station.Id] = f.Level;
                App.Services.Progress.HideoutCheckedUtc[f.Station.Id] = DateTime.UtcNow;
                App.Services.Progress.HideoutImpliedUtc.Remove(f.Station.Id); // увидели вместо догадки
            }
            // станции, которые следуют из условий постройки отсканированной
            // (тренажёрный зал требует «Стену» 6 — значит она достроена)
            var implied = Services.HideoutInference.Apply(data, App.Services.Progress);
            App.Services.SaveProgress();

            var lines = new List<(string, System.Windows.Media.Brush)>();
            if (result.Found.Count == 1)
            {
                var one = result.Found[0];
                lines.Add(($"✓ {one.Station.Name} — ур. {one.Level}", OkBrush));
                lines.Add((result.Note ?? "Сохранено", HideoutBrush));
            }
            else
            {
                lines.Add(($"✓ Убежище обновлено — станций: {result.Found.Count}", OkBrush));
                foreach (var f in result.Found.OrderBy(f => f.Station.Name).Take(10))
                    lines.Add(($"● {f.Station.Name} — ур. {f.Level}", HideoutBrush));
            }
            if (result.Found.Count > 10)
                lines.Add(($"…и ещё {result.Found.Count - 10}", MutedBrush));
            if (result.NoLevel.Count > 0)
                lines.Add(($"Без уровня: {string.Join(", ", result.NoLevel.Select(s => s.Name))}", MutedBrush));
            foreach (var im in implied.Take(3))
                lines.Add(($"Заодно: {im.Station.Name} — не ниже ур. {im.To}", MutedBrush));
            ShowLines(pt, lines.ToArray());
        }
        catch (Exception ex)
        {
            GetCursorPos(out var pt);
            ShowLines(pt, ("Ошибка: " + ex.Message, MutedBrush));
        }
        finally
        {
            _scanning = false;
        }
    }

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            GetCursorPos(out var pt);

            if (App.Services.Matcher == null || App.Services.Index == null)
            {
                ShowLines(pt, ("База ещё не загружена", MutedBrush));
                return;
            }

            // прячем свою панель, чтобы она не попала в кадр и не закрывала тултип
            await HidePanelForCaptureAsync();

            var scanId = DateTime.Now.ToString("HHmmss-fff");
            Services.ItemMatcher.MatchResult? bestRejected = null;

            // Проход 1: блок игрового тултипа — строго НАД курсором (у правого края
            // экрана игра сдвигает тултип влево, поэтому зона широкая влево).
            // Метки ячеек здесь отфильтрованы, поэтому порог ниже: даже подпорченный
            // шумом OCR тултип надёжнее, чем точная метка соседней ячейки.
            var match = await ScanRegionAsync(pt, pt.X - 480, pt.Y - 185, 1040, 175,
                scanId, 1, r => bestRejected = Better(bestRejected, r),
                threshold: 0.62, dropShortLabels: true);

            // проход 2: зона вокруг и ниже курсора — тултип у края экрана
            // переворачивается вниз, а метка ячейки лежит под самим курсором
            match ??= await ScanRegionAsync(pt, pt.X - 60, pt.Y - 40, 480, 280,
                scanId, 2, r => bestRejected = Better(bestRejected, r));

            // проход 3 (запасной): широкая зона — окно осмотра, где название
            // стоит в заголовке далеко от курсора
            match ??= await ScanRegionAsync(pt, pt.X - 180, pt.Y - 120, 760, 420,
                scanId, 3, r => bestRejected = Better(bestRejected, r));

            if (match == null)
            {
                // честно говорим, ЧТО пошло не так: не прочиталось или не нашлось в базе
                if (bestRejected != null && bestRejected.Score >= 0.45)
                {
                    ShowLines(pt, ("Предмет не распознан", MutedBrush),
                        ($"Ближайший кандидат: {bestRejected.Item.Name} " +
                         $"(уверенность {bestRejected.Score:F2} из нужных 0.70)", MutedBrush),
                        ($"OCR прочитал: «{bestRejected.MatchedLine}»", MutedBrush));
                }
                else
                {
                    ShowLines(pt, ("Название в кадре не найдено", MutedBrush),
                        ("Ни одна распознанная строка не похожа на предмет из базы.", MutedBrush),
                        ("Кадры сканов: %AppData%\\TarkovHelper\\debug", MutedBrush));
                }
                return;
            }

            ShowResult(pt, match.Item);
        }
        catch (Exception ex)
        {
            GetCursorPos(out var pt);
            ShowLines(pt, ("Ошибка: " + ex.Message, MutedBrush));
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>
    /// Снимает указанную область, распознаёт её и ищет предмет. Правила весов:
    ///  - короткие строки (метки на ячейках инвентаря) учитываются только в радиусе
    ///    одной ячейки от курсора — метки соседних ячеек отбрасываются совсем;
    ///  - длинные названия (тултип, заголовок осмотра) взвешиваются мягко по
    ///    расстоянию, чтобы при конкуренции побеждал наведённый предмет.
    /// </summary>
    private static Services.ItemMatcher.MatchResult? Better(
        Services.ItemMatcher.MatchResult? a, Services.ItemMatcher.MatchResult? b) =>
        a == null ? b : b == null ? a : b.Score > a.Score ? b : a;

    private async Task<Services.ItemMatcher.MatchResult?> ScanRegionAsync(
        POINT pt, int rx, int ry, int rw, int rh,
        string scanId, int passNo, Action<Services.ItemMatcher.MatchResult?> onRejected,
        double threshold = 0.70, bool dropShortLabels = false)
    {
        // своя рамка от прошлого прохода/нажатия не должна попасть в кадр:
        // пунктир, пересекающий тултип, превращает его в нечитаемые точки
        if (ScanFrame.Visibility == Visibility.Visible)
        {
            ScanFrame.BeginAnimation(OpacityProperty, null);
            ScanFrame.Visibility = Visibility.Collapsed;
            await Task.Delay(100);
        }

        var (x, y, w, h) = ClampToVirtualScreen(rx, ry, rw, rh);

        // отладочный кадр: ровно то, что ушло в OCR
        string? png = null;
        try
        {
            var dir = System.IO.Path.Combine(Services.DataStore.RootDir, "debug");
            System.IO.Directory.CreateDirectory(dir);
            foreach (var old in new System.IO.DirectoryInfo(dir).GetFiles("scan-*.png")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(30))
                old.Delete();
            png = System.IO.Path.Combine(dir, $"scan-{scanId}-p{passNo}.png");
        }
        catch
        {
            // отладка не должна мешать сканированию
        }

        // масштаб 3: мелкий шрифт тултипов и меток («MS2000») на 2х читается с мусором
        var lines = await Services.ScreenOcr.RecognizeLayoutAsync(x, y, w, h, scaleHint: 3, savePngPath: png);
        FlashScanRegion(x, y, w, h);

        // радиус ячейки инвентаря — от ширины монитора (~80 px при 2000)
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        var cellRadius = (mi.rcMonitor.Right - mi.rcMonitor.Left) * 0.04;

        // курсор в координатах снятой области
        var cx = pt.X - x;
        var cy = pt.Y - y;
        double Dist(Services.ScreenOcr.Line l) =>
            Math.Sqrt((l.X - cx) * (l.X - cx) + (l.Y - cy) * (l.Y - cy));
        double SoftWeight(double dist) =>
            dist <= 60 ? 1.0 : Math.Max(0.60, 1.0 - (dist - 60) / 900.0);

        var weighted = new List<(string Text, double Weight)>();
        var debug = new System.Text.StringBuilder();
        foreach (var l in lines)
        {
            var dist = Dist(l);
            var norm = Services.ItemMatcher.Normalize(l.Text);
            // короткие строки — метки ячеек: в тултип-зоне им не место вовсе,
            // в остальных зонах учитываются только в радиусе своей ячейки
            var weight = norm.Length <= 8
                ? (dropShortLabels || dist > cellRadius ? 0 : SoftWeight(dist))
                : SoftWeight(dist);

            if (weight > 0)
                weighted.Add((l.Text, weight));
            debug.AppendLine($"  d={dist,5:F0} w={weight:F2} | {l.Text}");
        }

        // Длинные названия в тултипе переносятся на 2-3 строки — склеиваем строки,
        // лежащие друг под другом с шагом одной текстовой строки и выровненные по
        // левому краю. Пары ищем по геометрии, а не по соседству в списке: между
        // строками тултипа по вертикали может вклиниться метка из сетки инвентаря.
        var ordered = lines.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
        bool NextRow(Services.ScreenOcr.Line top, Services.ScreenOcr.Line bottom) =>
            bottom.Y - top.Y is >= 8 and <= 55 && Math.Abs(bottom.X - top.X) <= 140;

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (ordered[j].Y - ordered[i].Y > 55) break; // дальше по Y только хуже
                if (!NextRow(ordered[i], ordered[j])) continue;

                var a = ordered[i];
                var b = ordered[j];
                var pair = a.Text + " " + b.Text;
                weighted.Add((pair, SoftWeight(Math.Min(Dist(a), Dist(b)))));
                debug.AppendLine($"  join    | {pair}");

                for (var k = j + 1; k < ordered.Count; k++)
                {
                    if (ordered[k].Y - b.Y > 55) break;
                    if (!NextRow(b, ordered[k])) continue;
                    weighted.Add((pair + " " + ordered[k].Text,
                        SoftWeight(Math.Min(Dist(a), Math.Min(Dist(b), Dist(ordered[k]))))));
                    debug.AppendLine($"  join3   | {pair} {ordered[k].Text}");
                    break;
                }
            }
        }

        var diag = new System.Text.StringBuilder();
        var (accepted, rejected) = App.Services.Matcher!.MatchDetailed(weighted, diag, threshold);
        if (accepted == null)
            onRejected(rejected);

        AppendItemDebug(
            $"===== скан {scanId} проход {passNo} область=({x},{y} {w}x{h}) курсор=({pt.X},{pt.Y}) png={System.IO.Path.GetFileName(png) ?? "-"}\n" +
            debug.ToString() + diag +
            (accepted != null
                ? $"  => ПРИНЯТ: {accepted.Item.Name} (score {accepted.Score:F2}, строка «{accepted.MatchedLine}»)\n"
                : rejected != null
                    ? $"  => отклонён лучший: {rejected.Item.Name} (score {rejected.Score:F2} < 0.70, строка «{rejected.MatchedLine}»)\n"
                    : "  => кандидатов нет\n"));
        return accepted;
    }

    private static void AppendItemDebug(string text)
    {
        try
        {
            var file = System.IO.Path.Combine(Services.DataStore.RootDir, "item-ocr-debug.log");
            if (System.IO.File.Exists(file) && new System.IO.FileInfo(file).Length > 1_000_000)
                System.IO.File.Delete(file);
            System.IO.File.AppendAllText(file, text);
        }
        catch
        {
            // отладка не должна мешать сканированию
        }
    }

    private void ShowResult(POINT pt, Item item)
    {
        var needs = App.Services.Index!.Get(item.Id);
        var lines = new List<(string, Brush)> { (item.Name, TitleBrush) };

        if (item.IsQuestItem)
            lines.Add(("Квестовый предмет", QuestBrush));

        var questNeeds = needs?.Needs.Where(n => n.Kind == NeedKind.Quest).ToList() ?? new List<Need>();
        var hideoutNeeds = needs?.Needs.Where(n => n.Kind == NeedKind.Hideout).ToList() ?? new List<Need>();
        var barterNeeds = needs?.Needs.Where(n => n.Kind == NeedKind.Barter).ToList() ?? new List<Need>();

        foreach (var n in questNeeds)
            lines.Add(($"● {n.Source} — ×{n.Count}" + (n.FoundInRaid ? "  (нужен FIR)" : ""), QuestBrush));

        // сначала то, что можно строить прямо сейчас; «позже» — приглушённым
        foreach (var n in hideoutNeeds.OrderByDescending(n => n.Available))
            lines.Add(($"● {n.Source} — ×{n.Count}", n.Available ? HideoutBrush : MutedBrush));

        if (barterNeeds.Count > 0)
        {
            foreach (var n in barterNeeds.Take(3))
                lines.Add(($"○ Обмен: {n.Source} — ×{n.Count}", BarterBrush));
            if (barterNeeds.Count > 3)
                lines.Add(($"○ …и ещё {barterNeeds.Count - 3} обмен(ов)", BarterBrush));
        }

        if (questNeeds.Count == 0 && hideoutNeeds.Count == 0 && !item.IsQuestItem)
        {
            lines.Add(barterNeeds.Count == 0
                ? ("Не нужен для квестов, убежища и обменов", OkBrush)
                : ("Для квестов и убежища не нужен", OkBrush));
        }

        if (!item.IsQuestItem)
        {
            var flea = item.LastLowPrice is > 0 ? item.LastLowPrice : item.Avg24hPrice;
            if (flea is > 0)
                lines.Add(($"Барахолка: {flea:N0} ₽", MutedBrush));
            if (item.TraderSellPrice is > 0)
                lines.Add(($"{item.TraderSellName}: {item.TraderSellPrice:N0} ₽", MutedBrush));
        }

        ShowLines(pt, lines.ToArray());
    }

    /// <summary>
    /// На полторы секунды подсвечивает пунктирной рамкой область, ушедшую в OCR.
    /// Вызывать строго ПОСЛЕ снимка: рамка, попавшая в кадр, портит распознавание.
    /// </summary>
    private void FlashScanRegion(int px, int py, int pw, int ph) =>
        FlashRegion(ScanFrame, px, py, pw, ph);

    /// <summary>Подсветить сразу несколько снятых областей (сканирование убежища).</summary>
    private void FlashRegions(IReadOnlyList<Services.HideoutScanner.Region> regions)
    {
        // рамок две, показываем последние снятые области
        var shown = regions.Skip(Math.Max(0, regions.Count - 2)).ToList();
        if (shown.Count > 0)
            FlashRegion(ScanFrame, shown[0].X, shown[0].Y, shown[0].W, shown[0].H);
        if (shown.Count > 1)
            FlashRegion(ScanFrame2, shown[1].X, shown[1].Y, shown[1].W, shown[1].H);
    }

    private void FlashRegion(System.Windows.Shapes.Rectangle frame, int px, int py, int pw, int ph)
    {
        // отладочная подсветка, включается в настройках
        if (!App.Services.Settings.ShowScanRegion) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        Canvas.SetLeft(frame, px / dpi.DpiScaleX - Left);
        Canvas.SetTop(frame, py / dpi.DpiScaleY - Top);
        frame.Width = pw / dpi.DpiScaleX;
        frame.Height = ph / dpi.DpiScaleY;
        frame.Visibility = Visibility.Visible;

        var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(1500))
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        anim.Completed += (_, _) => frame.Visibility = Visibility.Collapsed;
        frame.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>
    /// Разбирает сообщение Raw Input и запускает сканирование, если нажата
    /// кнопка мыши, назначенная в настройках.
    /// </summary>
    private void HandleMouseInput(IntPtr hRawInput)
    {
        var size = (uint)Marshal.SizeOf<RAWINPUTMOUSE>();
        if (GetRawInputData(hRawInput, RID_INPUT, out var data, ref size,
                (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue)
            return;
        if (data.header.dwType != RIM_TYPEMOUSE) return;

        var flags = data.mouse.usButtonFlags;
        uint pressed = 0;
        if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) pressed = VK_MBUTTON;
        else if ((flags & RI_MOUSE_BUTTON_4_DOWN) != 0) pressed = VK_XBUTTON1;
        else if ((flags & RI_MOUSE_BUTTON_5_DOWN) != 0) pressed = VK_XBUTTON2;
        if (pressed == 0) return;

        var p = App.Services.Settings;
        if (p.ItemHotkey == pressed) _ = ScanAsync();
        else if (p.HideoutHotkey == pressed) _ = ScanHideoutAsync();
    }

    /// <summary>Скрывает панель и даёт композитору время убрать её с экрана перед снимком.</summary>
    private async Task HidePanelForCaptureAsync()
    {
        if (Panel.Visibility != Visibility.Visible) return;
        _hideTimer.Stop();
        Panel.Visibility = Visibility.Collapsed;
        await Task.Delay(120);
    }

    private void ShowLines(POINT cursor, params (string Text, Brush Brush)[] lines)
    {
        PanelContent.Children.Clear();
        for (var i = 0; i < lines.Length; i++)
        {
            PanelContent.Children.Add(new TextBlock
            {
                Text = lines[i].Text,
                Foreground = lines[i].Brush,
                FontSize = i == 0 ? 15 : 13,
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, i == 0 ? 0 : 2, 0, 0),
            });
        }

        // физические пиксели -> DIP этого окна
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = cursor.X / dpi.DpiScaleX - Left + 24;
        var y = cursor.Y / dpi.DpiScaleY - Top + 24;

        Panel.Visibility = Visibility.Visible;
        Panel.UpdateLayout();
        x = Math.Min(x, Width - Panel.ActualWidth - 8);
        y = Math.Min(y, Height - Panel.ActualHeight - 8);
        Canvas.SetLeft(Panel, Math.Max(0, x));
        Canvas.SetTop(Panel, Math.Max(0, y));

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private static (int X, int Y, int W, int H) ClampToVirtualScreen(int x, int y, int w, int h)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        x = Math.Max(vx, Math.Min(x, vx + vw - 1));
        y = Math.Max(vy, Math.Min(y, vy + vh - 1));
        w = Math.Min(w, vx + vw - x);
        h = Math.Min(h, vy + vh - y);
        return (x, y, w, h);
    }
}
