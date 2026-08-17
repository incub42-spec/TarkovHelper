using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
        ChkHideCompleted.IsChecked = true;
        App.Services.Changed += () => Dispatcher.BeginInvoke(RefreshFromServices);
        Loaded += (_, _) => RefreshFromServices();
    }

    /// <summary>Полное обновление всех вкладок из текущего состояния сервисов.</summary>
    private void RefreshFromServices()
    {
        var s = App.Services;

        RefreshProfiles();
        ChkBarters.IsChecked = s.Settings.ShowBarterItems;
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

        TxtOcrStatus.Text = ScreenOcr.IsAvailable
            ? $"Windows OCR готов (язык: {ScreenOcr.EngineDescription})."
            : "Windows OCR недоступен — установите языковой пакет в Параметрах Windows.";

        RebuildItemRows();
        RebuildQuestRows();
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
        var s = App.Services.Settings;
        CmbProfile.ItemsSource = s.Profiles.Select(p => $"{p.Name} ({p.ModeName})").ToList();
        CmbProfile.SelectedIndex = s.Profiles.IndexOf(App.Services.Progress);
        _switchingProfile = false;

        var pve = App.Services.Progress.PveMode;
        TxtModeBadge.Text = App.Services.Progress.ModeName;
        BadgeMode.Background = new SolidColorBrush(pve
            ? Color.FromRgb(0x2E, 0x7D, 0x32)   // PvE — зелёный
            : Color.FromRgb(0xB7, 0x4E, 0x1E)); // PvP — оранжевый
        Title = $"Tarkov Helper — {App.Services.Progress.Name} ({App.Services.Progress.ModeName})";
        BtnDeleteProfile.IsEnabled = s.Profiles.Count > 1;

        _switchingProfile = true;
        CmbFaction.ItemsSource = FactionOptions;
        CmbFaction.SelectedItem = App.Services.Progress.Faction switch
        {
            "USEC" => "USEC",
            "BEAR" => "BEAR",
            _ => FactionOptions[0],
        };
        _switchingProfile = false;
    }

    /// <summary>Первый пункт — «не указана»: тогда показываем квесты обеих фракций.</summary>
    private static readonly List<string> FactionOptions = new() { "не указана", "USEC", "BEAR" };

    private void OnFactionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _switchingProfile) return;
        var picked = CmbFaction.SelectedItem as string ?? "";
        App.Services.Progress.Faction = picked is "USEC" or "BEAR" ? picked : "";
        App.Services.SaveProgress();
        App.Services.RebuildIndex(); // список нужного лута зависит от фракции
        ApplyQuestFilter();
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
        var dlg = new ProfileDialog { Owner = this };
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
            ProfileName = current.Name,
            IsPve = current.PveMode,
            ModeEditable = false, // режим менять нельзя: к нему привязан прогресс
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
        CaptureHotkey(BtnItemHotkey, isItem: true);

    private void OnHideoutHotkeyClick(object sender, RoutedEventArgs e) =>
        CaptureHotkey(BtnHideoutHotkey, isItem: false);

    /// <summary>
    /// Ждёт нажатия клавиши и назначает её на сканирование. Пока кнопка «слушает»,
    /// она подписана «нажмите клавишу…»; Esc отменяет назначение.
    /// </summary>
    private void CaptureHotkey(Button button, bool isItem)
    {
        var previous = button.Content;
        button.Content = "нажмите клавишу…";
        button.Focus();

        System.Windows.Input.KeyEventHandler? onKey = null;
        System.Windows.Input.MouseButtonEventHandler? onMouse = null;

        void Stop()
        {
            button.PreviewKeyDown -= onKey;
            button.PreviewMouseDown -= onMouse;
            button.Content = previous;
        }

        void Assign(uint vk)
        {
            var p = App.Services.Settings;
            if (isItem) p.ItemHotkey = vk; else p.HideoutHotkey = vk;
            App.Services.SaveProgress();

            OverlayWindow.Current?.ApplyHotkeys();
            RefreshFromServices();

            var registered = isItem
                ? OverlayWindow.HotkeyRegistered
                : OverlayWindow.HideoutHotkeyRegistered;
            if (!registered)
            {
                MessageBox.Show(this,
                    $"Клавиша {HotkeyNames.Describe(vk)} занята другим приложением — " +
                    "выберите другую.",
                    "Горячая клавиша", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        onKey = (_, args) =>
        {
            args.Handled = true;
            // системные клавиши приходят как Key.System, реальная лежит в SystemKey
            var key = args.Key == System.Windows.Input.Key.System ? args.SystemKey : args.Key;
            Stop();

            if (key == System.Windows.Input.Key.Escape) return;
            if (HotkeyNames.IsForbidden(key))
            {
                MessageBox.Show(this,
                    "Эту клавишу назначить нельзя: модификаторы и системные клавиши " +
                    "(Esc, Tab, Enter, Win, Caps Lock) сломают управление игрой и Windows.",
                    "Горячая клавиша", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Assign(HotkeyNames.ToVirtualKey(key));
        };

        onMouse = (_, args) =>
        {
            args.Handled = true;
            var vk = HotkeyNames.FromMouseButton(args.ChangedButton);
            Stop();

            if (vk == 0)
            {
                MessageBox.Show(this,
                    "Доступны только колёсико и боковые кнопки мыши: левая и правая " +
                    "заняты стрельбой и прицеливанием в игре.",
                    "Горячая клавиша", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Assign(vk);
        };

        button.PreviewKeyDown += onKey;
        button.PreviewMouseDown += onMouse;
    }

    // ---------- вкладка «Квесты» ----------

    private void RebuildQuestRows()
    {
        var data = App.Services.Data;
        _allQuests = data == null
            ? new List<QuestRow>()
            : data.Quests
                .OrderBy(q => q.TraderName)
                .ThenBy(q => q.MinPlayerLevel)
                .Select(q => new QuestRow(q))
                .ToList();
        ApplyQuestFilter();
    }

    private void ApplyQuestFilter()
    {
        IEnumerable<QuestRow> rows = _allQuests;
        // квесты чужой фракции игроку не выдадут — прячем, если фракция указана
        rows = rows.Where(r => App.Services.Progress.Fits(r.Faction));

        if (ChkHideCompleted.IsChecked == true)
            rows = rows.Where(r => !r.IsCompleted);
        var q = TxtQuestSearch.Text.Trim();
        if (!string.IsNullOrEmpty(q))
            rows = rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Trader.Contains(q, StringComparison.OrdinalIgnoreCase));
        QuestsList.ItemsSource = rows.ToList();
        ApplySort(QuestsList, _questsSort); // список пересобран — сортировку вернуть
    }

    private void OnQuestFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyQuestFilter();
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

    private sealed class ItemRow
    {
        public ItemRow(ItemNeeds n)
        {
            Name = n.Item.Name + (n.Item.IsQuestItem ? " [квестовый]" : "");
            HasPrimary = n.NeededForQuestOrHideout;
            QuestText = n.QuestCount > 0 ? "×" + n.QuestCount : "";
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

            QuestCount = n.QuestCount;
            FirCount = n.QuestFirCount;
            HideoutCount = n.HideoutCount;
            BarterCount = n.BarterUses;
            Price = flea ?? 0;
            TraderPrice = n.Item.TraderSellPrice ?? 0;
        }

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

    private sealed class QuestRow
    {
        private readonly Quest _quest;

        public QuestRow(Quest quest) => _quest = quest;

        public string Trader => _quest.TraderName;
        public Quest Quest => _quest;
        public string Name => App.Services.Progress.NameOf(_quest);
        /// <summary>«USEC»/«BEAR» у квестов своей фракции, пусто у общих.</summary>
        public string Faction => _quest.Faction;
        public string Level => _quest.MinPlayerLevel > 0 ? _quest.MinPlayerLevel.ToString() : "";
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
    private static readonly Brush ImpliedBrush =
        new SolidColorBrush(Color.FromRgb(0xE6, 0x8A, 0x00));   // янтарный: выведено, не увидено
}
