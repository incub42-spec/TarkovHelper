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

    /// <summary>Название квеста с учётом ручного переименования.</summary>
    public string NameOf(Quest quest) =>
        QuestNames.TryGetValue(quest.Id, out var custom) && custom.Length > 0
            ? custom
            : quest.Name;

    /// <summary>
    /// Квест можно взять прямо сейчас: сам не сдан, а все предыдущие в цепочке
    /// сданы. Пока цепочка не пройдена, торговец его не выдаст — значит и лут
    /// для него собирать не срочно.
    /// </summary>
    public bool IsAvailable(Quest quest) =>
        !CompletedQuests.Contains(quest.Id) &&
        quest.Requires.All(CompletedQuests.Contains) &&
        (PlayerLevel <= 0 || quest.MinPlayerLevel <= PlayerLevel) &&
        !LockedTraders.Contains(quest.TraderName) &&
        quest.TraderConditions.All(Meets);

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
    /// <summary>Показывать в списке предметы, нужные только для обменов.</summary>
    public bool ShowBarterItems { get; set; }
    /// <summary>Подсвечивать область скриншота при сканировании (отладка OCR).</summary>
    public bool ShowScanRegion { get; set; }
    /// <summary>Клавиша сканирования предмета (виртуальный код Windows). F9 по умолчанию.</summary>
    public uint ItemHotkey { get; set; } = 0x78;
    /// <summary>Клавиша сканирования убежища. F10 по умолчанию.</summary>
    public uint HideoutHotkey { get; set; } = 0x79;

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
