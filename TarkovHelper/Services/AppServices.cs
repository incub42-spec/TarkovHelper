using System.IO;
using System.Windows;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>Состояние приложения: база игры, прогресс, индекс потребностей, вотчер логов.</summary>
public sealed class AppServices
{
    public GameData? Data { get; private set; }
    /// <summary>Общие настройки и список профилей.</summary>
    public AppSettings Settings { get; private set; } = new();
    /// <summary>Прогресс активного профиля (персонажа).</summary>
    public Progress Progress => Settings.Active;
    public NeededItemsIndex? Index { get; private set; }
    public ItemMatcher? Matcher { get; private set; }
    public LogWatcher? Watcher { get; private set; }

    /// <summary>Индекс пересобран (изменился прогресс или обновились данные).</summary>
    public event Action? Changed;

    public string DataStatus =>
        Data == null
            ? "База не загружена — нажмите «Обновить данные»."
            : $"Режим {Progress.ModeName}. Предметов: {Data.Items.Count}, квестов: {Data.Quests.Count}, " +
              $"обменов: {Data.Barters.Count}. " +
              $"Обновлено: {Data.FetchedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm} (источник: {Data.Source})";

    public void Init()
    {
        Settings = DataStore.LoadSettings();
        Settings.GamePath ??= TryDetectGamePath();
        if (string.IsNullOrEmpty(Settings.ActiveProfile))
            Settings.ActiveProfile = Settings.Active.Name;
        DataStore.SaveSettings(Settings); // закрепляем миграцию старого формата
        Data = DataStore.LoadData(Progress.PveMode);
        if (Data != null)
            AfterDataLoaded();

        // Кеш прошлой версии не содержит полей, которые мы начали читать позже
        // (фракции, цепочки квестов, описания, признак оружия). Проверять их
        // по одному пришлось бы бесконечно, поэтому сверяем версию схемы.
        if (Data != null && Data.SchemaVersion < GameData.CurrentSchema)
            _ = RefreshDataAsync();
    }

    /// <summary>Переключает активный профиль и подгружает базу его режима.</summary>
    public async Task SwitchProfileAsync(string name)
    {
        var wasPve = Progress.PveMode;
        Settings.ActiveProfile = name;
        DataStore.SaveSettings(Settings);

        if (Progress.PveMode != wasPve)
        {
            // у режима свой кеш: если его нет, качаем базу
            Data = DataStore.LoadData(Progress.PveMode);
            if (Data == null)
            {
                await RefreshDataAsync();
                return;
            }
            AfterDataLoaded();
        }
        RebuildIndex();
    }

    /// <summary>
    /// Качает свежую базу: сначала GraphQL tarkov.dev, при его недоступности —
    /// резервные источники. Возвращает null при успехе, иначе текст ошибки.
    /// </summary>
    public async Task<string?> RefreshDataAsync()
    {
        string graphqlError;
        try
        {
            var data = await TarkovDevClient.FetchAsync();
            data.Source = "tarkov.dev";
            data.SchemaVersion = GameData.CurrentSchema;
            Data = data;
            DataStore.SaveData(data, Progress.PveMode);
            AfterDataLoaded();
            return null;
        }
        catch (Exception ex)
        {
            graphqlError = ex.Message;
        }

        try
        {
            var data = await FallbackDataClient.FetchAsync();
            data.Source = "резервный (json.tarkov.dev + SPT)";
            data.SchemaVersion = GameData.CurrentSchema;
            Data = data;
            DataStore.SaveData(data, Progress.PveMode);
            AfterDataLoaded();
            return null;
        }
        catch (Exception ex)
        {
            return $"tarkov.dev: {graphqlError}; резервный источник: {ex.Message}";
        }
    }

    private void AfterDataLoaded()
    {
        // кеш мог быть собран прошлой версией с другими названиями станций
        FallbackDataClient.ApplyStationNames(Data!);
        // уровни станций, которые видно только по условиям постройки других
        if (HideoutInference.Apply(Data!, Progress).Count > 0)
            DataStore.SaveSettings(Settings);
        Matcher = new ItemMatcher(Data!);
        RebuildIndex();
        RestartWatcher();
    }

    public void RebuildIndex()
    {
        if (Data != null)
            Index = NeededItemsIndex.Build(Data, Progress);
        Changed?.Invoke();
    }

    public void SaveProgress()
    {
        DataStore.SaveSettings(Settings);
        RebuildIndex();
    }

    public void RestartWatcher()
    {
        Watcher?.Dispose();
        Watcher = null;
        if (Data == null || string.IsNullOrWhiteSpace(Settings.GamePath)) return;
        if (!Directory.Exists(Settings.GamePath)) return;

        Watcher = new LogWatcher(
            Settings.GamePath,
            Data.Quests.Select(q => q.Id),
            OnQuestCompletedFromLog);
    }

    private void OnQuestCompletedFromLog(string questId)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (Progress.CompletedQuests.Add(questId))
                SaveProgress();
        });
    }

    /// <summary>
    /// Полный проход по всем логам игры: собирает историю сдачи квестов.
    /// Возвращает (найдено в логах, добавлено новых) или null, если папка не задана.
    /// </summary>
    public (int Found, int Added)? ImportQuestsFromLogs()
    {
        if (Data == null || string.IsNullOrWhiteSpace(Settings.GamePath)) return null;
        var logsDir = Path.Combine(Settings.GamePath, "Logs");
        if (!Directory.Exists(logsDir)) return null;

        var known = Data.Quests.Select(q => q.Id).ToHashSet();
        var found = new HashSet<string>();
        foreach (var file in LogWatcher.EnumerateNotificationLogs(logsDir))
        {
            string text;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                text = reader.ReadToEnd();
            }
            catch
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match m in LogWatcher.QuestMessageRegex().Matches(text))
            {
                if (m.Groups["kind"].Value == "successMessageText" && known.Contains(m.Groups["id"].Value))
                    found.Add(m.Groups["id"].Value);
            }
        }

        var added = found.Count(id => Progress.CompletedQuests.Add(id));
        if (added > 0)
            SaveProgress();
        return (found.Count, added);
    }

    private static string? TryDetectGamePath()
    {
        var candidates = new List<string?>();

        // путь установки из реестра (деинсталлятор EFT)
        foreach (var hive in new[]
                 {
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
                     @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
                 })
        {
            try
            {
                candidates.Add(Microsoft.Win32.Registry.GetValue(hive, "InstallLocation", null) as string);
            }
            catch
            {
                // реестр может быть недоступен — просто идём дальше
            }
        }

        candidates.AddRange(new[]
        {
            @"C:\Battlestate Games\EFT",
            @"C:\Battlestate Games\Escape from Tarkov",
            @"C:\Games\EFT",
            @"D:\games\Tarkov",
            @"D:\Battlestate Games\EFT",
            @"D:\Games\EFT",
        });

        return candidates.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p) && Directory.Exists(Path.Combine(p!, "Logs")));
    }
}
