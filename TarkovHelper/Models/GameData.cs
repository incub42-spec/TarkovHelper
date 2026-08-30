namespace TarkovHelper.Models;

/// <summary>Предмет из базы tarkov.dev (или квестовый предмет).</summary>
public sealed class Item
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string? NameEn { get; set; }
    public string? ShortNameEn { get; set; }
    public int BasePrice { get; set; }
    public int? Avg24hPrice { get; set; }
    /// <summary>Текущая (последняя минимальная) цена на барахолке.</summary>
    public int? LastLowPrice { get; set; }
    /// <summary>Лучшая цена выкупа у торговцев.</summary>
    public int? TraderSellPrice { get; set; }
    /// <summary>Заголовок статьи на англовики — по нему ищется русское название.</summary>
    public string? WikiTitle { get; set; }
    /// <summary>Имя уже русское (из локали), подтягивать с вики не нужно.</summary>
    public bool HasRussianName { get; set; }
    /// <summary>Торговец, дающий лучшую цену.</summary>
    public string? TraderSellName { get; set; }
    /// <summary>Квестовый предмет (не существует в обычном инвентаре, только в рейде).</summary>
    public bool IsQuestItem { get; set; }
    /// <summary>
    /// Оружие. Цены в базе — за голый ствол, а игра показывает предложение за
    /// собранный, вместе с обвесом, поэтому суммы расходятся в разы.
    /// </summary>
    public bool IsWeapon { get; set; }
    /// <summary>
    /// Армейский жетон. Терапевт платит за него цену, умноженную на уровень
    /// убитого, а сам уровень игра рисует цифрой в углу ячейки.
    /// </summary>
    public bool IsDogtag { get; set; }
}

/// <summary>Цель квеста "принести/заложить предметы".</summary>
public sealed class QuestItemObjective
{
    /// <summary>Допустимые варианты предмета (обычно один).</summary>
    public List<string> ItemIds { get; set; } = new();
    public int Count { get; set; }
    public bool FoundInRaid { get; set; }
    public string Type { get; set; } = "";
}

public sealed class Quest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TraderName { get; set; } = "";
    public int MinPlayerLevel { get; set; }
    public bool KappaRequired { get; set; }
    public List<QuestItemObjective> ItemObjectives { get; set; } = new();
    /// <summary>Заголовок статьи на англовики — по нему ищется русское название.</summary>
    public string? WikiTitle { get; set; }
    /// <summary>Имя уже русское (из локали), подтягивать с вики не нужно.</summary>
    public bool HasRussianName { get; set; }
    /// <summary>
    /// Фракция, которой выдаётся квест: «USEC», «BEAR» или пусто для общих.
    /// У части квестов в игре две версии с одинаковым названием и торговцем,
    /// но разными идентификаторами — игроку доступна только своя.
    /// </summary>
    public string Faction { get; set; } = "";
    /// <summary>
    /// Квесты, которые надо сдать до этого. Из них складывается цепочка
    /// прокачки: пока предыдущий не сдан, этот у торговца не появится.
    /// </summary>
    public List<string> Requires { get; set; } = new();
}

public sealed class TradeRequirement
{
    public string ItemId { get; set; } = "";
    public int Count { get; set; }
}

public sealed class Barter
{
    public string Id { get; set; } = "";
    public string TraderName { get; set; } = "";
    public int Level { get; set; }
    public List<TradeRequirement> Required { get; set; } = new();
    /// <summary>Название того, что получаем в обмен (для подписи).</summary>
    public string Reward { get; set; } = "";
}

public sealed class HideoutLevel
{
    public int Level { get; set; }
    public List<TradeRequirement> Requirements { get; set; } = new();
    /// <summary>Какие станции и до какого уровня нужны, чтобы построить этот уровень.</summary>
    public List<StationRequirement> StationRequirements { get; set; } = new();
}

/// <summary>Условие постройки: другая станция должна быть построена до Level.</summary>
public sealed class StationRequirement
{
    public string StationId { get; set; } = "";
    public int Level { get; set; }
}

public sealed class HideoutStation
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Английское название (для OCR английского клиента).</summary>
    public string? NameEn { get; set; }
    /// <summary>Альтернативные написания названия (разные версии клиента).</summary>
    public List<string> Aliases { get; set; } = new();
    public List<HideoutLevel> Levels { get; set; } = new();
}

/// <summary>Вся статическая база игры, кешируется на диске.</summary>
public sealed class GameData
{
    public DateTime FetchedAtUtc { get; set; }
    /// <summary>Откуда загружена база: "tarkov.dev" или "резервный (json.tarkov.dev + SPT)".</summary>
    public string Source { get; set; } = "tarkov.dev";
    public List<Item> Items { get; set; } = new();
    public List<Quest> Quests { get; set; } = new();
    public List<Barter> Barters { get; set; } = new();
    public List<HideoutStation> Stations { get; set; } = new();
}
