using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovHelper.Models;
using TarkovHelper.Overlay;
using TarkovHelper.Services;

namespace TarkovHelper;

public partial class MainWindow : Window
{
    private List<ItemRow> _allItems = new();
    private List<QuestRow> _allQuests = new();

    public MainWindow()
    {
        InitializeComponent();
        App.Services.Changed += () => Dispatcher.BeginInvoke(RefreshFromServices);
        Loaded += (_, _) => RefreshFromServices();
    }

    /// <summary>Полное обновление всех вкладок из текущего состояния сервисов.</summary>
    private void RefreshFromServices()
    {
        var s = App.Services;

        RefreshProfiles();
        ChkBarters.IsChecked = s.Settings.ShowBarterItems;
        ChkGroupQuests.IsChecked = s.Settings.GroupQuests;
        ChkYandexOcr.IsChecked = s.Settings.UseYandexOcr;
        if (TxtYandexKey.Password.Length == 0 && s.Settings.YandexOcrKey is { } yk)
            TxtYandexKey.Password = yk;
        if (TxtYandexFolder.Text.Length == 0 && s.Settings.YandexFolderId is { } yf)
            TxtYandexFolder.Text = yf;
        RefreshYandexStatus();
        ChkScanRegion.IsChecked = s.Settings.ShowScanRegion;
        TxtGamePath.Text = s.Settings.GamePath ?? "";
        TxtDataStatus.Text = s.DataStatus;
        TxtWatcherStatus.Text = s.Watcher == null
            ? "Слежение за логами выключено (не указана папка игры)."
            : "Слежение за логами: " + s.Watcher.Status;
        var itemKey = HotkeyNames.Describe(s.Settings.ItemHotkey);
        var hideoutKey = HotkeyNames.Describe(s.Settings.HideoutHotkey);
        BtnItemHotkey.Content = itemKey;
        BtnHideoutHotkey.Content = hideoutKey;
        TxtHotkeyStatus.Text = (OverlayWindow.HotkeyRegistered, OverlayWindow.HideoutHotkeyRegistered) switch
        {
            (true, true) => $"Горячие клавиши активны: {itemKey} — предмет, {hideoutKey} — убежище.",
            (true, false) => $"{itemKey} активна, но {hideoutKey} занята другим приложением.",
            (false, true) => $"{hideoutKey} активна, но {itemKey} занята другим приложением.",
            _ => "Не удалось зарегистрировать горячие клавиши (заняты другими приложениями).",
        };
        // подсказки пишем с реальными клавишами: они переназначаемые
        TxtItemHelp.Text =
            $"{itemKey} — наведите курсор на предмет в рейде (виден тултип с названием " +
            "или окно осмотра): подсказка, нужен ли предмет. Игра должна работать " +
            "в оконном режиме или borderless.";
        TxtHideoutHelpTitle.Text = $"{hideoutKey} — сканирование убежища, по одной станции за раз:";
        TxtHideoutHelp.Text =
            "1. Щёлкните левой кнопкой по станции в нижней панели убежища — откроется её окно.\n" +
            $"2. Не убирая курсор со станции, нажмите {hideoutKey}.\n" +
            "3. Так по очереди с каждой станцией.\n\n" +
            "Программа читает две области — плитку под курсором и окно станции — и сохраняет " +
            "уровень, только если они совпали. Если курсор увести на другую плитку, она об этом " +
            "скажет и ничего не запишет. Станции, которых в панели нет (например «Стена», когда " +
            "проход за ней открыт), достраиваются сами по условиям постройки других станций.";

        var questKey = HotkeyNames.Describe(s.Settings.QuestHotkey);
        BtnQuestHotkey.Content = questKey;
        BtnRaidHotkey.Content = HotkeyNames.Describe(s.Settings.RaidHotkey);
        TxtQuestHelpTitle.Text = $"{questKey} — список квестов торговца:";
        TxtQuestHelp.Text =
            "1. Откройте торговца, вкладку «Задания»." + Environment.NewLine +
            "2. Включите галочку «Завершенные» — игра покажет сданные квесты." + Environment.NewLine +
            $"3. Нажмите {questKey}; прокрутите список и нажмите ещё раз." +
            Environment.NewLine + Environment.NewLine +
            "Игра показывает завершённые и активные вперемешку, поэтому статус читается " +
            "в каждой строке: отмечаются только те, где написано «завершено», активные " +
            "пропускаются — сколько чего нашлось, видно в подсказке. Игра нигде не хранит " +
            "на диске, что сдано, а в логах уведомления о сдаче живут недолго — список на " +
            "экране единственный полный источник. Если отсканировали не то, отметки " +
            "снимаются кнопкой отката на вкладке «Квесты». Что именно прочитано, пишется " +
            "в папке %AppData%, файл quest-ocr-debug.log.";

        TxtOcrStatus.Text = ScreenOcr.IsAvailable
            ? $"Windows OCR готов (язык: {ScreenOcr.EngineDescription})."
            : "Windows OCR недоступен — установите языковой пакет в Параметрах Windows.";

        var scanned = App.Services.LastQuestScan.Count;
        BtnUndoScan.Visibility = scanned > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnUndoScan.Content = $"Откатить сканирование ({scanned})";

        RebuildItemRows();
        RebuildQuestRows();
        RebuildStashRows();
        RebuildStationRows();
    }

    // ---------- вкладка «Что собирать» ----------

    private void RebuildItemRows()
    {
        var index = App.Services.Index;
        if (index == null)
        {
            _allItems = new List<ItemRow>();
            ItemsList.ItemsSource = null;
            return;
        }

        _allItems = index.ByItemId.Values
            .Where(n => n.NeededForQuestOrHideout || App.Services.Settings.ShowBarterItems)
            .Select(n => new ItemRow(n))
            .OrderByDescending(r => r.HasPrimary)
            .ThenBy(r => r.Name)
            .ToList();
        ApplyItemFilter();
    }

    private void ApplyItemFilter()
    {
        var q = TxtItemSearch.Text.Trim();
        var rows = string.IsNullOrEmpty(q)
            ? _allItems
            : _allItems.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ItemsList.ItemsSource = rows;
        ApplySort(ItemsList, _itemsSort); // список пересобран — сортировку вернуть
    }

    private void OnItemSearchChanged(object sender, TextChangedEventArgs e) => ApplyItemFilter();

    private void OnBarterFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Services.Settings.ShowBarterItems = ChkBarters.IsChecked == true;
        App.Services.SaveProgress();
    }

    private void OnScanRegionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Services.Settings.ShowScanRegion = ChkScanRegion.IsChecked == true;
        App.Services.SaveProgress();
    }

    // ---------- профили персонажей ----------

    private bool _switchingProfile;

    private void RefreshProfiles()
    {
        _switchingProfile = true;
        var settings = App.Services.Settings;
        CmbProfile.ItemsSource = settings.Profiles.Select(p => $"{p.Name} ({p.ModeName})").ToList();
        CmbProfile.SelectedIndex = settings.Profiles.IndexOf(App.Services.Progress);
        _switchingProfile = false;

        var progress = App.Services.Progress;
        TxtModeBadge.Text = progress.ModeName;
        BadgeMode.Background = new SolidColorBrush(progress.PveMode
            ? Color.FromRgb(0x2E, 0x7D, 0x32)   // PvE — зелёный
            : Color.FromRgb(0xB7, 0x4E, 0x1E)); // PvP — оранжевый
        Title = $"Tarkov Helper — {progress.Name} ({progress.ModeName})";
        MenuDeleteProfile.IsEnabled = settings.Profiles.Count > 1;

        // данные профиля показываем текстом: правятся они в окне редактирования
        TxtProfileInfo.Text =
            (progress.Faction.Length > 0 ? progress.Faction : "фракция не указана") + " · " +
            (progress.PlayerLevel > 0 ? $"{progress.PlayerLevel} ур." : "уровень не указан");
    }

    /// <summary>Меню профиля открывается по кнопке, а не по правой клавише.</summary>
    private void OnProfileMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.ContextMenu == null) return;
        b.ContextMenu.PlacementTarget = b;
        b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        b.ContextMenu.IsOpen = true;
    }

    /// <summary>
    /// Правка профиля целиком: имя, фракция, уровень. В шапке эти поля только
    /// занимали место, а уровень полем ввода выглядел как что-то временное.
    /// </summary>
    private void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        var current = App.Services.Progress;
        var dlg = new ProfileDialog
        {
            Owner = this,
            Title = "Профиль персонажа",
            ModeEditable = false,
            ProfileName = current.Name,
            Faction = current.Faction,
            Level = current.PlayerLevel,
        };
        if (dlg.ShowDialog() != true) return;

        var settings = App.Services.Settings;
        var renamed = !string.Equals(dlg.ProfileName, current.Name, StringComparison.Ordinal);
        if (renamed && settings.Profiles.Any(p => p != current &&
                string.Equals(p.Name, dlg.ProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Профиль с таким именем уже есть.", "Профили",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        current.Faction = dlg.Faction;
        current.PlayerLevel = dlg.Level;
        if (renamed)
        {
            current.Name = dlg.ProfileName;
            settings.ActiveProfile = dlg.ProfileName;
        }

        App.Services.SaveProgress();   // индекс лута зависит от фракции и уровня
        RefreshFromServices();
        FlashLevelSaved();
    }

    /// <summary>Короткое «сохранено» в шапке: подтверждает, что правка принята.</summary>
    private void FlashLevelSaved()
    {
        TxtLevelSaved.Visibility = Visibility.Visible;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            TxtLevelSaved.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private async void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _switchingProfile) return;
        var i = CmbProfile.SelectedIndex;
        var profiles = App.Services.Settings.Profiles;
        if (i < 0 || i >= profiles.Count) return;
        if (profiles[i] == App.Services.Progress) return;

        TxtDataStatus.Text = "Переключаю профиль…";
        await App.Services.SwitchProfileAsync(profiles[i].Name);
        RefreshFromServices();
    }

    private async void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileDialog
        {
            Owner = this,
            Title = "Новый профиль",
            DetailsVisible = false, // фракцию и уровень зададим при редактировании
        };
        if (dlg.ShowDialog() != true) return;

        var s = App.Services.Settings;
        if (s.Profiles.Any(p => string.Equals(p.Name, dlg.ProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Профиль с таким именем уже есть.", "Профили",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        s.Profiles.Add(new Progress { Name = dlg.ProfileName, PveMode = dlg.IsPve });
        App.Services.SaveProgress();
        await App.Services.SwitchProfileAsync(dlg.ProfileName);
        RefreshFromServices();
    }

    private void OnRenameProfileClick(object sender, RoutedEventArgs e)
    {
        var current = App.Services.Progress;
        var dlg = new ProfileDialog
        {
            Owner = this,
            Title = "Переименовать профиль",
            ProfileName = current.Name,
            IsPve = current.PveMode,
            ModeEditable = false, // режим менять нельзя: к нему привязан прогресс
            DetailsVisible = false,
        };
        if (dlg.ShowDialog() != true) return;

        current.Name = dlg.ProfileName;
        App.Services.Settings.ActiveProfile = dlg.ProfileName;
        App.Services.SaveProgress();
        RefreshProfiles();
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        var s = App.Services.Settings;
        if (s.Profiles.Count < 2) return;

        var current = App.Services.Progress;
        var answer = MessageBox.Show(this,
            $"Удалить профиль «{current.Name}» ({current.ModeName})?\n\n" +
            $"Отметки {current.CompletedQuests.Count} квестов и уровни убежища будут потеряны.",
            "Профили", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        s.Profiles.Remove(current);
        App.Services.SaveProgress();
        await App.Services.SwitchProfileAsync(s.Profiles[0].Name);
        RefreshFromServices();
    }

    // ---------- обновление приложения ----------

    private UpdateService.Available? _update;

    /// <summary>Тихая проверка обновлений при запуске: молчит, если всё актуально.</summary>
    public async Task CheckUpdateOnStartupAsync()
    {
        try
        {
            _update = await UpdateService.CheckAsync();
            ShowUpdateState(_update == null
                ? $"Версия {UpdateService.CurrentVersion.ToString(3)} — последняя."
                : $"Доступна версия {_update.Version.ToString(3)} " +
                  $"(у вас {UpdateService.CurrentVersion.ToString(3)}).");
        }
        catch (Exception ex)
        {
            ShowUpdateState($"Версия {UpdateService.CurrentVersion.ToString(3)}. " +
                            $"Проверить обновления не удалось: {ex.Message}");
        }
    }

    private void ShowUpdateState(string status)
    {
        TxtUpdateStatus.Text = status;
        BtnInstallUpdate.Visibility = _update == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = "Проверяю…";
        try
        {
            await CheckUpdateOnStartupAsync();
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;

        var answer = MessageBox.Show(this,
            $"Скачать версию {_update.Version.ToString(3)} и перезапустить приложение?\n\n" +
            "Загрузка занимает около 75 МБ. Прогресс и настройки сохранятся.",
            "Обновление", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        BtnInstallUpdate.IsEnabled = false;
        BtnCheckUpdate.IsEnabled = false;
        var progress = new Progress<double>(p =>
            TxtUpdateStatus.Text = $"Скачиваю обновление… {p:P0}");

        try
        {
            await UpdateService.DownloadAndApplyAsync(_update, progress);
            Application.Current.Shutdown(); // новая версия уже запускается
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = "Не удалось обновиться: " + ex.Message;
            BtnInstallUpdate.IsEnabled = true;
            BtnCheckUpdate.IsEnabled = true;
            MessageBox.Show(this,
                "Не удалось обновиться:\n" + ex.Message +
                "\n\nСкачайте новую версию вручную со страницы релизов на GitHub.",
                "Обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnItemHotkeyClick(object sender, RoutedEventArgs e) =>
        CaptureHotkey(BtnItemHotkey, key =>
        {
            App.Services.Settings.ItemHotkey = key;
            SaveHotkeys();
        });

    private void OnQuestHotkeyClick(object sender, RoutedEventArgs e) =>
        CaptureHotkey(BtnQuestHotkey, key =>
        {
            App.Services.Settings.QuestHotkey = key;
            SaveHotkeys();
        });

    private void OnRaidHotkeyClick(object sender, RoutedEventArgs e) =>
        CaptureHotkey(BtnRaidHotkey, key =>
        {
            App.Services.Settings.RaidHotkey = key;
            SaveHotkeys();
        });

    private void OnHideoutHotkeyClick(object sender, RoutedEventArgs e) =>
        CaptureHotkey(BtnHideoutHotkey, key =>
        {
            App.Services.Settings.HideoutHotkey = key;
            SaveHotkeys();
        });

    /// <summary>Сохраняет клавиши и перерегистрирует их в оверлее.</summary>
    private void SaveHotkeys()
    {
        App.Services.SaveProgress();
        OverlayWindow.Current?.ApplyHotkeys();
        RefreshFromServices();
    }

    /// <summary>
    /// Ждёт нажатия клавиши и назначает её на сканирование. Пока кнопка «слушает»,
    /// она подписана «нажмите клавишу…»; Esc отменяет назначение.
    /// </summary>
    private void CaptureHotkey(Button button, Action<uint> assign)
    {
        var previous = button.Content;
        button.Content = "нажмите клавишу…";

        void Finish()
        {
            button.PreviewKeyDown -= OnKey;
            button.PreviewMouseDown -= OnMouse;
            button.Content = previous;
        }

        void Apply(uint key)
        {
            Finish();
            assign(key);
        }

        void OnKey(object? sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true;
            if (e.Key == System.Windows.Input.Key.Escape) { Finish(); return; }
            if (HotkeyNames.IsForbidden(e.Key)) return;

            var vk = HotkeyNames.ToVirtualKey(e.Key);
            if (vk != 0) Apply(vk);
        }

        void OnMouse(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var vk = HotkeyNames.FromMouseButton(e.ChangedButton);
            if (vk == 0) return;   // левая и правая заняты игрой
            e.Handled = true;
            Apply(vk);
        }

        button.PreviewKeyDown += OnKey;
        button.PreviewMouseDown += OnMouse;
        button.Focus();
    }

    // ---------- вкладка «Квесты» ----------

    private void RebuildQuestRows()
    {
        var data = App.Services.Data;
        _allQuests = data == null
            ? new List<QuestRow>()
            : data.Quests
                .OrderBy(q => FallbackDataClient.TraderRank(q.TraderName))
                // порядок, увиденный при сканировании: так же, как в игре.
                // Чего не сканировали — следом, по уровню
                .ThenBy(q => App.Services.Progress.SectionOf(q) == 0 ? 9 : App.Services.Progress.SectionOf(q))
                .ThenBy(q => App.Services.Progress.OrderOf(q))
                .ThenBy(q => q.MinPlayerLevel)
                .Select(q => new QuestRow(q))
                .ToList();
        ApplyQuestFilter();
    }

    /// <summary>Выбранная локация; пусто — все.</summary>
    private string _mapFilter = "";
    private const string AllMaps = "Все локации";
    private const string AnyMap = "Любая локация";

    private void OnQuestMapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || CmbQuestMap.SelectedItem is not string choice) return;
        _mapFilter = choice == AllMaps ? "" : choice;
        ApplyQuestFilter();
    }

    /// <summary>Список локаций собираем из самих квестов: лишних там не будет.</summary>
    private void RebuildMapFilter()
    {
        var maps = _allQuests
            .Where(r => r.Map.Length > 0)
            .Select(r => r.Map)
            .Distinct()
            .OrderBy(m => m, StringComparer.CurrentCulture)
            .ToList();

        var items = new List<string> { AllMaps, AnyMap };
        items.AddRange(maps);
        if (CmbQuestMap.ItemsSource is List<string> old && old.SequenceEqual(items)) return;

        CmbQuestMap.ItemsSource = items;
        CmbQuestMap.SelectedItem = items.Contains(_mapFilter) ? _mapFilter : AllMaps;
    }

    /// <summary>Выбранная вкладка торговца; пусто — все торговцы.</summary>
    private string _traderTab = "";
    private const string AllTraders = "Все";

    private void OnTraderTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Content: string trader }) return;
        _traderTab = trader == AllTraders ? "" : trader;
        if (IsLoaded) ApplyQuestFilter();
    }

    /// <summary>Вкладки торговцев строятся по тем, у кого есть доступные квесты.</summary>
    private void RebuildTraderTabs()
    {
        var traders = _allQuests
            .Where(r => r.Status == "активно!")
            .Select(r => r.Trader)
            .Distinct()
            .OrderBy(FallbackDataClient.TraderRank)
            .ToList();

        // выбранный торговец мог остаться без доступных квестов — возвращаемся ко всем
        if (_traderTab.Length > 0 && !traders.Contains(_traderTab)) _traderTab = "";

        traders.Insert(0, AllTraders);
        if (TraderTabs.ItemsSource is List<string> current && current.SequenceEqual(traders)) return;

        TraderTabs.ItemsSource = traders;
        TraderTabs.UpdateLayout();
        SelectTraderTab();
    }

    /// <summary>Отмечает кнопку текущего торговца после пересборки вкладок.</summary>
    private void SelectTraderTab()
    {
        var wanted = _traderTab.Length == 0 ? AllTraders : _traderTab;
        foreach (var item in TraderTabs.Items)
        {
            if (TraderTabs.ItemContainerGenerator.ContainerFromItem(item) is not ContentPresenter cp)
                continue;
            cp.ApplyTemplate();
            if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(cp) == 0) continue;
            if (System.Windows.Media.VisualTreeHelper.GetChild(cp, 0) is RadioButton rb)
                rb.IsChecked = (string)item == wanted;
        }
    }

    private void ApplyQuestFilter()
    {
        RebuildTraderTabs();
        RebuildMapFilter();

        // в основном окне только то, чем можно заняться сейчас; полный список — по кнопке
        IEnumerable<QuestRow> rows = _allQuests.Where(r => r.Status == "активно!");

        if (_traderTab.Length > 0)
            rows = rows.Where(r => r.Trader == _traderTab);

        // «Любая локация» — это задания без привязки к карте
        if (_mapFilter == AnyMap) rows = rows.Where(r => r.Map.Length == 0);
        else if (_mapFilter.Length > 0) rows = rows.Where(r => r.Map == _mapFilter);
        // квесты чужой фракции игроку не выдадут — прячем, если фракция указана
        rows = rows.Where(r => App.Services.Progress.Fits(r.Faction));

        var q = TxtQuestSearch.Text.Trim();
        if (!string.IsNullOrEmpty(q))
            rows = rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Trader.Contains(q, StringComparison.OrdinalIgnoreCase));
        ShowTraderColumn(_traderTab.Length == 0);
        RefreshUnmatchedButton();

        var shown = rows.ToList();
        QuestsList.ItemsSource = shown;
        ApplySort(QuestsList, _questsSort); // список пересобран — сортировку вернуть
        ApplyQuestGrouping();

        // Список длиннее, чем в игре, ровно на те квесты, которые игрок уже
        // сдал, а программа об этом не знает: игра нигде не хранит историю на
        // диске, она попадает сюда только сканированием. Показываем размер
        // этого пробела, иначе расхождение выглядит как ошибка фильтра.
        var known = App.Services.Progress.CompletedQuests.Count;
        // Описания и цели берутся из локали SPT; она от марта 2025, и тексты
        // переработанных заданий отстают от игры — у «Оружейника. АКС-74Н»
        // там ещё абзац про нейросети и эргономика 65 вместо 52.
        var stale = App.Services.Data?.Source?.Contains("резервный") == true
            ? " Описания и цели — из локали марта 2025: у переработанных заданий текст в игре другой."
            : "";
        TxtQuestKnowledge.Text =
            $"Показано: {shown.Count}. Выполненными известны {known} из {_allQuests.Count} — " +
            "остальные сданные программа считает доступными, пока их не отсканируешь " +
            $"({Services.HotkeyNames.Describe(App.Services.Settings.QuestHotkey)} на списке «Завершенные» у торговца)." +
            stale;
    }

    /// <summary>
    /// Раскладывает список по разделам торговца — так же, как это делает игра
    /// по своей галочке. Разделы известны из сканирования; у несканированных
    /// квестов раздел берётся из условия лояльности в базе, а если и его нет —
    /// они собираются в отдельную группу, чтобы не выдумывать.
    /// </summary>
    private void ApplyQuestGrouping()
    {
        var view = CollectionViewSource.GetDefaultView(QuestsList.ItemsSource);
        if (view == null) return;

        view.GroupDescriptions.Clear();
        if (ChkGroupQuests.IsChecked == true)
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(QuestRow.Section)));
        view.Refresh();
    }

    /// <summary>
    /// Столбец «Торговец» нужен только на вкладке «Все»: когда торговец выбран
    /// кнопкой, его имя в каждой строке — лишний шум.
    /// </summary>
    private void ShowTraderColumn(bool show)
    {
        if (QuestsList.View is not GridView grid) return;

        var present = grid.Columns.Contains(ColQuestTrader);
        if (show == present) return;

        if (show) grid.Columns.Insert(1, ColQuestTrader);
        else grid.Columns.Remove(ColQuestTrader);
    }

    /// <summary>
    /// «Торговец их не предлагает» — то же, чем заканчивается обход списка,
    /// только руками: игрок видит список в игре и сам знает, чего там нет.
    /// Сданными квесты при этом не считаются.
    /// </summary>
    private void OnNotIssuedClick(object sender, RoutedEventArgs e) => SetIssued(false);

    private void OnIssuedClick(object sender, RoutedEventArgs e) => SetIssued(true);

    private void SetIssued(bool issued)
    {
        var rows = QuestsList.SelectedItems.OfType<QuestRow>().ToList();
        if (rows.Count == 0) return;

        var changed = 0;
        foreach (var row in rows)
        {
            changed += issued
                ? App.Services.Progress.NotIssued.Remove(row.Quest.Id) ? 1 : 0
                : App.Services.Progress.NotIssued.Add(row.Quest.Id) ? 1 : 0;
        }

        if (changed > 0) App.Services.SaveProgress();
    }

    /// <summary>
    /// Строки, прочитанные сканированием, но не найденные в базе. Локаль
    /// отстаёт от игры, и вписывать каждый случай в код бессмысленно — куда
    /// правильнее дать связать их прямо здесь.
    /// </summary>
    private void OnUnmatchedClick(object sender, RoutedEventArgs e)
    {
        var trader = _traderTab.Length > 0
            ? _traderTab
            : App.Services.Progress.UnmatchedRows.FirstOrDefault(x => x.Value.Count > 0).Key;
        if (string.IsNullOrEmpty(trader)) return;

        new LinkQuestsWindow(trader) { Owner = this }.ShowDialog();
        RefreshFromServices();
    }

    /// <summary>Кнопка нужна, только когда есть что связывать.</summary>
    private void RefreshUnmatchedButton()
    {
        var rows = App.Services.Progress.UnmatchedRows;
        var count = _traderTab.Length > 0
            ? rows.TryGetValue(_traderTab, out var mine) ? mine.Count : 0
            : rows.Sum(x => x.Value.Count);

        BtnUnmatched.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnUnmatched.Content = $"Не распознано ({count})…";
    }

    private void OnYandexOcrChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Services.Settings.UseYandexOcr = ChkYandexOcr.IsChecked == true;
        Services.DataStore.SaveSettings(App.Services.Settings);
        RefreshYandexStatus();
    }

    private void OnYandexKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var key = TxtYandexKey.Password.Trim();
        App.Services.Settings.YandexOcrKey = key.Length > 0 ? key : null;
        Services.DataStore.SaveSettings(App.Services.Settings);
        RefreshYandexStatus();
    }

    private void OnYandexFolderChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var id = TxtYandexFolder.Text.Trim();
        App.Services.Settings.YandexFolderId = id.Length > 0 ? id : null;
        Services.DataStore.SaveSettings(App.Services.Settings);
        RefreshYandexStatus();
    }

    /// <summary>
    /// Пробный запрос: рисуем картинку с надписью и просим её прочитать. Так
    /// видно, что ключ и каталог рабочие, — и это не отправляет в облако ничего
    /// личного, в отличие от снимка экрана.
    /// </summary>
    private async void OnYandexTestClick(object sender, RoutedEventArgs e)
    {
        BtnYandexTest.IsEnabled = false;
        TxtYandexStatus.Text = "Проверяю…";
        try
        {
            var settings = new Services.YandexOcr.AppSettingsView(
                App.Services.Settings.YandexOcrKey, App.Services.Settings.YandexFolderId);

            var lines = await Services.YandexOcr.RecognizeAsync(SampleImage("Проверка связи"), settings);
            TxtYandexStatus.Text = lines is { Count: > 0 }
                ? $"Связь есть: прочитано «{lines[0].Text}»."
                : $"Не вышло: {Services.YandexOcr.LastError ?? "ключ или каталог не заполнены"}";
        }
        finally
        {
            BtnYandexTest.IsEnabled = true;
        }
    }

    /// <summary>Чёрный текст на белом — картинка для пробного запроса.</summary>
    private static byte[] SampleImage(string text)
    {
        const int width = 420, height = 90;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 36, Brushes.Black,
                VisualTreeHelper.GetDpi(visual).PixelsPerDip);
            dc.DrawText(formatted, new Point(20, 20));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Пишем, готово ли облако к работе и что ответило в прошлый раз.</summary>
    private void RefreshYandexStatus()
    {
        var s = App.Services.Settings;
        var ready = !string.IsNullOrWhiteSpace(s.YandexOcrKey) &&
                    !string.IsNullOrWhiteSpace(s.YandexFolderId);

        TxtYandexStatus.Text = !ready
            ? "Нужны ключ и каталог. В «API-ключ» — секретное значение ключа (показывается один " +
              "раз при создании). В «Каталог» — идентификатор из таблицы «Каталоги», а не " +
              "идентификатор облака: оба начинаются с b1g… и отличаются только уровнем. " +
              "Значения вида aje… не подходят вовсе — так выглядят сам ключ и сервисный аккаунт."
            : !s.UseYandexOcr
            ? "Ключ и каталог заполнены. Поставьте галочку выше, чтобы списки читало облако."
            : !ready
                ? "Нужны ключ и каталог. В поле «Каталог» идёт идентификатор каталога (b1g…), " +
                  "а не сервисного аккаунта (aje…) — их легко перепутать, лежат на соседних страницах."
                : Services.YandexOcr.LastError is { } error
                    ? $"Последний запрос не удался: {error}"
                    : "Готово: списки квестов читаются облаком, при сбое — встроенным движком.";
    }

    private void OnGroupQuestsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // выбор запоминаем: заново ставить галочку при каждом запуске незачем
        App.Services.Settings.GroupQuests = ChkGroupQuests.IsChecked == true;
        Services.DataStore.SaveSettings(App.Services.Settings);
        ApplyQuestGrouping();
    }

    /// <summary>Откат массовой отметки: ошибиться сканированием списка легко.</summary>
    private void OnUndoQuestScanClick(object sender, RoutedEventArgs e)
    {
        var count = App.Services.UndoQuestScan();
        if (count > 0)
            MessageBox.Show(this, $"Снято отметок: {count}.", "Сканирование квестов",
                MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshFromServices();
    }

    private void OnQuestFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyQuestFilter();
    }

    /// <summary>Уровни и репутация торговцев — по ним отсекаются недоступные квесты.</summary>
    private void OnTradersClick(object sender, RoutedEventArgs e)
    {
        var traders = App.Services.Data?.Quests
            .Select(q => q.TraderName)
            .Where(t => t.Length > 0)
            .Distinct()
            .OrderBy(FallbackDataClient.TraderRank)
            .ToList() ?? new List<string>();
        if (traders.Count == 0) return;

        if (new TradersWindow(traders) { Owner = this }.ShowDialog() == true)
            ApplyQuestFilter();
    }

    /// <summary>Полный список — отдельным окном: в основном показываем только доступные.</summary>
    private void OnShowAllQuestsClick(object sender, RoutedEventArgs e)
    {
        var rows = _allQuests
            .Where(r => App.Services.Progress.Fits(r.Faction))
            .OrderBy(r => FallbackDataClient.TraderRank(r.Trader))
            .ThenBy(r => r.Name)
            .ToList();
        new QuestListWindow(rows, "Все квесты", 0) { Owner = this }.ShowDialog();
        ApplyQuestFilter();
    }

    /// <summary>Описание выбранного квеста: без него список ничего не объясняет.</summary>
    private void OnQuestSelected(object sender, SelectionChangedEventArgs e)
    {
        if (QuestsList.SelectedItem is not QuestRow row)
        {
            TxtQuestTitle.Text = "Выберите квест в списке";
            TxtQuestMeta.Text = TxtQuestDesc.Text = TxtQuestChain.Text = "";
            QuestObjectives.ItemsSource = null;
            TxtQuestObjTitle.Visibility = Visibility.Collapsed;
            return;
        }

        var q = row.Quest;
        TxtQuestTitle.Text = row.Name;

        var meta = new List<string> { row.Trader, row.Status };
        if (q.MinPlayerLevel > 0) meta.Add($"с {q.MinPlayerLevel} ур.");
        if (q.Faction.Length > 0) meta.Add(q.Faction);
        if (q.KappaRequired) meta.Add("нужен для Каппы");
        TxtQuestMeta.Text = string.Join(" · ", meta);

        TxtQuestDesc.Text = q.Description.Length > 0
            ? q.Description
            : "Описание недоступно: этого квеста ещё нет в источнике локализации.";

        var objectives = q.Objectives
            .Select(o => new
            {
                Text = "• " + o.Text + (o.Count > 1 ? $" ×{o.Count}" : "") +
                       (o.Optional ? "  (необязательно)" : ""),
                Brush = o.Optional ? (Brush)MutedTextBrush : TitleTextBrush,
            })
            .ToList();
        QuestObjectives.ItemsSource = objectives;
        TxtQuestObjTitle.Visibility = objectives.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // чего не хватает, чтобы квест открылся
        // условие бывает не только «сдан»: «На распутье» у Рефа выдают, пока
        // активно «Между двух огней», а «Выкуп доверия» — если «Реагент.
        // Часть 4» провален
        static string StatusWord(string status) => status switch
        {
            "active" => "взят",
            "failed" => "провален",
            _ => "сдан",
        };

        var blockers = q.Prerequisites
            .Where(p => !App.Services.Progress.Satisfied(p))
            .Select(p => new
            {
                Quest = App.Services.Data?.Quests.FirstOrDefault(x => x.Id == p.TaskId),
                Need = string.Join(" или ", p.Statuses.Select(StatusWord)),
            })
            .Where(x => x.Quest != null)
            .Select(x => $"{App.Services.Progress.NameOf(x.Quest!)} ({x.Need})")
            .ToList();
        TxtQuestChain.Text = blockers.Count > 0
            ? "Сначала нужно: " + string.Join(", ", blockers)
            : q.Prerequisites.Count > 0
                ? "Цепочка перед ним пройдена."
                : "";

        if (App.Services.Progress.FailedQuests.Contains(q.Id))
            TxtQuestChain.Text += (TxtQuestChain.Text.Length > 0 ? Environment.NewLine : "") +
                                  (q.Restartable
                                      ? "Провален, но его можно взять заново."
                                      : "Провален — этот квест уже не сдать.");

        if (App.Services.Progress.LockedTraders.Contains(q.TraderName))
            TxtQuestChain.Text += (TxtQuestChain.Text.Length > 0 ? Environment.NewLine : "") +
                                  $"{q.TraderName} ещё не открыт.";

        // условия по торговцу — вторая причина, по которой квеста нет в игре
        if (q.TraderConditions.Count > 0)
        {
            var unmet = q.TraderConditions.Where(c => !App.Services.Progress.Meets(c)).ToList();
            var shown = unmet.Count > 0 ? unmet : q.TraderConditions;
            var prefix = unmet.Count > 0 ? "Не хватает: " : "Условия торговца: ";
            TxtQuestChain.Text += (TxtQuestChain.Text.Length > 0 ? Environment.NewLine : "") +
                                  prefix + string.Join("; ", shown.Select(c => c.Describe()));
        }

        // локаль от марта 2025: у переработанных заданий текст расходится с игрой
        if (q.Objectives.Any(o => !o.Translated))
            TxtQuestChain.Text += (TxtQuestChain.Text.Length > 0 ? Environment.NewLine : "") +
                                  "Задание переработали в игре: локаль знает не все его цели, " +
                                  "и описание может быть от старой версии.";

        // Торговец его не показал при обходе списка — значит ещё не выдал
        if (App.Services.Progress.NotIssued.Contains(q.Id))
            TxtQuestChain.Text += (TxtQuestChain.Text.Length > 0 ? Environment.NewLine : "") +
                                  "Торговец пока не выдал: при обходе списка квеста в нём не было.";

        // Квест, увиденный сканированием со статусом «активно!», торговец уже
        // выдал — что бы ни говорили требования из базы. Без этой строки
        // получается ерунда: «доступен», а рядом «не хватает уровня».
        if (App.Services.Progress.ActiveQuests.Contains(q.Id))
            TxtQuestChain.Text += (TxtQuestChain.Text.Length > 0 ? Environment.NewLine : "") +
                                  "Взят в игре — сканирование видело его активным, " +
                                  "поэтому требования выше уже неактуальны.";
    }

    /// <summary>
    /// Выполненные — отдельным окном. Их сотни, и в основном списке они мешают,
    /// но иногда нужно посмотреть, что именно отмечено, и снять лишнее.
    /// </summary>
    private void OnShowCompletedClick(object sender, RoutedEventArgs e)
    {
        var completed = _allQuests
            .Where(r => r.IsCompleted && App.Services.Progress.Fits(r.Faction))
            .OrderByDescending(r => r.CheckedSort)
            .ThenBy(r => FallbackDataClient.TraderRank(r.Trader))
            .ThenBy(r => r.Name)
            .ToList();

        if (completed.Count == 0)
        {
            MessageBox.Show(this, "Выполненных квестов пока нет.", "Выполненные квесты",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new QuestListWindow(completed, "Выполненные квесты", 3) { Owner = this }.ShowDialog();
        ApplyQuestFilter(); // в окне могли снять отметку
    }

    /// <summary>
    /// Своё название квеста. Нужно для квестов, которых ещё нет ни в локалях,
    /// ни на русской вики: их название видно только в самой игре, а придумывать
    /// перевод за игрока нельзя. Название хранится в профиле и переживает
    /// обновление базы.
    /// </summary>
    private void OnRenameQuestClick(object sender, RoutedEventArgs e)
    {
        if (QuestsList.SelectedItem is not QuestRow row) return;

        var dlg = new ProfileDialog
        {
            Owner = this,
            Title = "Название квеста",
            Prompt = $"Название квеста ({row.Trader}), как в игре:",
            ModeEditable = false,
            DetailsVisible = false,
            ProfileName = row.Name,
        };
        if (dlg.ShowDialog() != true) return;

        var p = App.Services.Progress;
        if (string.Equals(dlg.ProfileName, row.Quest.Name, StringComparison.Ordinal))
            p.QuestNames.Remove(row.Quest.Id); // вернули как в базе — своё имя не нужно
        else
            p.QuestNames[row.Quest.Id] = dlg.ProfileName;

        App.Services.SaveProgress();
        App.Services.RebuildIndex(); // название видно и в подсказке оверлея
        RebuildQuestRows();
    }

    // ---------- вкладка «Убежище» ----------

    private void RebuildStationRows()
    {
        var data = App.Services.Data;
        StationsList.ItemsSource = data?.Stations
            .OrderBy(s => s.Name)
            .Select(s => new StationRow(s))
            .ToList();
    }

    /// <summary>
    /// Подтвердить уровень станции вручную. Нужно, когда сканировать нечего:
    /// «Стены» в панели убежища уже нет, а уровень в списке и так верный, значит
    /// сменой значения отметку не поставить.
    /// </summary>
    private void OnStationCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StationRow row })
            row.MarkChecked();
    }

    // ---------- вкладка «Настройки» ----------

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        TxtDataStatus.Text = "Загрузка с tarkov.dev…";
        var error = await App.Services.RefreshDataAsync();
        BtnRefresh.IsEnabled = true;
        if (error != null)
            TxtDataStatus.Text = "Ошибка: " + error;
    }

    private void OnBrowseGamePath(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Укажите папку с игрой (в ней должна быть папка Logs)",
        };
        if (dialog.ShowDialog(this) == true)
        {
            TxtGamePath.Text = dialog.FolderName;
            SaveGamePath();
        }
    }

    private void OnGamePathChanged(object sender, RoutedEventArgs e) => SaveGamePath();

    private void OnImportLogsClick(object sender, RoutedEventArgs e)
    {
        var result = App.Services.ImportQuestsFromLogs();
        if (result == null)
        {
            MessageBox.Show(this,
                "Сначала укажите папку игры (в ней должна быть подпапка Logs) и дождитесь загрузки базы.",
                "Импорт из логов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this,
            $"В логах найдено сдач квестов: {result.Value.Found}.\n" +
            $"Отмечено новых: {result.Value.Added}.\n\n" +
            "Квесты, сданные до самой старой записи в логах, нужно отметить вручную.",
            "Импорт из логов", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveGamePath()
    {
        var path = TxtGamePath.Text.Trim();
        if (path == (App.Services.Settings.GamePath ?? "")) return;
        App.Services.Settings.GamePath = string.IsNullOrEmpty(path) ? null : path;
        App.Services.SaveProgress();
        App.Services.RestartWatcher();
        TxtWatcherStatus.Text = App.Services.Watcher == null
            ? "Слежение за логами выключено."
            : "Слежение за логами: " + App.Services.Watcher.Status;
        if (!string.IsNullOrEmpty(path) && !Directory.Exists(Path.Combine(path, "Logs")))
            TxtWatcherStatus.Text += " (внимание: в этой папке нет подпапки Logs)";
    }

    // ---------- сортировка таблиц ----------

    /// <summary>Выбранный столбец и направление для одного списка.</summary>
    private sealed class SortState
    {
        public string? Property;
        public ListSortDirection Direction = ListSortDirection.Ascending;
    }

    private readonly SortState _itemsSort = new();
    private readonly SortState _questsSort = new();

    /// <summary>
    /// Ключ сортировки столбца. Для большинства — свойство из привязки, но у
    /// колонок вида «×10» и цен сортировать надо по числу, а не по подписи,
    /// иначе «×10» окажется раньше «×2». Столбцы с шаблоном (галочка, дата
    /// проверки) привязки не имеют — их узнаём по заголовку.
    /// </summary>
    private static readonly Dictionary<string, string> SortKeyByBinding = new()
    {
        ["QuestText"] = "QuestCount",
        ["FirText"] = "FirCount",
        ["HideoutText"] = "HideoutCount",
        ["BarterText"] = "BarterCount",
        ["PriceText"] = "Price",
        ["TraderPriceText"] = "TraderPrice",
        ["Level"] = "LevelValue",
    };

    private static readonly Dictionary<string, string> SortKeyByHeader = new()
    {
        ["Выполнен"] = "IsCompleted",
        ["Отмечен"] = "CheckedSort",
    };

    private static string? SortKeyFor(GridViewColumn column)
    {
        if (column.DisplayMemberBinding is Binding { Path.Path: { Length: > 0 } path })
            return SortKeyByBinding.TryGetValue(path, out var better) ? better : path;

        var header = HeaderText(column);
        return SortKeyByHeader.TryGetValue(header, out var key) ? key : null;
    }

    /// <summary>Заголовок без стрелки сортировки.</summary>
    private static string HeaderText(GridViewColumn column) =>
        (column.Header?.ToString() ?? "").TrimEnd(' ', '▲', '▼');

    /// <summary>
    /// Клик по заголовку столбца сортирует список, повторный клик по тому же —
    /// разворачивает порядок. Столбец, по которому идёт сортировка, помечается
    /// стрелкой.
    /// </summary>
    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;
        if (SortKeyFor(column) is not { } property) return;

        var list = (ListView)sender;
        var state = ReferenceEquals(list, QuestsList) ? _questsSort : _itemsSort;

        if (state.Property == property)
        {
            state.Direction = state.Direction == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            state.Property = property;
            // числа и даты полезнее сначала по убыванию: сверху самое крупное и свежее
            state.Direction = IsNumericKey(property)
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }

        ApplySort(list, state);
    }

    private static bool IsNumericKey(string property) =>
        property is "QuestCount" or "FirCount" or "HideoutCount" or "BarterCount"
            or "Price" or "TraderPrice" or "LevelValue" or "CheckedSort" or "IsCompleted";

    /// <summary>Применяет сохранённую сортировку — вызывается и после смены содержимого.</summary>
    private void ApplySort(ListView list, SortState state)
    {
        var view = CollectionViewSource.GetDefaultView(list.ItemsSource);
        if (view == null) return;

        view.SortDescriptions.Clear();
        if (state.Property != null)
            view.SortDescriptions.Add(new SortDescription(state.Property, state.Direction));
        view.Refresh();

        MarkSortedColumn(list, state);
    }

    /// <summary>Стрелка в заголовке: видно, по какому столбцу и в какую сторону.</summary>
    private static void MarkSortedColumn(ListView list, SortState state)
    {
        if (list.View is not GridView grid) return;
        foreach (var column in grid.Columns)
        {
            var title = HeaderText(column);
            if (SortKeyFor(column) == state.Property && state.Property != null)
                title += state.Direction == ListSortDirection.Ascending ? " ▲" : " ▼";
            column.Header = title;
        }
    }

    // ---------- строки таблиц ----------

    /// <summary>Строка схрона: сколько нужно, сколько есть, сколько осталось.</summary>
    internal sealed class StashRow
    {
        private readonly ItemNeeds _needs;

        internal StashRow(ItemNeeds needs)
        {
            _needs = needs;
            ItemId = needs.Item.Id;
            Name = needs.Item.Name;
            Need = needs.QuestCount + needs.HideoutCount;
            Options = needs.Options;
            NeedText = Options > 1 ? $"{Need} (любой из {Options})" : Need.ToString();
            Sources = string.Join(";  ", needs.Needs
                .Where(x => x.Kind != NeedKind.Barter)
                .Select(x => $"{x.Source} ×{x.Count}"));
        }

        public string ItemId { get; }
        public string Name { get; }
        public int Need { get; }
        public string Sources { get; }

        public int Options { get; }
        public string NeedText { get; }

        public int Have => App.Services.Progress.InStash(ItemId);
        public int Left => LeftToFind(_needs);
        public Brush LeftBrush => Left == 0 ? CheckedBrush : TitleTextBrush;
    }

    /// <summary>
    /// Сколько ещё нужно найти. У целей, где подходит несколько предметов,
    /// считаем по всей группе: пятнадцать наушников любых моделей — это
    /// пятнадцать штук всего, и накопленные разные модели складываются.
    /// </summary>
    private static int LeftToFind(ItemNeeds needs)
    {
        var progress = App.Services.Progress;
        var index = App.Services.Index;

        var left = 0;
        foreach (var group in needs.Needs.Where(n => n.Kind != NeedKind.Barter)
                     .GroupBy(n => n.GroupKey))
        {
            var count = group.Sum(n => n.Count);
            var have = group.Key.Length > 0 &&
                       index != null &&
                       index.GroupItems.TryGetValue(group.Key, out var ids)
                ? ids.Sum(progress.InStash)
                : progress.InStash(needs.Item.Id);

            left += Math.Max(0, count - have);
        }
        return left;
    }

    private List<StashRow> _stashRows = new();

    private void RebuildStashRows()
    {
        var index = App.Services.Index;
        _stashRows = index == null
            ? new List<StashRow>()
            : index.ByItemId.Values
                .Where(n => n.NeededForQuestOrHideout)
                .OrderBy(n => n.Item.Name, StringComparer.CurrentCulture)
                .Select(n => new StashRow(n))
                .ToList();
        ApplyStashFilter();
    }

    private void ApplyStashFilter()
    {
        IEnumerable<StashRow> rows = _stashRows;

        if (ChkStashOnlyLeft.IsChecked == true)
            rows = rows.Where(r => r.Left > 0);

        var q = TxtStashSearch.Text.Trim();
        if (q.Length > 0)
            rows = rows.Where(r => r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));

        StashList.ItemsSource = rows.ToList();
    }

    private void OnStashFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyStashFilter();
    }

    private void OnStashPlusClick(object sender, RoutedEventArgs e) => ChangeStash(sender, +1);

    private void OnStashMinusClick(object sender, RoutedEventArgs e) => ChangeStash(sender, -1);

    private void ChangeStash(object sender, int delta)
    {
        if (sender is not FrameworkElement { Tag: string itemId }) return;
        var progress = App.Services.Progress;
        progress.SetStash(itemId, progress.InStash(itemId) + delta);
        App.Services.SaveProgress();
    }

    /// <summary>Число вводят руками, когда в схроне сразу десяток штук.</summary>
    private void OnStashCountEdited(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string itemId } box) return;
        if (!int.TryParse(box.Text.Trim(), out var count)) count = 0;

        var progress = App.Services.Progress;
        if (progress.InStash(itemId) == count) return;

        progress.SetStash(itemId, count);
        App.Services.SaveProgress();
    }

    private void OnStashCountKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        OnStashCountEdited(sender, e);
        e.Handled = true;
    }

    private sealed class ItemRow
    {
        public ItemRow(ItemNeeds n)
        {
            Name = n.Item.Name + (n.Item.IsQuestItem ? " [квестовый]" : "");
            HasPrimary = n.NeededForQuestOrHideout;
            // «×6 (2)» — всего нужно шесть, из них два для квестов, доступных сейчас
            QuestText = n.QuestCount > 0
                ? "×" + n.QuestCount + (n.QuestNowCount > 0 && n.QuestNowCount != n.QuestCount
                    ? $" ({n.QuestNowCount})"
                    : "")
                : "";
            FirText = n.QuestFirCount > 0 ? "×" + n.QuestFirCount : "";
            // «×6 (2)» — всего нужно шесть, из них два для построек, доступных сейчас
            HideoutText = n.HideoutCount > 0
                ? "×" + n.HideoutCount + (n.HideoutNowCount > 0 && n.HideoutNowCount != n.HideoutCount
                    ? $" ({n.HideoutNowCount})"
                    : "")
                : "";
            BarterText = n.BarterUses > 0 ? n.BarterUses.ToString() : "";
            var flea = n.Item.LastLowPrice is > 0 ? n.Item.LastLowPrice : n.Item.Avg24hPrice;
            PriceText = flea is > 0 ? flea.Value.ToString("N0") : "";
            TraderPriceText = n.Item.TraderSellPrice is > 0
                ? $"{n.Item.TraderSellPrice:N0} ({n.Item.TraderSellName})"
                : "";
            Sources = string.Join(";  ", n.Needs
                .Where(x => x.Kind != NeedKind.Barter)
                .Select(x => $"{x.Source} ×{x.Count}"));

            QuestCount = n.QuestNowCount > 0 ? n.QuestNowCount : n.QuestCount;
            FirCount = n.QuestFirCount;
            HideoutCount = n.HideoutCount;
            BarterCount = n.BarterUses;
            Price = flea ?? 0;
            TraderPrice = n.Item.TraderSellPrice ?? 0;

            // Сколько уже лежит в схроне и сколько осталось найти. Без этого
            // список показывает полную потребность и заставляет держать
            // накопленное в голове.
            ItemId = n.Item.Id;
            Have = App.Services.Progress.InStash(n.Item.Id);
            var need = n.QuestCount + n.HideoutCount;
            Left = LeftToFind(n);
            HaveText = Have > 0 ? Have.ToString() : "";
            LeftText = need > 0 ? Left.ToString() : "";
            Enough = need > 0 && Left == 0;

            // «×15 из 23» — пятнадцать штук любых из двадцати трёх моделей
            if (n.Options > 1 && QuestText.Length > 0)
                QuestText += $" из {n.Options}";
        }

        public string ItemId { get; }
        public int Have { get; }
        public int Left { get; }
        public string HaveText { get; }
        public string LeftText { get; }
        /// <summary>Нужное уже собрано — строку можно не искать в рейде.</summary>
        public bool Enough { get; }

        /// <summary>Собранное подсвечиваем зелёным: искать больше не нужно.</summary>
        public Brush LeftBrush => Enough ? CheckedBrush : TitleTextBrush;

        public string Name { get; }
        public bool HasPrimary { get; }
        public string QuestText { get; }
        public string FirText { get; }
        public string HideoutText { get; }
        public string BarterText { get; }
        public string PriceText { get; }
        public string TraderPriceText { get; }
        public string Sources { get; }

        // числовые значения для сортировки: по тексту «×10» шло бы перед «×2»
        public int QuestCount { get; }
        public int FirCount { get; }
        public int HideoutCount { get; }
        public int BarterCount { get; }
        public int Price { get; }
        public int TraderPrice { get; }
    }

    /// <summary>Строка списка квестов; используется и окном выполненных.</summary>
    internal sealed class QuestRow
    {
        private readonly Quest _quest;

        public QuestRow(Quest quest) => _quest = quest;

        public string Trader => _quest.TraderName;
        public Quest Quest => _quest;
        public string Name => App.Services.Progress.NameOf(_quest);
        /// <summary>«USEC»/«BEAR» у квестов своей фракции, пусто у общих.</summary>
        public string Faction => _quest.Faction;
        public string Level => _quest.MinPlayerLevel > 0 ? _quest.MinPlayerLevel.ToString() : "";

        /// <summary>Раздел, в котором квест лежит у торговца в игре.</summary>
        public string Section => App.Services.Progress.SectionName(_quest);

        /// <summary>Локация задания; пусто — подойдёт любая.</summary>
        public string Map => _quest.MapName;

        /// <summary>Место квеста в цепочке: сдан, можно брать или ещё закрыт.</summary>
        /// <summary>
        /// Состояние словами игры: в списке торговца выданное задание помечено
        /// «активно!», сданное — «завершено». Свои термины тут только сбивают.
        /// </summary>
        public string Status => IsCompleted
            ? "завершено"
            : App.Services.Progress.FailedQuests.Contains(_quest.Id) && !_quest.Restartable
                ? "провалено"
                : App.Services.Progress.IsAvailable(_quest) ? "активно!"
                    : App.Services.Progress.NotIssued.Contains(_quest.Id) ? "не выдано" : "закрыто";

        public Brush StatusBrush => Status switch
        {
            "активно!" => CheckedBrush,     // зелёный: можно брать прямо сейчас
            "закрыто" => UncheckedBrush,    // красный: цепочка не пройдена
            "не выдано" => MutedTextBrush,  // торговец пока не предлагает
            "провалено" => UncheckedBrush,  // уже не сдать, если не перезапускаемый
            _ => MutedTextBrush,
        };

        /// <summary>Сколько квестов цепочки ещё не сдано — для сортировки по близости.</summary>
        public int Blockers => _quest.Requires.Count(id => !App.Services.Progress.CompletedQuests.Contains(id));
        public string Kappa => _quest.KappaRequired ? "да" : "";

        // значения для сортировки: по тексту «10» шло бы перед «2», а дата — перед галочкой
        public int LevelValue => _quest.MinPlayerLevel;
        public DateTime CheckedSort =>
            App.Services.Progress.QuestCheckedUtc.TryGetValue(_quest.Id, out var utc)
                ? utc
                : DateTime.MinValue;

        public bool IsCompleted
        {
            get => App.Services.Progress.CompletedQuests.Contains(_quest.Id);
            set
            {
                var p = App.Services.Progress;
                if (value)
                {
                    p.CompletedQuests.Add(_quest.Id);
                    p.QuestCheckedUtc[_quest.Id] = DateTime.UtcNow;
                }
                else
                {
                    p.CompletedQuests.Remove(_quest.Id);
                    p.QuestCheckedUtc.Remove(_quest.Id);
                }
                App.Services.SaveProgress();
            }
        }

        /// <summary>Когда отмечен выполненным — чтобы видеть свежесть данных.</summary>
        public string CheckedAt
        {
            get
            {
                var at = FormatChecked(App.Services.Progress.QuestCheckedUtc, _quest.Id);
                return at.Length == 0 ? "" : "✓ " + at;
            }
        }

        public Brush CheckedBrush => MainWindow.CheckedBrush;
    }

    /// <summary>«17.08 15:42» для недавних отметок, «—» если отметки не было.</summary>
    private static string FormatChecked(Dictionary<string, DateTime> map, string id) =>
        map.TryGetValue(id, out var utc) ? utc.ToLocalTime().ToString("dd.MM HH:mm") : "";

    private sealed class StationRow : INotifyPropertyChanged
    {
        private readonly HideoutStation _station;

        public StationRow(HideoutStation station)
        {
            _station = station;
            LevelOptions = Enumerable.Range(0, station.Levels.Count == 0
                ? 1
                : station.Levels.Max(l => l.Level) + 1).ToList();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name => _station.Name;
        public List<int> LevelOptions { get; }

        public int CurrentLevel
        {
            get => App.Services.Progress.HideoutLevels.TryGetValue(_station.Id, out var l) ? l : 0;
            set
            {
                App.Services.Progress.HideoutLevels[_station.Id] = value;
                MarkChecked();
            }
        }

        /// <summary>
        /// Подтвердить текущее значение вручную. Нужно для станций, которых в игре
        /// не видно (например «Стена», когда проход за ней уже открыт): сканировать
        /// нечего, а сменой уровня в списке отметку не поставить, если он и так верный.
        /// </summary>
        public void MarkChecked()
        {
            App.Services.Progress.HideoutCheckedUtc[_station.Id] = DateTime.UtcNow;
            App.Services.Progress.HideoutImpliedUtc.Remove(_station.Id); // теперь это факт
            App.Services.SaveProgress();
            foreach (var p in new[] { nameof(CheckedAt), nameof(IsChecked), nameof(StatusGlyph), nameof(StatusBrush) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }

        /// <summary>Откуда взялся уровень: скан, ручная отметка или вывод из построек.</summary>
        public string CheckedAt
        {
            get
            {
                var at = FormatChecked(App.Services.Progress.HideoutCheckedUtc, _station.Id);
                if (at.Length > 0) return "проверено " + at;
                return IsImplied
                    ? "не ниже этого — следует из других построек"
                    : "не проверялось";
            }
        }

        public bool IsChecked =>
            App.Services.Progress.HideoutCheckedUtc.ContainsKey(_station.Id);

        /// <summary>Уровень не увиден, а выведен по условиям постройки других станций.</summary>
        public bool IsImplied =>
            !IsChecked && App.Services.Progress.HideoutImpliedUtc.ContainsKey(_station.Id);

        /// <summary>Галочка — проверено, «≈» — выведено логически, крестик — неизвестно.</summary>
        public string StatusGlyph => IsChecked ? "✓" : IsImplied ? "≈" : "✕";
        public Brush StatusBrush =>
            IsChecked ? CheckedBrush : IsImplied ? ImpliedBrush : UncheckedBrush;
    }

    private static readonly Brush CheckedBrush =
        new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));   // зелёный: подтверждено
    private static readonly Brush UncheckedBrush =
        new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));   // красный: ещё не проверяли
    private static readonly Brush TitleTextBrush =
        new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly Brush MutedTextBrush =
        new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));   // серый: уже сдан
    private static readonly Brush ImpliedBrush =
        new SolidColorBrush(Color.FromRgb(0xE6, 0x8A, 0x00));   // янтарный: выведено, не увидено
}
