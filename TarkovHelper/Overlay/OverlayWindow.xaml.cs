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
    private const int QuestHotkeyId = 0x5456;   // F11 — список квестов у торговца
    // Shift к той же клавише: сверка списка с игрой отмечает пачку квестов
    // сданными, поэтому отдельным жестом, а не сама собой
    private const int QuestSyncHotkeyId = 0x5457;
    private const int RaidHotkeyId = 0x5458;   // F8 — сводка по текущей локации

    private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF1));
    private static readonly Brush QuestBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
    private static readonly Brush HideoutBrush = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
    private static readonly Brush BarterBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x90, 0xA4, 0xAE));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
    /// <summary>Красный: станция не распозналась или области разошлись.</summary>
    private static readonly Brush FailBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));

    private readonly DispatcherTimer _hideTimer;
    /// <summary>Опрос курсора: подсказка убирается наведением на неё.</summary>
    private readonly DispatcherTimer _dismissTimer;
    private DateTime _shownAt;
    /// <summary>Сколько подсказка держится, не реагируя на мышь (мс).</summary>
    private const int GraceMs = 400;
    private bool _scanning;

    public static bool HotkeyRegistered { get; private set; }
    public static bool HideoutHotkeyRegistered { get; private set; }
    public static bool QuestHotkeyRegistered { get; private set; }
    public static bool RaidHotkeyRegistered { get; private set; }
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

        // десять секунд: успеть прочитать список квестов и обменов, не отвлекаясь
        // от разбора лута; раньше срока подсказка убирается мышью
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _hideTimer.Tick += (_, _) => HidePanel();

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _dismissTimer.Tick += (_, _) => DismissIfCursorOverPanel();
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
        UnregisterHotKey(hwnd, QuestHotkeyId);
        UnregisterHotKey(hwnd, QuestSyncHotkeyId);
        UnregisterHotKey(hwnd, RaidHotkeyId);

        // кнопки мыши регистрировать не нужно — они приходят через Raw Input
        var p = App.Services.Settings;
        HotkeyRegistered = Services.HotkeyNames.IsMouseButton(p.ItemHotkey)
            || RegisterHotKey(hwnd, HotkeyId, 0, p.ItemHotkey);
        HideoutHotkeyRegistered = Services.HotkeyNames.IsMouseButton(p.HideoutHotkey)
            || RegisterHotKey(hwnd, HideoutHotkeyId, 0, p.HideoutHotkey);
        QuestHotkeyRegistered = Services.HotkeyNames.IsMouseButton(p.QuestHotkey)
            || RegisterHotKey(hwnd, QuestHotkeyId, 0, p.QuestHotkey);
        if (!Services.HotkeyNames.IsMouseButton(p.QuestHotkey))
            RegisterHotKey(hwnd, QuestSyncHotkeyId, MOD_SHIFT, p.QuestHotkey);
        RaidHotkeyRegistered = Services.HotkeyNames.IsMouseButton(p.RaidHotkey)
            || RegisterHotKey(hwnd, RaidHotkeyId, 0, p.RaidHotkey);
    }

    protected override void OnClosed(EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, HotkeyId);
            UnregisterHotKey(hwnd, HideoutHotkeyId);
            UnregisterHotKey(hwnd, QuestHotkeyId);
            UnregisterHotKey(hwnd, QuestSyncHotkeyId);
            UnregisterHotKey(hwnd, RaidHotkeyId);
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
                case QuestHotkeyId:
                    handled = true;
                    _ = ScanQuestsAsync(reconcile: false);
                    break;
                case QuestSyncHotkeyId:
                    handled = true;
                    _ = ScanQuestsAsync(reconcile: true);
                    break;
                case RaidHotkeyId:
                    handled = true;
                    ShowRaidSummary();
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

    /// <summary>
    /// F11: прочитать список квестов у торговца и отметить их выполненными.
    /// Игра нигде не хранит на диске, что сдано, а список на экране —
    /// единственный полный источник. Отметку можно откатить в приложении.
    /// </summary>
    /// <summary>
    /// Сводка по локации, в которую игрок зашёл последней: активные задания
    /// этой карты, что для них собрать и какие ключи взять. Локацию берём из
    /// журнала игры, руками указывать ничего не нужно.
    /// </summary>
    private void ShowRaidSummary()
    {
        GetCursorPos(out var pt);

        var data = App.Services.Data;
        var progress = App.Services.Progress;
        var index = App.Services.Index;
        if (data == null || index == null)
        {
            ShowLines(pt, ("База ещё не загружена", MutedBrush));
            return;
        }

        var raid = Services.RaidWatcher.Current(App.Services.Settings.GamePath);
        if (raid == null)
        {
            ShowLines(pt,
                ("Локация неизвестна", FailBrush),
                ("В журнале игры ещё нет записи о заходе в рейд. Проверьте папку игры в настройках.",
                    MutedBrush));
            return;
        }

        var quests = data.Quests
            .Where(q => q.MapName == raid.MapName)
            .Where(q => !progress.CompletedQuests.Contains(q.Id))
            .Where(q => progress.Fits(q.Faction) && progress.IsAvailable(q))
            .OrderBy(q => q.TraderName)
            .ToList();

        var lines = new List<(string, System.Windows.Media.Brush)>
        {
            ($"{raid.MapName}: заданий {quests.Count}", OkBrush),
        };

        if (quests.Count == 0)
        {
            lines.Add(("Активных заданий на этой карте нет", MutedBrush));
            ShowLines(pt, lines.ToArray());
            return;
        }

        foreach (var q in quests.Take(6))
            lines.Add(($"● {q.TraderName}: {progress.NameOf(q)}", QuestBrush));
        if (quests.Count > 6)
            lines.Add(($"…и ещё {quests.Count - 6}", MutedBrush));

        // что собрать именно здесь: предметы этих заданий, которых не хватает
        var wanted = new List<string>();
        foreach (var q in quests)
        {
            foreach (var obj in q.ItemObjectives)
            {
                foreach (var itemId in obj.ItemIds.Take(1))
                {
                    var needs = index.Get(itemId);
                    if (needs == null) continue;
                    var left = Math.Max(0, obj.Count - progress.InStash(itemId));
                    if (left == 0) continue;

                    var name = needs.Item.Name + (obj.ItemIds.Count > 1
                        ? $" (или {obj.ItemIds.Count - 1} др.)"
                        : "");
                    wanted.Add($"{name} ×{left}" + (obj.FoundInRaid ? " FIR" : ""));
                }
            }
        }

        if (wanted.Count > 0)
        {
            lines.Add(($"Собрать: {wanted.Count}", MutedBrush));
            foreach (var w in wanted.Distinct().Take(6))
                lines.Add(($"○ {w}", TitleBrush));
        }

        // ключи забывают чаще всего — без них вылазка впустую
        var keys = quests
            .SelectMany(q => q.NeededKeys)
            .Distinct()
            .Select(id => data.Items.FirstOrDefault(i => i.Id == id)?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        if (keys.Count > 0)
        {
            lines.Add(($"Взять ключи: {keys.Count}", FailBrush));
            foreach (var k in keys.Take(5))
                lines.Add(($"🔑 {k}", MutedBrush));
        }

        ShowLines(pt, lines.ToArray());
    }

    private async Task ScanQuestsAsync(bool reconcile)
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

            await HidePanelForCaptureAsync();
            var result = await Services.QuestScanner.ScanAsync(data, App.Services.Progress, pt);
            FlashScanRegion(result.Area.X, result.Area.Y, result.Area.W, result.Area.H);

            if (result.Total == 0)
            {
                ShowLines(pt, ("✕ Названий квестов в кадре нет", FailBrush),
                    ($"Прочитано строк: {result.LinesRead}. Откройте у торговца вкладку «Задания» " +
                     "и включите «Завершенные».", MutedBrush));
                return;
            }

            // порядок строк кадра — единственный источник игрового порядка
            if (result.Trader is { } listTrader)
                App.Services.RememberQuestOrder(listTrader, result.Ordered, result.Sections,
                    result.ShortNames, result.FullNames);

            var added = App.Services.MarkQuestsCompleted(result.Completed);
            var failed = App.Services.MarkQuestsFailed(result.Failed);
            // всё, что торговец сейчас показывает, он уже выдал: предыдущие
            // квесты цепочки сданы, даже если мы их не сканировали
            var implied = App.Services.InferCompletedFromChain(
                result.Completed.Concat(result.Active).Concat(result.Failed));
            // по «новое!» цепочку не достраиваем: заблокированный квест виден
            // в списке, а предыдущие в его цепочке ещё не сданы
            // а активный квест не сдан — снимаем отметку, если она была ошибочной.
            // «новое!» так читать нельзя: это метка непросмотренного изменения,
            // и висит она в том числе на только что завершённом задании —
            // проверено на «Профпригодности. Часть 2» у Рефа
            var cleared = App.Services.UnmarkQuestsCompleted(result.Active);
            var lines = new List<(string, System.Windows.Media.Brush)>
            {
                added.Count > 0
                    ? ($"✓ Отмечено выполненными: {added.Count}", OkBrush)
                    : ($"Новых отметок нет (завершённых в кадре: {result.Completed.Count})", MutedBrush),
            };
            foreach (var q in added.Take(6))
                lines.Add(($"● {App.Services.Progress.NameOf(q)}", QuestBrush));
            if (added.Count > 6)
                lines.Add(($"…и ещё {added.Count - 6}", MutedBrush));

            if (failed > 0)
                lines.Add(($"Отмечено проваленными: {failed}", FailBrush));

            if (implied.Count > 0)
                lines.Add(($"По цепочке отмечено ещё: {implied.Count}", OkBrush));
            if (cleared.Count > 0)
            {
                lines.Add(($"Снята ошибочная отметка «выполнен»: {cleared.Count}", OkBrush));
                foreach (var q in cleared.Take(4))
                    lines.Add(($"● {App.Services.Progress.NameOf(q)} — активен", QuestBrush));
            }
            // остальные активные не трогаем: они в работе, а не сданы
            if (result.Active.Count > cleared.Count)
                lines.Add(($"Активных пропущено: {result.Active.Count - cleared.Count}", MutedBrush));
            if (result.New.Count > 0)
                lines.Add(($"С пометкой «новое!» пропущено: {result.New.Count}", MutedBrush));
            if (result.Unknown.Count > 0)
                lines.Add(($"Без статуса в строке пропущено: {result.Unknown.Count}", MutedBrush));
            // Список без завершённых — это ровно то, что торговец сейчас
            // предлагает. Копим увиденное по кадрам: длинный список читается
            // с прокруткой, и судить о нём можно только целиком.
            if (result.IsAvailableList && result.Trader is { } trader)
            {
                var seen = App.Services.RememberSeenQuests(trader, result.Seen);
                App.Services.RememberUnmatched(trader, result.UnmatchedRows);
                App.Services.RememberSeenSections(trader, result.SeenSections, result.AtListTop);

                if (!reconcile)
                {
                    lines.Add(($"{trader}: в обходе списка узнано {seen}", MutedBrush));
                    lines.Add(($"Прокрутите до конца и нажмите " +
                               $"Shift+{Services.HotkeyNames.Describe(App.Services.Settings.QuestHotkey)}" +
                               " — сверю, чего торговец не предлагает", MutedBrush));
                }
                else
                {
                    // Обход закончен: чего в списке не оказалось, того торговец
                    // не выдал. Это наблюдение, а не отметка «сдан».
                    var notIssued = App.Services.FinishTraderWalk(trader);
                    lines.Add(notIssued.Count == 0
                        ? ($"✓ {trader}: список совпадает с игрой", OkBrush)
                        : ($"✓ {trader}: торговец пока не предлагает {notIssued.Count}", OkBrush));

                    foreach (var q in notIssued.Take(6))
                        lines.Add(($"● {App.Services.Progress.NameOf(q)}", QuestBrush));
                    if (notIssued.Count > 6)
                        lines.Add(($"…и ещё {notIssued.Count - 6}", MutedBrush));
                    if (notIssued.Count > 0)
                        lines.Add(("Сданными они не считаются — просто уходят из " +
                                   "сегодняшних дел", MutedBrush));

                    // строка со статусом, которую не привязали к базе, могла быть
                    // как событийным заданием, так и не распознанным квестом
                    // откат на встроенный движок должен быть виден: иначе «облако
            // выключено» и «облако ответило ошибкой» выглядят одинаково
            if (App.Services.Settings.UseYandexOcr && !Services.ScreenOcr.LastUsedCloud)
                lines.Add(($"Облако не прочитало кадр: {Services.YandexOcr.LastError}", FailBrush));

            if (result.UnmatchedRows.Count > 0)
                    {
                        lines.Add(($"Строк без совпадения в базе: {result.UnmatchedRows.Count} — " +
                                   "свяжите их в приложении, вкладка «Квесты»", FailBrush));
                        foreach (var row in result.UnmatchedRows.Take(3))
                            lines.Add(($"● {row}", MutedBrush));
                    }
                }
            }

            if (added.Count + implied.Count > 0 || reconcile)
                lines.Add(("Откатить можно в приложении, вкладка «Квесты»", MutedBrush));
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

            await ShowResultAsync(pt, match.Item);
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

        // Масштаб 3: мелкий шрифт тултипов и меток («MS2000») на 2х читается с мусором.
        // bothLanguages — половина названий в игре латиницей, и русский движок их
        // коверкает («Magnum Research» → «Мадпит РеБеагсћ»), поэтому кадр читается
        // ещё и английским движком.
        var lines = await Services.ScreenOcr.RecognizeLayoutAsync(
            x, y, w, h, scaleHint: 3, savePngPath: png, bothLanguages: true);
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

        // Одна и та же строка глазами двух движков: русский верно читает
        // кириллицу, английский — латиницу, а названия сплошь смешанные
        // («Активные беруши CENS "ProFlex DX5"»). По отдельности ни одно
        // прочтение на название не похоже, вместе — содержат его целиком.
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (ordered[j].Y - ordered[i].Y > 6) break;
                if (Math.Abs(ordered[j].X - ordered[i].X) > 60) continue;

                var both = ordered[i].Text + " " + ordered[j].Text;
                weighted.Add((both, SoftWeight(Math.Min(Dist(ordered[i]), Dist(ordered[j])))));
                debug.AppendLine($"  оба движка | {both}");
            }
        }

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

                // Третьей строкой пробуем все подходящие, а не только первую:
                // кадр читают два движка, и первой снизу нередко оказывается
                // мусорная строка, а нужный хвост названия — следующей.
                var thirds = 0;
                for (var k = j + 1; k < ordered.Count && thirds < 3; k++)
                {
                    if (ordered[k].Y - b.Y > 55) break;
                    if (!NextRow(b, ordered[k])) continue;
                    weighted.Add((pair + " " + ordered[k].Text,
                        SoftWeight(Math.Min(Dist(a), Math.Min(Dist(b), Dist(ordered[k]))))));
                    debug.AppendLine($"  join3   | {pair} {ordered[k].Text}");
                    thirds++;
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

    private async Task ShowResultAsync(POINT pt, Item item)
    {
        var needs = App.Services.Index!.Get(item.Id);
        var lines = new List<(string, Brush)> { (item.Name, TitleBrush) };

        if (item.IsQuestItem)
            lines.Add(("Квестовый предмет", QuestBrush));

        var questNeeds = needs?.Needs.Where(n => n.Kind == NeedKind.Quest).ToList() ?? new List<Need>();
        var hideoutNeeds = needs?.Needs.Where(n => n.Kind == NeedKind.Hideout).ToList() ?? new List<Need>();
        var barterNeeds = needs?.Needs.Where(n => n.Kind == NeedKind.Barter).ToList() ?? new List<Need>();

        // сначала квесты, которые уже можно взять; «позже» — приглушённым
        foreach (var n in questNeeds.OrderByDescending(n => n.Available))
            lines.Add(($"● {n.Source} — ×{n.Count}" + (n.FoundInRaid ? "  (нужен FIR)" : ""),
                n.Available ? QuestBrush : MutedBrush));

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
            {
                // Жетон: Терапевт платит цену, умноженную на уровень убитого,
                // поэтому одна цена из базы ни о чём не говорит. Уровень нарисован
                // цифрой в углу ячейки — дочитываем его и показываем итог.
                var level = item.IsDogtag ? await ReadDogtagLevelAsync(pt) : null;
                if (level is > 0)
                    lines.Add(($"{item.TraderSellName}: {item.TraderSellPrice * level:N0} ₽ " +
                               $"({item.TraderSellPrice:N0} × ур. {level})", MutedBrush));
                else if (item.IsDogtag)
                    lines.Add(($"{item.TraderSellName}: {item.TraderSellPrice:N0} ₽ × уровень жетона",
                        MutedBrush));
                else
                    lines.Add(($"{item.TraderSellName}: {item.TraderSellPrice:N0} ₽", MutedBrush));
            }

            // у стволов игра предлагает цену за собранный, с обвесом — суммы не сойдутся
            if (item.IsWeapon)
                lines.Add(("Цена за голый ствол, без обвеса", MutedBrush));
        }

        ShowLines(pt, lines.ToArray());
    }

    /// <summary>
    /// Уровень убитого с жетона: игра рисует его цифрой в углу ячейки, а Терапевт
    /// платит цену жетона, умноженную на этот уровень. Снимаем пятачок вокруг
    /// курсора крупно и с порогом по яркости — мелкие цифры иначе не читаются, —
    /// и берём число, ближайшее к курсору: у соседних ячеек свои цифры.
    /// </summary>
    private static async Task<int?> ReadDogtagLevelAsync(POINT pt)
    {
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        var cell = (mi.rcMonitor.Right - mi.rcMonitor.Left) * 0.045; // ячейка инвентаря

        var side = (int)(cell * 2);
        var (x, y, w, h) = ClampToVirtualScreen(pt.X - side / 2, pt.Y - side / 2, side, side);
        if (w < 8 || h < 8) return null;

        var lines = await Services.ScreenOcr.RecognizeLayoutAsync(
            x, y, w, h, scaleHint: 4, binarize: true);

        int? best = null;
        var bestDist = double.MaxValue;
        foreach (var l in lines)
        {
            var clean = l.Text.Trim();
            if (clean.Length > 3) continue; // рядом с уровнем другого текста нет
            var digits = new string(clean.Where(char.IsDigit).ToArray());
            if (digits.Length is < 1 or > 2) continue;
            var value = int.Parse(digits);
            if (value is < 1 or > 79) continue; // выше 79 уровень в игре не поднимается

            var dx = l.X - (pt.X - x);
            var dy = l.Y - (pt.Y - y);
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > cell || dist >= bestDist) continue;
            best = value;
            bestDist = dist;
        }
        return best;
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

        // Любой клик убирает подсказку: ждать шесть секунд, пока она пропадёт
        // сама, неудобно. Клавиша сканирования сработает ниже и покажет новую.
        if ((flags & RI_MOUSE_ANY_BUTTON_DOWN) != 0 &&
            Panel.Visibility == Visibility.Visible &&
            (DateTime.UtcNow - _shownAt).TotalMilliseconds >= GraceMs)
        {
            Dispatcher.BeginInvoke(new Action(HidePanel));
        }

        uint pressed = 0;
        if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) pressed = VK_MBUTTON;
        else if ((flags & RI_MOUSE_BUTTON_4_DOWN) != 0) pressed = VK_XBUTTON1;
        else if ((flags & RI_MOUSE_BUTTON_5_DOWN) != 0) pressed = VK_XBUTTON2;
        if (pressed == 0) return;

        var p = App.Services.Settings;
        if (p.ItemHotkey == pressed) _ = ScanAsync();
        else if (p.HideoutHotkey == pressed) _ = ScanHideoutAsync();
        else if (p.QuestHotkey == pressed) _ = ScanQuestsAsync(reconcile: false);
    }

    /// <summary>Скрывает панель и даёт композитору время убрать её с экрана перед снимком.</summary>
    private async Task HidePanelForCaptureAsync()
    {
        if (Panel.Visibility != Visibility.Visible) return;
        HidePanel();
        await Task.Delay(120);
    }

    private void ShowLines(POINT cursor, params (string Text, Brush Brush)[] lines)
    {
        // полосу сбрасываем до замера панели: её прошлая ширина иначе не даст
        // панели ужаться под более короткую подсказку
        LifeBar.BeginAnimation(WidthProperty, null);
        LifeBar.Width = 0;

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

        _shownAt = DateTime.UtcNow;
        _hideTimer.Stop();
        _hideTimer.Start();
        _dismissTimer.Start();
        StartLifeBar();
    }

    /// <summary>
    /// Полоска времени до закрытия: подсказка гаснет по таймеру, и без неё
    /// исчезновение выглядит внезапным — непонятно, программа закрыла окно
    /// или что-то сломалось. Полоса убывает от ширины панели до нуля ровно
    /// за то время, которое подсказка держится на экране.
    /// </summary>
    private void StartLifeBar()
    {
        var width = Panel.ActualWidth - Panel.Padding.Left - Panel.Padding.Right;
        if (width <= 0) return;

        LifeBar.BeginAnimation(WidthProperty, null);
        LifeBar.Width = width;

        var anim = new System.Windows.Media.Animation.DoubleAnimation(
            width, 0, _hideTimer.Interval)
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        LifeBar.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>Убирает подсказку и останавливает оба таймера.</summary>
    private void HidePanel()
    {
        _hideTimer.Stop();
        _dismissTimer.Stop();
        LifeBar.BeginAnimation(WidthProperty, null);
        LifeBar.Width = 0;
        Panel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Подсказка убирается наведением курсора: панель click-through, поймать
    /// мышь событиями окна нельзя, поэтому сравниваем позицию курсора с её
    /// прямоугольником. Небольшая пауза после показа — чтобы подсказка не
    /// пропала мгновенно, если она выехала прямо под курсор.
    /// </summary>
    private void DismissIfCursorOverPanel()
    {
        if (Panel.Visibility != Visibility.Visible) return;
        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < GraceMs) return;

        GetCursorPos(out var pt);
        var dpi = VisualTreeHelper.GetDpi(this);
        var cx = pt.X / dpi.DpiScaleX - Left;
        var cy = pt.Y / dpi.DpiScaleY - Top;

        var px = Canvas.GetLeft(Panel);
        var py = Canvas.GetTop(Panel);
        if (cx >= px && cx <= px + Panel.ActualWidth &&
            cy >= py && cy <= py + Panel.ActualHeight)
        {
            HidePanel();
        }
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
