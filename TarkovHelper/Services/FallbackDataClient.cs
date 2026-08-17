using System.Net.Http;
using System.Text.Json;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Резервный источник данных на случай недоступности GraphQL tarkov.dev.
/// Собирает ту же GameData из нескольких открытых источников:
///  - json.tarkov.dev — сырые данные (предметы, квесты, обмены, убежище) без имён;
///  - SPT (зеркало на GitHub) — русские/английские имена предметов;
///  - TarkovTracker tarkovdata — английские имена предметов;
///  - TarkovLab TarkovData — актуальные английские названия квестов.
/// </summary>
public static class FallbackDataClient
{
    private const string ItemsUrl = "https://json.tarkov.dev/regular/items";
    private const string TasksUrl = "https://json.tarkov.dev/regular/tasks";
    private const string BartersUrl = "https://json.tarkov.dev/regular/barters";
    private const string HideoutUrl = "https://json.tarkov.dev/regular/hideout";
    private const string SptRuUrl = "https://raw.githubusercontent.com/sp-tarkov/server/master/project/assets/database/locales/global/ru.json";
    private const string ItemsEnUrl = "https://raw.githubusercontent.com/TarkovTracker/tarkovdata/master/items.en.json";
    private const string QuestNamesUrl = "https://raw.githubusercontent.com/TarkovLab/TarkovData/master/data/quests.json";

    /// <summary>id торговца -> (EN, RU) имя.</summary>
    private static readonly Dictionary<string, (string En, string Ru)> Traders = new()
    {
        ["54cb50c76803fa8b248b4571"] = ("Prapor", "Прапор"),
        ["54cb57776803fa99248b456e"] = ("Therapist", "Терапевт"),
        ["579dc571d53a0658a154fbec"] = ("Fence", "Скупщик"),
        ["58330581ace78e27b8b10cee"] = ("Skier", "Лыжник"),
        ["5935c25fb3acc3127c3d8cd9"] = ("Peacekeeper", "Миротворец"),
        ["5a7c2eca46aef81a7ca2145d"] = ("Mechanic", "Механик"),
        ["5ac3b934156ae10c4430e83c"] = ("Ragman", "Барахольщик"),
        ["5c0647fdd443bc2504c2d371"] = ("Jaeger", "Егерь"),
        ["638f541a29ffd1183d187f57"] = ("Lightkeeper", "Смотритель"),
        ["656f0f98d80a697f855d34b1"] = ("BTR Driver", "Водитель БТР"),
        ["6617beeaa9cfa777ca915b7c"] = ("Ref", "Реф"),
    };

    /// <summary>normalizedName станции -> русское название в клиенте игры + алиасы.</summary>
    private static readonly Dictionary<string, (string Name, string[] Aliases)> StationsRu = new()
    {
        ["vents"] = ("Вентиляция", Array.Empty<string>()),
        ["security"] = ("Охрана", new[] { "Пост охраны" }),
        ["lavatory"] = ("Санузел", Array.Empty<string>()),
        ["stash"] = ("Склад", new[] { "Схрон" }),
        ["generator"] = ("Генератор", Array.Empty<string>()),
        ["heating"] = ("Отопление", new[] { "Обогрев" }),
        ["water-collector"] = ("Сборщик воды", new[] { "Водосборник" }),
        ["medstation"] = ("Медблок", new[] { "Медицинский блок" }),
        ["nutrition-unit"] = ("Пищеблок", Array.Empty<string>()),
        ["rest-space"] = ("Зона отдыха", new[] { "Место отдыха" }),
        ["workbench"] = ("Верстак", Array.Empty<string>()),
        ["intelligence-center"] = ("Разведцентр", new[] { "Разведывательный центр" }),
        ["shooting-range"] = ("Тир", Array.Empty<string>()),
        ["library"] = ("Библиотека", Array.Empty<string>()),
        ["scav-case"] = ("Ящик диких", new[] { "Посылка от диких", "Кейс диких" }),
        ["illumination"] = ("Освещение", Array.Empty<string>()),
        ["hall-of-fame"] = ("Зал славы", new[] { "Доска почёта" }),
        ["air-filtering-unit"] = ("Установка фильтрации воздуха", new[] { "Фильтрация воздуха" }),
        ["solar-power"] = ("Солнечная электростанция", new[] { "Солнечная батарея" }),
        ["booze-generator"] = ("Самогонный аппарат", Array.Empty<string>()),
        ["bitcoin-farm"] = ("Ферма биткоинов", new[] { "Ферма биткойнов" }),
        ["christmas-tree"] = ("Новогодняя ёлка", new[] { "Ёлка" }),
        ["weapon-rack"] = ("Оружейная стойка", new[] { "Стойка для оружия" }),
        ["gear-rack"] = ("Стойка для снаряжения", new[] { "Стойка снаряжения" }),
        ["cultist-circle"] = ("Круг сектантов", new[] { "Круг культистов" }),
        ["gym"] = ("Тренажёрный зал", new[] { "Спортзал" }),
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/0.1");
        return c;
    }

    public static async Task<GameData> FetchAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        status?.Report("Резервный источник: качаю данные…");

        var itemsTask = GetJsonAsync(ItemsUrl, ct);
        var tasksTask = GetJsonAsync(TasksUrl, ct);
        var bartersTask = GetJsonAsync(BartersUrl, ct);
        var hideoutTask = GetJsonAsync(HideoutUrl, ct);
        var sptRuTask = GetJsonAsync(SptRuUrl, ct);
        var itemsEnTask = GetJsonAsync(ItemsEnUrl, ct);
        var questNamesTask = GetJsonAsync(QuestNamesUrl, ct);

        await Task.WhenAll(itemsTask, tasksTask, bartersTask, hideoutTask,
            sptRuTask, itemsEnTask, questNamesTask);

        status?.Report("Резервный источник: разбираю данные…");

        using var itemsDoc = itemsTask.Result;
        using var tasksDoc = tasksTask.Result;
        using var bartersDoc = bartersTask.Result;
        using var hideoutDoc = hideoutTask.Result;
        using var sptRuDoc = sptRuTask.Result;
        using var itemsEnDoc = itemsEnTask.Result;
        using var questNamesDoc = questNamesTask.Result;

        var sptRu = sptRuDoc.RootElement;
        var itemsEn = itemsEnDoc.RootElement;
        var result = new GameData { FetchedAtUtc = DateTime.UtcNow };

        // --- английские названия квестов от TarkovLab: gameId -> name ---
        var questNames = new Dictionary<string, string>();
        if (questNamesDoc.RootElement.TryGetProperty("quests", out var qn) && qn.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qn.EnumerateArray())
            {
                var gameId = Str(q, "gameId");
                var name = Str(q, "name");
                if (gameId.Length > 0 && name.Length > 0)
                    questNames[gameId] = name;
            }
        }

        // --- предметы ---
        var items = itemsDoc.RootElement.GetProperty("data").GetProperty("items");
        foreach (var prop in items.EnumerateObject())
        {
            var it = prop.Value;
            var id = prop.Name;

            // пресеты оружия замусоривают распознавание, пропускаем
            if (it.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array &&
                types.EnumerateArray().Any(t => t.GetString() == "preset"))
            {
                continue;
            }

            var ruName = LocaleStr(sptRu, $"{id} Name");
            var ruShort = LocaleStr(sptRu, $"{id} ShortName");
            string? enName = null, enShort = null;
            if (itemsEn.TryGetProperty(id, out var en) && en.ValueKind == JsonValueKind.Object)
            {
                enName = Str(en, "name");
                enShort = Str(en, "shortName");
            }

            // предмет без единого имени бесполезен и для списка, и для OCR
            if (ruName == null && string.IsNullOrEmpty(enName)) continue;

            // лучший выкуп у торговцев (Скупщик почти всегда даёт меньше и не выигрывает)
            int? traderPrice = null;
            string? traderName = null;
            if (it.TryGetProperty("sellToTrader", out var sells) && sells.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sells.EnumerateArray())
                {
                    var p = Int(s, "priceRUB");
                    if (p is not > 0 || p <= (traderPrice ?? 0)) continue;
                    traderPrice = p;
                    traderName = Traders.TryGetValue(Str(s, "trader"), out var tr) ? tr.Ru : "Торговец";
                }
            }

            result.Items.Add(new Item
            {
                Id = id,
                Name = ruName ?? enName ?? id,
                ShortName = ruShort ?? enShort ?? "",
                NameEn = enName,
                ShortNameEn = enShort,
                BasePrice = Int(it, "basePrice") ?? 0,
                Avg24hPrice = Int(it, "avg24hPrice"),
                LastLowPrice = Int(it, "lastLowPrice"),
                TraderSellPrice = traderPrice,
                TraderSellName = traderName,
            });
        }

        // --- квесты ---
        var questItems = new Dictionary<string, Item>();
        var tasks = tasksDoc.RootElement.GetProperty("data").GetProperty("tasks");
        foreach (var prop in tasks.EnumerateObject())
        {
            var t = prop.Value;
            var id = prop.Name;
            var traderId = Str(t, "trader");

            var quest = new Quest
            {
                Id = id,
                // русское название из локали SPT; для квестов новее версии SPT — английское из TarkovLab
                Name = LocaleStr(sptRu, $"{id} name")
                    ?? (questNames.TryGetValue(id, out var qname) ? qname : id),
                TraderName = Traders.TryGetValue(traderId, out var tn) ? tn.Ru : "Торговец",
                MinPlayerLevel = Int(t, "minPlayerLevel") ?? 0,
                KappaRequired = t.TryGetProperty("kappaRequired", out var k) && k.ValueKind == JsonValueKind.True,
            };

            if (t.TryGetProperty("objectives", out var objs) && objs.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in objs.EnumerateArray())
                {
                    var type = Str(o, "type");
                    var isGive = type is "giveItem" or "plantItem" or "giveQuestItem" or "plantQuestItem";
                    if (!isGive) continue;

                    var objective = new QuestItemObjective
                    {
                        Type = type,
                        Count = Int(o, "count") ?? 1,
                        FoundInRaid = o.TryGetProperty("foundInRaid", out var fir) && fir.ValueKind == JsonValueKind.True,
                    };

                    if (o.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in arr.EnumerateArray())
                        {
                            if (v.ValueKind == JsonValueKind.String)
                                objective.ItemIds.Add(v.GetString()!);
                        }
                    }
                    else if (o.TryGetProperty("questItem", out var qi) && qi.ValueKind == JsonValueKind.String)
                    {
                        var qid = qi.GetString()!;
                        objective.ItemIds.Add(qid);
                        if (!questItems.ContainsKey(qid))
                        {
                            var qiName = LocaleStr(sptRu, $"{qid} Name") ?? $"Квестовый предмет ({quest.Name})";
                            questItems[qid] = new Item
                            {
                                Id = qid,
                                Name = qiName,
                                ShortName = qiName,
                                IsQuestItem = true,
                            };
                        }
                    }

                    if (objective.ItemIds.Count > 0)
                        quest.ItemObjectives.Add(objective);
                }
            }

            result.Quests.Add(quest);
        }
        result.Items.AddRange(questItems.Values);

        var itemNames = result.Items.ToDictionary(i => i.Id, i => i.Name);

        // --- обмены ---
        if (bartersDoc.RootElement.TryGetProperty("data", out var barters) && barters.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in barters.EnumerateArray())
            {
                var traderId = Str(b, "trader");
                var barter = new Barter
                {
                    Id = Str(b, "id"),
                    Level = Int(b, "minTraderLevel") ?? 1,
                    TraderName = Traders.TryGetValue(traderId, out var tn) ? tn.Ru : "Торговец",
                };
                if (b.TryGetProperty("requiredItems", out var req) && req.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in req.EnumerateArray())
                    {
                        var itemId = Str(r, "item");
                        if (itemId.Length > 0)
                            barter.Required.Add(new TradeRequirement { ItemId = itemId, Count = Int(r, "count") ?? 1 });
                    }
                }
                if (b.TryGetProperty("offeredItem", out var off) && off.ValueKind == JsonValueKind.Object)
                {
                    var rewardId = Str(off, "item");
                    // сырой ид в интерфейсе бесполезен — пробуем локаль SPT, иначе обобщаем
                    barter.Reward = itemNames.TryGetValue(rewardId, out var rn)
                        ? rn
                        : LocaleStr(sptRu, $"{rewardId} Name") ?? "предмет";
                }
                if (barter.Required.Count > 0)
                    result.Barters.Add(barter);
            }
        }

        // --- убежище ---
        var stations = hideoutDoc.RootElement.GetProperty("data");
        foreach (var prop in stations.EnumerateObject())
        {
            var s = prop.Value;
            var norm = Str(s, "normalizedName");
            var known = StationsRu.TryGetValue(norm, out var ru);
            var station = new HideoutStation
            {
                Id = prop.Name,
                Name = known ? ru.Name : Humanize(norm),
                NameEn = Humanize(norm),
                Aliases = known ? ru.Aliases.ToList() : new List<string>(),
            };
            if (s.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in levels.EnumerateArray())
                {
                    var level = new HideoutLevel { Level = Int(l, "level") ?? 0 };
                    if (l.TryGetProperty("itemRequirements", out var reqs) && reqs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in reqs.EnumerateArray())
                        {
                            var itemId = Str(r, "item");
                            if (itemId.Length > 0)
                                level.Requirements.Add(new TradeRequirement { ItemId = itemId, Count = Int(r, "count") ?? 1 });
                        }
                    }
                    station.Levels.Add(level);
                }
                station.Levels.Sort((a, b) => a.Level.CompareTo(b.Level));
            }
            result.Stations.Add(station);
        }

        if (result.Items.Count == 0 || result.Quests.Count == 0)
            throw new InvalidOperationException("Резервный источник вернул пустые данные.");

        return result;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? LocaleStr(JsonElement locale, string key) =>
        locale.ValueKind == JsonValueKind.Object &&
        locale.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Humanize(string normalized) =>
        normalized.Length == 0
            ? "Станция"
            : char.ToUpperInvariant(normalized[0]) + normalized[1..].Replace('-', ' ');

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int? Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt32(out var i) ? i : (int)Math.Round(v.GetDouble()))
            : null;
}
