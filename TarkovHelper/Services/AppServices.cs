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
        _itemsById = null;   // база сменилась — раскладки собрать заново
        _idsByName = null;
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
    public List<Quest> MarkQuestsCompleted(IEnumerable<Quest> quests, bool continueScan = false)
    {
        var added = quests.Where(q => Progress.CompletedQuests.Add(q.Id)).ToList();
        foreach (var q in added)
        {
            Progress.QuestCheckedUtc[q.Id] = DateTime.UtcNow;
            Progress.ActiveQuests.Remove(q.Id); // сдан — значит уже не в работе
        }

        if (added.Count > 0)
        {
            LastQuestScan = continueScan ? LastQuestScan.Concat(added).ToList() : added;
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
    /// <param name="issued">
    /// Торговец квест выдал: статус «активно!». У «новое!» это неизвестно —
    /// такой квест может быть и заблокированным, замок игра пишет только в
    /// карточке справа. Тогда просто снимаем ложную отметку, не объявляя
    /// квест доступным.
    /// </param>
    public List<Quest> UnmarkQuestsCompleted(IEnumerable<Quest> quests, bool issued = true)
    {
        var removed = new List<Quest>();
        var noted = false;
        foreach (var q in quests)
        {
            // «активно!» — это факт, увиденный на экране: он важнее условий
            // выдачи из базы, которые от игры отстают
            if (issued)
            {
                noted |= Progress.ActiveQuests.Add(q.Id);
                noted |= Progress.NotIssued.Remove(q.Id); // раз активен, то выдан
            }

            var changed = Progress.CompletedQuests.Remove(q.Id);
            changed |= Progress.FailedQuests.Remove(q.Id); // перезапустили — снова в работе
            if (!changed) continue;
            Progress.QuestCheckedUtc.Remove(q.Id);
            removed.Add(q);
        }

        if (noted && removed.Count == 0) SaveProgress();
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

    /// <summary>
    /// Отмечает, что торговец пока не выдал эти квесты: список в кадре был
    /// целиком, а их там не оказалось. В отличие от сверки это не объявляет
    /// квест сданным — просто перестаёт считать его делом сегодняшнего дня.
    /// </summary>
    /// <summary>
    /// Что уже попалось при обходе списка каждого торговца. Длинный список
    /// читается с прокруткой, по одному кадру судить нельзя — копим, пока
    /// игрок не скажет, что дошёл до конца.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _walk = new();

    /// <summary>
    /// Складывает нераспознанные строки торговца: их игрок свяжет с квестами
    /// сам, во вкладке «Квесты». Копим без повторов и не больше сорока — это
    /// подсказка, а не журнал.
    /// </summary>
    public void RememberUnmatched(string trader, IEnumerable<string> rows)
    {
        if (!Progress.UnmatchedRows.TryGetValue(trader, out var kept))
            Progress.UnmatchedRows[trader] = kept = new List<string>();

        var added = false;
        foreach (var row in rows)
        {
            var text = row.Trim();
            if (text.Length < 5 || kept.Contains(text)) continue;
            if (kept.Count >= 40) kept.RemoveAt(0);
            kept.Add(text);
            added = true;
        }
        if (added) SaveProgress();
    }

    /// <summary>
    /// Связывает прочитанную строку с квестом. Имя квеста не трогаем: строка
    /// приходит из OCR и может быть с опечатками — она нужна только для того,
    /// чтобы в следующий раз эту же строку узнать.
    /// </summary>
    public void LinkUnmatched(string trader, string row, Quest quest)
    {
        Progress.QuestAliases[row.Trim()] = quest.Id;
        if (Progress.UnmatchedRows.TryGetValue(trader, out var kept))
            kept.RemoveAll(x => x == row);
        SaveProgress();
    }

    /// <summary>Убирает строку из списка нераспознанных, ничего не связывая.</summary>
    public void ForgetUnmatched(string trader, string row)
    {
        if (!Progress.UnmatchedRows.TryGetValue(trader, out var kept)) return;
        if (kept.RemoveAll(x => x == row) > 0) SaveProgress();
    }

    /// <summary>Добавляет кадр в обход; возвращает, сколько узнано всего.</summary>
    /// <summary>Разделы, попавшие в кадры текущего обхода.</summary>
    private readonly Dictionary<string, HashSet<int>> _walkSections = new();

    /// <summary>Торговцы, у которых обход начинался с верха списка.</summary>
    private readonly HashSet<string> _walkFromTop = new();

    /// <summary>Запоминает, какие разделы списка попались в кадр.</summary>
    public void RememberSeenSections(string trader, IEnumerable<int> sections, bool atListTop)
    {
        if (!_walkSections.TryGetValue(trader, out var kept))
            _walkSections[trader] = kept = new HashSet<int>();
        foreach (var s in sections) kept.Add(s);
        if (atListTop) _walkFromTop.Add(trader);
    }

    public int RememberSeenQuests(string trader, IEnumerable<Quest> shown)
    {
        if (!_walk.TryGetValue(trader, out var seen))
        {
            _walk[trader] = seen = new HashSet<string>();

            // Начался новый обход — прежние выводы «торговец не выдал» больше
            // ничего не значат: список мог измениться, а могли и мы ошибиться,
            // не дойдя до конца. Пусть их выставит этот обход.
            if (Data != null)
            {
                var cleared = Data.Quests
                    .Where(q => q.TraderName == trader)
                    .Count(q => Progress.NotIssued.Remove(q.Id));
                if (cleared > 0) SaveProgress();
            }
        }
        foreach (var quest in shown)
            if (quest.TraderName == trader) seen.Add(quest.Id);
        return seen.Count;
    }

    /// <summary>
    /// Обход списка закончен: всё, чего в нём не оказалось, торговец пока не
    /// выдал. Возвращает такие квесты и забывает обход, чтобы следующий
    /// начался с чистого листа.
    /// </summary>
    public List<Quest> FinishTraderWalk(string trader)
    {
        var seen = _walk.TryGetValue(trader, out var s) ? s : new HashSet<string>();
        var sections = _walkSections.TryGetValue(trader, out var sec) ? sec : new HashSet<int>();
        _walk.Remove(trader);
        _walkSections.Remove(trader);
        if (Data == null) return new List<Quest>();

        // Верх списка узнаём по галочкам «Завершенные»/«Заблокированные»: они
        // видны, только когда список прокручен в начало. Не видели их — судим
        // лишь о тех разделах, что действительно попались в кадр.
        var partial = !_walkFromTop.Remove(trader);

        var notIssued = new List<Quest>();
        var changed = false;
        foreach (var quest in Data.Quests)
        {
            if (quest.TraderName != trader) continue;

            if (seen.Contains(quest.Id))
            {
                // увидели в списке — торговец его всё-таки выдал
                changed |= Progress.NotIssued.Remove(quest.Id);
                continue;
            }

            if (Progress.CompletedQuests.Contains(quest.Id)) continue;
            if (!Progress.Fits(quest.Faction)) continue;
            // квест, который сканирование видело активным, торговец точно выдал
            if (Progress.ActiveQuests.Contains(quest.Id)) continue;
            if (!Progress.IsAvailable(quest)) continue;
            // о непросмотренной части списка судить нельзя
            if (partial && !sections.Contains(Progress.SectionOf(quest))) continue;

            notIssued.Add(quest);
            changed |= Progress.NotIssued.Add(quest.Id);
        }

        if (changed) SaveProgress();
        return notIssued;
    }

    /// <summary>Запоминает порядок квестов и их разделы, увиденные в кадре.</summary>
    public void RememberQuestOrder(
        string trader, IEnumerable<Quest> seen, IReadOnlyDictionary<string, int> sections,
        IReadOnlyDictionary<string, string> shortNames, IReadOnlyCollection<string> fullNames)
    {
        var ordered = seen.Where(q => q.TraderName == trader).ToList();
        if (ordered.Count == 0) return;
        Progress.RememberOrder(trader, ordered);
        foreach (var (id, section) in sections)
            Progress.QuestSections[id] = section;
        foreach (var (id, name) in shortNames)
            Progress.SeenNames[id] = name;
        // Игра показала номер части — укороченное имя было ошибкой. Снимаем
        // его со всей цепочки: строку с номером могло притянуть к соседней
        // части, и тогда короткое имя осело именно на ней.
        foreach (var id in fullNames)
        {
            Progress.SeenNames.Remove(id);

            var quest = Data?.Quests.FirstOrDefault(q => q.Id == id);
            if (quest == null) continue;
            var family = QuestMatcher.WithoutPart(quest.Name);
            if (family.Length == 0) continue;

            var seenIds = ordered.Select(q => q.Id).ToHashSet();
            foreach (var other in Data!.Quests)
            {
                if (other.TraderName != quest.TraderName) continue;
                if (QuestMatcher.WithoutPart(other.Name) != family) continue;

                Progress.SeenNames.Remove(other.Id);

                // Часть цепочки, которой в кадре нет, торговец не выдавал:
                // «активен» на ней — след того же промаха, когда строку с
                // номером притянуло к соседней части.
                if (!seenIds.Contains(other.Id)) Progress.ActiveQuests.Remove(other.Id);
            }
        }
        SaveProgress();
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

    // Ленивая раскладка предметов: имя → записи базы и id → предмет. Нужна,
    // чтобы считать схрон по имени, а не по записи: одно и то же имя носят
    // несколько записей (жетоны, патроны), на глаз они неразличимы, и опись
    // ведётся по имени.
    private Dictionary<string, Item>? _itemsById;
    private Dictionary<string, List<string>>? _idsByName;

    /// <summary>Предмет базы по идентификатору.</summary>
    public Item? ItemById(string itemId)
    {
        if (Data == null) return null;
        _itemsById ??= Data.Items
            .GroupBy(i => i.Id)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return _itemsById.GetValueOrDefault(itemId);
    }

    /// <summary>Сколько такого предмета в схроне, считая все одноимённые записи.</summary>
    public int InStashByName(string itemId)
    {
        var item = ItemById(itemId);
        if (Data == null || item == null) return Progress.InStash(itemId);

        _idsByName ??= Data.Items
            .GroupBy(i => i.Name, StringComparer.CurrentCulture)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Id).ToList(),
                StringComparer.CurrentCulture);

        return _idsByName.TryGetValue(item.Name, out var ids)
            ? ids.Sum(Progress.InStash)
            : Progress.InStash(itemId);
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
