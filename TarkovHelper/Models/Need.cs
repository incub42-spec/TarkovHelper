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
}

/// <summary>Агрегированные потребности по одному предмету.</summary>
public sealed class ItemNeeds
{
    public Item Item { get; set; } = new();
    public List<Need> Needs { get; set; } = new();

    public int QuestCount => Needs.Where(n => n.Kind == NeedKind.Quest).Sum(n => n.Count);
    public int QuestFirCount => Needs.Where(n => n.Kind == NeedKind.Quest && n.FoundInRaid).Sum(n => n.Count);
    public int HideoutCount => Needs.Where(n => n.Kind == NeedKind.Hideout).Sum(n => n.Count);
    /// <summary>Сколько нужно для построек, доступных прямо сейчас.</summary>
    public int HideoutNowCount =>
        Needs.Where(n => n.Kind == NeedKind.Hideout && n.Available).Sum(n => n.Count);
    public int BarterUses => Needs.Count(n => n.Kind == NeedKind.Barter);
    public bool NeededForQuestOrHideout => Needs.Any(n => n.Kind != NeedKind.Barter);
}
