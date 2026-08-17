using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        ChkBarters.IsChecked = s.Progress.ShowBarterItems;
        ChkScanRegion.IsChecked = s.Progress.ShowScanRegion;
        TxtGamePath.Text = s.Progress.GamePath ?? "";
        TxtDataStatus.Text = s.DataStatus;
        TxtWatcherStatus.Text = s.Watcher == null
            ? "Слежение за логами выключено (не указана папка игры)."
            : "Слежение за логами: " + s.Watcher.Status;
        var itemKey = HotkeyNames.Describe(s.Progress.ItemHotkey);
        var hideoutKey = HotkeyNames.Describe(s.Progress.HideoutHotkey);
        BtnItemHotkey.Content = itemKey;
        BtnHideoutHotkey.Content = hideoutKey;
        TxtHotkeyStatus.Text = (OverlayWindow.HotkeyRegistered, OverlayWindow.HideoutHotkeyRegistered) switch
        {
            (true, true) => $"Горячие клавиши активны: {itemKey} — предмет, {hideoutKey} — убежище.",
            (true, false) => $"{itemKey} активна, но {hideoutKey} занята другим приложением.",
            (false, true) => $"{hideoutKey} активна, но {itemKey} занята другим приложением.",
            _ => "Не удалось зарегистрировать горячие клавиши (заняты другими приложениями).",
        };
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
            .Where(n => n.NeededForQuestOrHideout || App.Services.Progress.ShowBarterItems)
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
    }

    private void OnItemSearchChanged(object sender, TextChangedEventArgs e) => ApplyItemFilter();

    private void OnBarterFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Services.Progress.ShowBarterItems = ChkBarters.IsChecked == true;
        App.Services.SaveProgress();
    }

    private void OnScanRegionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Services.Progress.ShowScanRegion = ChkScanRegion.IsChecked == true;
        App.Services.SaveProgress();
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
            var p = App.Services.Progress;
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
        if (ChkHideCompleted.IsChecked == true)
            rows = rows.Where(r => !r.IsCompleted);
        var q = TxtQuestSearch.Text.Trim();
        if (!string.IsNullOrEmpty(q))
            rows = rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Trader.Contains(q, StringComparison.OrdinalIgnoreCase));
        QuestsList.ItemsSource = rows.ToList();
    }

    private void OnQuestFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyQuestFilter();
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
        if (path == (App.Services.Progress.GamePath ?? "")) return;
        App.Services.Progress.GamePath = string.IsNullOrEmpty(path) ? null : path;
        App.Services.SaveProgress();
        App.Services.RestartWatcher();
        TxtWatcherStatus.Text = App.Services.Watcher == null
            ? "Слежение за логами выключено."
            : "Слежение за логами: " + App.Services.Watcher.Status;
        if (!string.IsNullOrEmpty(path) && !Directory.Exists(Path.Combine(path, "Logs")))
            TxtWatcherStatus.Text += " (внимание: в этой папке нет подпапки Logs)";
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
            HideoutText = n.HideoutCount > 0 ? "×" + n.HideoutCount : "";
            BarterText = n.BarterUses > 0 ? n.BarterUses.ToString() : "";
            var flea = n.Item.LastLowPrice is > 0 ? n.Item.LastLowPrice : n.Item.Avg24hPrice;
            PriceText = flea is > 0 ? flea.Value.ToString("N0") : "";
            TraderPriceText = n.Item.TraderSellPrice is > 0
                ? $"{n.Item.TraderSellPrice:N0} ({n.Item.TraderSellName})"
                : "";
            Sources = string.Join(";  ", n.Needs
                .Where(x => x.Kind != NeedKind.Barter)
                .Select(x => $"{x.Source} ×{x.Count}"));
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
    }

    private sealed class QuestRow
    {
        private readonly Quest _quest;

        public QuestRow(Quest quest) => _quest = quest;

        public string Trader => _quest.TraderName;
        public string Name => _quest.Name;
        public string Level => _quest.MinPlayerLevel > 0 ? _quest.MinPlayerLevel.ToString() : "";
        public string Kappa => _quest.KappaRequired ? "да" : "";

        public bool IsCompleted
        {
            get => App.Services.Progress.CompletedQuests.Contains(_quest.Id);
            set
            {
                if (value)
                    App.Services.Progress.CompletedQuests.Add(_quest.Id);
                else
                    App.Services.Progress.CompletedQuests.Remove(_quest.Id);
                App.Services.SaveProgress();
            }
        }
    }

    private sealed class StationRow
    {
        private readonly HideoutStation _station;

        public StationRow(HideoutStation station)
        {
            _station = station;
            LevelOptions = Enumerable.Range(0, station.Levels.Count == 0
                ? 1
                : station.Levels.Max(l => l.Level) + 1).ToList();
        }

        public string Name => _station.Name;
        public List<int> LevelOptions { get; }

        public int CurrentLevel
        {
            get => App.Services.Progress.HideoutLevels.TryGetValue(_station.Id, out var l) ? l : 0;
            set
            {
                App.Services.Progress.HideoutLevels[_station.Id] = value;
                App.Services.SaveProgress();
            }
        }
    }
}
