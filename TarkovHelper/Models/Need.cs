namespace TarkovHelper.Models;

public enum NeedKind
{
    Quest,
    Hideout,
    Barter,
}

/// <summary>Одна причина, по которой предмет нужен игроку.</summary>
public sealed class Need
{
    public NeedKind Kind { get; set; }
    /// <summary>Подпись источника: название квеста, станции убежища или обмена.</summary>
    public string Source { get; set; } = "";
    public int Count { get; set; }
    public bool FoundInRaid { get; set; }
    /// <summary>
    /// Можно строить прямо сейчас: это следующий уровень станции и все условия
    /// по другим станциям выполнены. У убежища есть последовательность прокачки,
    /// поэтому часть предметов нужна не сегодня, а через несколько построек.
    /// </summary>
    public bool Available { get; set; } = true;
    /// <summary>
    /// Сколько предметов подходят под эту цель. «Скавенжер» просит 15 наушников
    /// любых из 23 моделей — значит пятнадцать штук всего, а не по пятнадцать
    /// каждой. Ключ группы общий у всех вариантов одной цели.
    /// </summary>
    public int Options { get; set; } = 1;
    public string GroupKey { get; set; } = "";
}

/// <summary>Агрегированные потребности по одному предмету.</summary>
public sealed class ItemNeeds
{
    public Item Item { get; set; } = new();
    public List<Need> Needs { get; set; } = new();

    public int QuestCount => Needs.Where(n => n.Kind == NeedKind.Quest).Sum(n => n.Count);
    /// <summary>Сколько нужно для квестов, которые уже можно взять у торговца.</summary>
    public int QuestNowCount =>
        Needs.Where(n => n.Kind == NeedKind.Quest && n.Available).Sum(n => n.Count);
    public int QuestFirCount => Needs.Where(n => n.Kind == NeedKind.Quest && n.FoundInRaid).Sum(n => n.Count);
    public int HideoutCount => Needs.Where(n => n.Kind == NeedKind.Hideout).Sum(n => n.Count);
    /// <summary>Сколько нужно для построек, доступных прямо сейчас.</summary>
    public int HideoutNowCount =>
        Needs.Where(n => n.Kind == NeedKind.Hideout && n.Available).Sum(n => n.Count);
    public int BarterUses => Needs.Count(n => n.Kind == NeedKind.Barter);
    /// <summary>Обмены, до которых уже дорос уровень лояльности у торговца.</summary>
    public int BarterNowUses =>
        Needs.Count(n => n.Kind == NeedKind.Barter && n.Available);

    /// <summary>Цели, где подходит несколько предметов: считать их надо вместе.</summary>
    public IEnumerable<Need> Shared => Needs.Where(n => n.Options > 1 && n.GroupKey.Length > 0);

    /// <summary>Из скольких вариантов выбирается предмет; 1 — только он сам.</summary>
    public int Options => Needs.Count == 0 ? 1 : Needs.Max(n => n.Options);
    public bool NeededForQuestOrHideout => Needs.Any(n => n.Kind != NeedKind.Barter);

    public bool HasQuest => QuestCount > 0;
    public bool HasHideout => HideoutCount > 0;
    public bool HasBarter => BarterUses > 0;
}
