namespace TarkovHelper.Models;

/// <summary>
/// Профиль персонажа: свой прогресс и свой режим игры. У PvE и PvP персонажи
/// разные, поэтому квесты и убежище хранятся отдельно для каждого.
/// </summary>
public sealed class Progress
{
    /// <summary>Имя профиля, видимое в интерфейсе.</summary>
    public string Name { get; set; } = "Основной";
    /// <summary>Режим PvE: у него свой набор квестов и свои цены.</summary>
    public bool PveMode { get; set; }
    /// <summary>
    /// Фракция персонажа: «USEC», «BEAR» или пусто, если не указана. У части
    /// квестов две версии, и своей фракции доступна только одна из них.
    /// </summary>
    public string Faction { get; set; } = "";
    /// <summary>
    /// Уровень персонажа. 0 — не указан, тогда требования по уровню не учитываем.
    /// Нужен, чтобы не считать доступными квесты, до которых игрок не дорос.
    /// </summary>
    public int PlayerLevel { get; set; }
    /// <summary>
    /// Торговцы, которых у персонажа ещё нет (Смотритель, водитель БТР
    /// открываются не сразу). Их квесты не могут быть доступны, и условия
    /// по ним считаются невыполненными.
    /// </summary>
    public HashSet<string> LockedTraders { get; set; } = new();
    /// <summary>Уровень лояльности торговца (1–4). Чего нет — не проверяем.</summary>
    public Dictionary<string, int> TraderLevels { get; set; } = new();
    /// <summary>Репутация у торговца; бывает отрицательной (Скупщик).</summary>
    public Dictionary<string, double> TraderRep { get; set; } = new();
    public HashSet<string> CompletedQuests { get; set; } = new();
    /// <summary>
    /// Проваленные квесты. Не сданы и не в работе: у большинства провал
    /// окончательный, поэтому ни в доступных им делать нечего, ни лут для них
    /// собирать не нужно.
    /// </summary>
    public HashSet<string> FailedQuests { get; set; } = new();
    /// <summary>Ид станции убежища -> построенный уровень (0 = не построено).</summary>
    public Dictionary<string, int> HideoutLevels { get; set; } = new();

    /// <summary>Когда уровень станции подтверждён сканом или вручную (UTC).</summary>
    public Dictionary<string, DateTime> HideoutCheckedUtc { get; set; } = new();
    /// <summary>
    /// Уровни, выведенные из условий постройки других станций, а не увиденные.
    /// Это нижняя граница: станция построена «не ниже», но может быть и выше.
    /// </summary>
    public Dictionary<string, DateTime> HideoutImpliedUtc { get; set; } = new();
    /// <summary>Когда квест отмечен выполненным (UTC).</summary>
    public Dictionary<string, DateTime> QuestCheckedUtc { get; set; } = new();

    /// <summary>
    /// Свои названия квестов: ид -> название. Нужны для квестов, которых ещё нет
    /// ни в локалях, ни на русской вики — их название взять неоткуда, кроме как
    /// из самой игры. Хранятся в профиле и переживают обновление базы.
    /// </summary>
    public Dictionary<string, string> QuestNames { get; set; } = new();

    public string ModeName => PveMode ? "PvE" : "PvP";

    /// <summary>
    /// Квесты, которые игра показала активными. Требования в базе отстают от
    /// игры: у «Поставщика» там 36 уровень, а Прапор выдал его на 35-м. Без
    /// этой поправки выданный квест пропадал бы из списка, хотя игрок его уже
    /// взял. Видел своими глазами — важнее любых условий.
    /// </summary>
    public HashSet<string> ActiveQuests { get; set; } = new();

    /// <summary>
    /// Квесты, которых торговец не показал в списке, хотя по данным базы уже
    /// мог бы выдать. С патча 1.1.0 задания приходят пачками по два-четыре, и
    /// невыданные не видны вовсе — значит собирать для них лут не срочно.
    /// Это наблюдение, а не отметка «сдан»: как только квест появится в кадре,
    /// признак снимется сам.
    /// </summary>
    public HashSet<string> NotIssued { get; set; } = new();

    /// <summary>
    /// Строки списка, которые сканирование не смогло привязать к квесту.
    /// Локаль отстаёт от игры, и часть заданий в базе называется иначе —
    /// такие строки складываем сюда, чтобы игрок связал их сам, а не ждал
    /// правки в коде.
    /// </summary>
    public Dictionary<string, List<string>> UnmatchedRows { get; set; } = new();

    /// <summary>
    /// Строки, связанные с квестами вручную: прочитанный текст → квест.
    /// Нужны, когда распознавание не дотягивает до порога — «БАДы» OCR читает
    /// как «бААЫ», и на четырёх буквах одна ошибка стоит четверти сходства.
    /// Имя квеста при этом не меняется: связь нужна только для узнавания.
    /// </summary>
    public Dictionary<string, string> QuestAliases { get; set; } = new();

    /// <summary>
    /// Что уже лежит в схроне: предмет → сколько штук. Нужно, чтобы список
    /// «что собирать» показывал остаток, а не полную потребность: половина
    /// нужного обычно уже накоплена, и без этого список врёт.
    /// </summary>
    public Dictionary<string, int> Stash { get; set; } = new();

    /// <summary>Сколько этого предмета в схроне.</summary>
    public int InStash(string itemId) =>
        Stash.TryGetValue(itemId, out var n) && n > 0 ? n : 0;

    /// <summary>Записывает количество; ноль убирает запись совсем.</summary>
    public void SetStash(string itemId, int count)
    {
        if (count > 0) Stash[itemId] = count;
        else Stash.Remove(itemId);
    }

    /// <summary>
    /// Порядок квестов у каждого торговца — такой, каким его показывает игра.
    /// Из данных он не выводится: это не алфавит, не уровень и не порядок в
    /// дампе, а хронология выдачи в конкретном профиле. Зато его видно на
    /// экране, поэтому запоминаем при сканировании списка.
    /// </summary>
    public Dictionary<string, List<string>> QuestOrder { get; set; } = new();

    /// <summary>
    /// Раздел, в котором игра показывает квест: 1–4 — уровень лояльности
    /// торговца, 5 — «Ключевые». В базе уровень лояльности указан у 110
    /// квестов из 514, а на экране виден у всех, поэтому берём его оттуда.
    /// </summary>
    public Dictionary<string, int> QuestSections { get; set; } = new();

    /// <summary>Раздел квеста; 0 — не знаем, список ещё не сканировали.</summary>
    public int SectionOf(Quest quest)
    {
        if (QuestSections.TryGetValue(quest.Id, out var s)) return s;

        // пока не сканировали — берём из базы, где условие лояльности указано
        var level = quest.TraderConditions
            .Where(c => c.Kind == "level" && c.TraderName == quest.TraderName)
            .Select(c => (int)c.Value)
            .DefaultIfEmpty(0)
            .Max();
        return level is >= 1 and <= 4 ? level : 0;
    }

    /// <summary>Подпись раздела для группировки списка.</summary>
    public string SectionName(Quest quest) => SectionOf(quest) switch
    {
        0 => "Раздел неизвестен — список не сканировали",
        5 => "Ключевые",
        6 => "Оперативные",
        7 => "Сюжетные",
        var l => $"Уровень лояльности {l}",
    };

    /// <summary>Место квеста в списке торговца; в конец — если не видели.</summary>    /// <summary>Место квеста в списке торговца; в конец — если не видели.</summary>
    public int OrderOf(Quest quest)
    {
        if (!QuestOrder.TryGetValue(quest.TraderName, out var list)) return int.MaxValue;
        var i = list.IndexOf(quest.Id);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>
    /// Вплетает порядок очередного кадра в уже известный. Список длиннее
    /// экрана читается по частям, и кадры надо склеить: уже знакомые строки
    /// служат якорями, новые встают сразу после своего предшественника.
    /// </summary>
    public void RememberOrder(string trader, IEnumerable<Quest> seen)
    {
        if (!QuestOrder.TryGetValue(trader, out var list))
            QuestOrder[trader] = list = new List<string>();

        var at = -1;
        foreach (var quest in seen)
        {
            var i = list.IndexOf(quest.Id);
            if (i >= 0) { at = i; continue; }
            at = at < 0 ? 0 : at + 1;
            list.Insert(at, quest.Id);
        }
    }

    /// <summary>
    /// Названия, увиденные в игре. Локаль порой подробнее клиента: в базе
    /// «Бункер. Часть 1», а игра пишет просто «Бункер». Список сверяют
    /// глазами с экраном, поэтому в приложении должно стоять то же имя.
    /// </summary>
    public Dictionary<string, string> SeenNames { get; set; } = new();

    /// <summary>Название квеста: своё, затем увиденное в игре, затем из базы.</summary>
    public string NameOf(Quest quest) =>
        QuestNames.TryGetValue(quest.Id, out var custom) && custom.Length > 0 ? custom
        : SeenNames.TryGetValue(quest.Id, out var seen) && seen.Length > 0 ? seen
        : quest.Name;

    /// <summary>
    /// Квест можно взять прямо сейчас: сам не сдан, а все предыдущие в цепочке
    /// сданы. Пока цепочка не пройдена, торговец его не выдаст — значит и лут
    /// для него собирать не срочно.
    /// </summary>
    public bool IsAvailable(Quest quest) =>
        !CompletedQuests.Contains(quest.Id) &&
        (ActiveQuests.Contains(quest.Id) ||
         (!NotIssued.Contains(quest.Id) && MeetsRequirements(quest)));

    /// <summary>Условия выдачи по данным базы.</summary>
    private bool MeetsRequirements(Quest quest) =>
        (!FailedQuests.Contains(quest.Id) || quest.Restartable) &&
        (quest.Prerequisites.Count > 0
            ? quest.Prerequisites.All(Satisfied)
            : quest.Requires.All(CompletedQuests.Contains)) &&
        (PlayerLevel <= 0 || quest.MinPlayerLevel <= PlayerLevel) &&
        !LockedTraders.Contains(quest.TraderName) &&
        quest.TraderConditions.All(Meets);

    /// <summary>
    /// Выполнено ли условие по другому заданию. Подходит любой из указанных
    /// статусов: «сдан», «взят» или «провален».
    /// </summary>
    public bool Satisfied(QuestPrerequisite need) =>
        need.Statuses.Any(status => status switch
        {
            "complete" => CompletedQuests.Contains(need.TaskId),
            "active" => ActiveQuests.Contains(need.TaskId),
            "failed" => FailedQuests.Contains(need.TaskId),
            _ => true, // незнакомый статус не должен прятать квест
        });

    /// <summary>
    /// Выполнено ли условие по торговцу. Незаполненные уровень и репутацию
    /// считаем выполненными: лучше показать лишний квест, чем молча спрятать
    /// доступный, пока игрок не ввёл свои цифры.
    /// </summary>
    public bool Meets(TraderCondition c)
    {
        // торговца ещё нет — значит ни уровня, ни репутации у него быть не может
        if (LockedTraders.Contains(c.TraderName)) return false;

        double have;
        if (c.Kind == "reputation")
        {
            if (!TraderRep.TryGetValue(c.TraderName, out var rep)) return true;
            have = rep;
        }
        else
        {
            if (!TraderLevels.TryGetValue(c.TraderName, out var lvl) || lvl <= 0) return true;
            have = lvl;
        }

        return c.Compare switch
        {
            ">=" => have >= c.Value,
            ">" => have > c.Value,
            "<=" => have <= c.Value,
            "<" => have < c.Value,
            "=" or "==" => Math.Abs(have - c.Value) < 0.001,
            _ => true,
        };
    }

    /// <summary>
    /// Доступен ли квест этому персонажу. Квест чужой фракции игроку не выдадут,
    /// поэтому его не показываем и лут для него не собираем. Пока фракция не
    /// выбрана, показываем всё — иначе молча спрячем половину списка.
    /// </summary>
    public bool Fits(string questFaction) =>
        questFaction.Length == 0 || Faction.Length == 0 ||
        string.Equals(questFaction, Faction, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Общие настройки приложения и список профилей.
/// Хранится в %AppData%\TarkovHelper\progress.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Папка с игрой (для чтения логов). Например C:\Battlestate Games\EFT.</summary>
    public string? GamePath { get; set; }
    /// <summary>Показывать предметы, нужные для квестов.</summary>
    public bool ShowQuestItems { get; set; } = true;
    /// <summary>Показывать предметы, нужные для убежища.</summary>
    public bool ShowHideoutItems { get; set; } = true;
    /// <summary>Показывать предметы, нужные для обменов.</summary>
    public bool ShowBarterItems { get; set; }
    /// <summary>Из отмеченных источников оставлять только доступное сейчас.</summary>
    public bool ShowOnlyNowItems { get; set; } = true;
    /// <summary>Прятать строки, где нужное количество уже лежит в схроне.</summary>
    public bool HideEnoughItems { get; set; }
    /// <summary>Подсвечивать область скриншота при сканировании (отладка OCR).</summary>
    public bool ShowScanRegion { get; set; }
    /// <summary>Клавиша сканирования предмета (виртуальный код Windows). F9 по умолчанию.</summary>
    public uint ItemHotkey { get; set; } = 0x78;
    /// <summary>Клавиша сканирования убежища. F10 по умолчанию.</summary>
    public uint HideoutHotkey { get; set; } = 0x79;
    /// <summary>Клавиша сканирования списка квестов у торговца. F11 по умолчанию.</summary>
    public uint QuestHotkey { get; set; } = 0x7A;
    /// <summary>Раскладывать квесты по разделам торговца, как это делает игра.</summary>
    public bool GroupQuests { get; set; } = true;
    /// <summary>Клавиша сводки по текущей локации. F8 по умолчанию.</summary>
    public uint RaidHotkey { get; set; } = 0x77;

    /// <summary>
    /// Облачный OCR Яндекса для сканирования списков. Встроенный движок на
    /// коротких русских названиях ошибается дорого — «БАДы» он читает как
    /// «6AAbl». Кадр области при этом уходит в облако, поэтому по умолчанию
    /// выключено и на подсказку предмета (F9) не влияет: там важнее скорость.
    /// </summary>
    public bool UseYandexOcr { get; set; }
    public string? YandexOcrKey { get; set; }
    public string? YandexFolderId { get; set; }

    /// <summary>Имя активного профиля.</summary>
    public string ActiveProfile { get; set; } = "";
    public List<Progress> Profiles { get; set; } = new();

    /// <summary>Активный профиль; при пустом списке создаётся профиль по умолчанию.</summary>
    public Progress Active
    {
        get
        {
            if (Profiles.Count == 0)
                Profiles.Add(new Progress { Name = "PvE", PveMode = true });

            return Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase))
                ?? Profiles[0];
        }
    }
}
