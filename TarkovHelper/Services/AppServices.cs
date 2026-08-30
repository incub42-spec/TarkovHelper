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

    /// <summary>
    /// Квесты, отмеченные последним сканированием списка. Хранится, чтобы
    /// массовую отметку можно было откатить одним действием: ошибиться тут
    /// легко — достаточно отсканировать список активных вместо завершённых.
    /// </summary>
    public List<Quest> LastQuestScan { get; private set; } = new();

    /// <summary>Отмечает квесты выполненными; возвращает только реально добавленные.</summary>
    public List<Quest> MarkQuestsCompleted(IEnumerable<Quest> quests)
    {
        var added = quests.Where(q => Progress.CompletedQuests.Add(q.Id)).ToList();
        foreach (var q in added)
            Progress.QuestCheckedUtc[q.Id] = DateTime.UtcNow;

        if (added.Count > 0)
        {
            LastQuestScan = added;
            SaveProgress();
        }
        return added;
    }

    /// <summary>
    /// Квест, который игра показывает активным, точно не сдан. Такие отметки
    /// остаются от неудачного скана — когда список активных прочитали вместо
    /// списка завершённых, — и найти их среди сотен выполненных почти нельзя.
    /// Поэтому снимаем сразу, как только игра показала обратное.
    /// </summary>
    public List<Quest> UnmarkQuestsCompleted(IEnumerable<Quest> quests)
    {
        var removed = new List<Quest>();
        foreach (var q in quests)
        {
            var changed = Progress.CompletedQuests.Remove(q.Id);
            changed |= Progress.FailedQuests.Remove(q.Id); // перезапустили — снова в работе
            if (!changed) continue;
            Progress.QuestCheckedUtc.Remove(q.Id);
            removed.Add(q);
        }

        if (removed.Count > 0)
        {
            var ids = removed.Select(q => q.Id).ToHashSet();
            LastQuestScan = LastQuestScan.Where(q => !ids.Contains(q.Id)).ToList();
            SaveProgress();
        }
        return removed;
    }

    /// <summary>
    /// Достраивает историю по цепочке: раз торговец выдал квест, все квесты,
    /// которые он требует, уже сданы. Список завершённых длинный и читается
    /// с прокруткой, а вот те несколько, что видны сейчас, дают эту часть
    /// истории бесплатно и без ошибок распознавания.
    /// </summary>
    public List<Quest> InferCompletedFromChain(IEnumerable<Quest> visible)
    {
        if (Data == null) return new List<Quest>();
        var byId = Data.Quests.ToDictionary(q => q.Id);
        var added = new List<Quest>();

        void Walk(Quest quest)
        {
            foreach (var id in quest.Requires)
            {
                if (!byId.TryGetValue(id, out var prev)) continue;
                if (!Progress.CompletedQuests.Add(id)) continue;
                Progress.QuestCheckedUtc[id] = DateTime.UtcNow;
                added.Add(prev);
                Walk(prev);
            }
        }

        foreach (var quest in visible)
            Walk(quest);

        if (added.Count > 0)
        {
            LastQuestScan = LastQuestScan.Concat(added).ToList(); // откатывается вместе со сканом
            SaveProgress();
        }
        return added;
    }

    /// <summary>
    /// Отмечает квесты проваленными. У большинства провал окончательный, так
    /// что их лут больше не нужен; перезапускаемые остаются в доступных.
    /// </summary>
    public int MarkQuestsFailed(IEnumerable<Quest> quests)
    {
        var count = quests.Count(q => Progress.FailedQuests.Add(q.Id));
        if (count > 0) SaveProgress();
        return count;
    }

    /// <summary>Откат последнего сканирования списка квестов.</summary>
    public int UndoQuestScan()
    {
        var count = 0;
        foreach (var q in LastQuestScan)
        {
            if (!Progress.CompletedQuests.Remove(q.Id)) continue;
            Progress.QuestCheckedUtc.Remove(q.Id);
            count++;
        }
        LastQuestScan = new List<Quest>();
        if (count > 0) SaveProgress();
        return count;
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
