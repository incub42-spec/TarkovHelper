using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Индекс "предмет -> зачем он нужен" с учётом прогресса игрока:
/// выполненные квесты и построенные уровни убежища исключаются.
/// </summary>
public sealed class NeededItemsIndex
{
    public Dictionary<string, ItemNeeds> ByItemId { get; } = new();

    public ItemNeeds? Get(string itemId) => ByItemId.TryGetValue(itemId, out var n) ? n : null;

    public static NeededItemsIndex Build(GameData data, Progress progress)
    {
        var index = new NeededItemsIndex();
        var items = data.Items.ToDictionary(i => i.Id);

        void Add(string itemId, Need need)
        {
            if (!items.TryGetValue(itemId, out var item)) return;
            if (!index.ByItemId.TryGetValue(itemId, out var needs))
            {
                needs = new ItemNeeds { Item = item };
                index.ByItemId[itemId] = needs;
            }
            needs.Needs.Add(need);
        }

        // Квесты: все невыполненные (в том числе ещё не открытые — лут собираем заранее)
        foreach (var quest in data.Quests)
        {
            if (progress.CompletedQuests.Contains(quest.Id)) continue;
            if (!progress.Fits(quest.Faction)) continue; // квест чужой фракции не выдадут
            foreach (var obj in quest.ItemObjectives)
            {
                foreach (var itemId in obj.ItemIds)
                {
                    Add(itemId, new Need
                    {
                        Kind = NeedKind.Quest,
                        Source = $"{quest.TraderName}: {quest.Name}",
                        Count = obj.Count,
                        FoundInRaid = obj.FoundInRaid,
                    });
                }
            }
        }

        // Убежище: уровни выше построенного. У станций своя последовательность —
        // уровень строится, только когда готов предыдущий и построены станции из
        // его условий. Что до этого ещё не дошло, помечаем как «позже»: предмет
        // нужен, но не в этом рейде.
        int Built(string stationId) =>
            progress.HideoutLevels.TryGetValue(stationId, out var l) ? l : 0;

        foreach (var station in data.Stations)
        {
            var built = Built(station.Id);
            foreach (var level in station.Levels)
            {
                if (level.Level <= built) continue;

                var available = level.Level == built + 1 &&
                                level.StationRequirements.All(r => Built(r.StationId) >= r.Level);

                foreach (var req in level.Requirements)
                {
                    Add(req.ItemId, new Need
                    {
                        Kind = NeedKind.Hideout,
                        Source = $"Убежище: {station.Name} ур. {level.Level}" +
                                 (available ? "" : " (позже)"),
                        Count = req.Count,
                        Available = available,
                    });
                }
            }
        }

        // Обмены: информационно (обмены повторяемы, в "надо собрать" не суммируем)
        foreach (var barter in data.Barters)
        {
            foreach (var req in barter.Required)
            {
                Add(req.ItemId, new Need
                {
                    Kind = NeedKind.Barter,
                    Source = $"{barter.TraderName} ур.{barter.Level} → {barter.Reward}",
                    Count = req.Count,
                });
            }
        }

        return index;
    }
}
