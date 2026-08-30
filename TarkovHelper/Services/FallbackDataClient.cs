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
public static partial class FallbackDataClient
{
    /// <summary>
    /// Режим игры: наборы квестов и цены у PvE и PvP различаются
    /// (в PvE, например, нет событийных квестов «Neuanfang»).
    /// </summary>
    private static string Mode => App.Services.Progress.PveMode ? "pve" : "regular";

    /// <summary>Режим активного профиля для подписи источника данных.</summary>
    public static string ModeName => App.Services.Progress.ModeName;

    private static string ItemsUrl => $"https://json.tarkov.dev/{Mode}/items";
    private static string TasksUrl => $"https://json.tarkov.dev/{Mode}/tasks";
    private static string BartersUrl => $"https://json.tarkov.dev/{Mode}/barters";
    private static string HideoutUrl => $"https://json.tarkov.dev/{Mode}/hideout";
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

    /// <summary>
    /// Порядок торговцев как в игре — по нему сортируются вкладки и списки.
    /// По алфавиту получается непривычно: глаз ищет Прапора первым.
    /// Смотритель и водитель БТР в игровой ленте не показываются, поэтому в конце.
    /// </summary>
    public static readonly IReadOnlyList<string> TraderOrder = new[]
    {
        "Прапор", "Терапевт", "Скупщик", "Лыжник", "Миротворец", "Механик",
        "Барахольщик", "Егерь", "Реф", "Смотритель", "Водитель БТР",
    };

    /// <summary>Место торговца в игровом порядке; незнакомые — в конец.</summary>
    public static int TraderRank(string trader)
    {
        var i = TraderOrder.ToList().IndexOf(trader);
        return i < 0 ? TraderOrder.Count : i;
    }

    /// <summary>normalizedName станции -> русское название в клиенте игры + алиасы.</summary>
    private static readonly Dictionary<string, (string Name, string[] Aliases)> StationsRu = new()
    {
        ["vents"] = ("Вентиляция", Array.Empty<string>()),
        ["security"] = ("Безопасность", new[] { "Охрана", "Пост охраны" }),
        ["lavatory"] = ("Санузел", new[] { "Уборная", "Туалет" }),
        ["defective-wall"] = ("Стена", new[] { "Разрушенная стена", "Дефектная стена" }),
        ["stash"] = ("Склад", new[] { "Схрон" }),
        ["generator"] = ("Генератор", Array.Empty<string>()),
        ["heating"] = ("Отопление", new[] { "Обогрев" }),
        ["water-collector"] = ("Водосборник", new[] { "Сборщик воды" }),
        ["medstation"] = ("Медблок", new[] { "Медицинский блок" }),
        ["nutrition-unit"] = ("Пищеблок", new[] { "Кухня", "Столовая" }),
        ["rest-space"] = ("Зона отдыха", new[] { "Место отдыха" }),
        ["workbench"] = ("Верстак", Array.Empty<string>()),
        ["intelligence-center"] = ("Разведцентр", new[] { "Разведывательный центр" }),
        ["shooting-range"] = ("Тир", Array.Empty<string>()),
        ["library"] = ("Библиотека", Array.Empty<string>()),
        ["scav-case"] = ("Ящик диких", new[] { "Посылка от диких", "Кейс диких" }),
        ["illumination"] = ("Освещение", Array.Empty<string>()),
        ["hall-of-fame"] = ("Уголок боевой славы", new[] { "Зал славы", "Доска почёта" }),
        ["air-filtering-unit"] = ("Воздушный фильтратор",
            new[] { "Установка фильтрации воздуха", "Фильтрация воздуха" }),
        ["solar-power"] = ("Солнечная батарея", new[] { "Солнечная электростанция" }),
        ["booze-generator"] = ("Самогонный аппарат", Array.Empty<string>()),
        ["bitcoin-farm"] = ("Биткоин ферма",
            new[] { "Ферма биткоинов", "Ферма биткойнов", "Биткойн ферма" }),
        ["christmas-tree"] = ("Новогодняя ёлка", new[] { "Ёлка" }),
        ["weapon-rack"] = ("Оружейный стенд", new[] { "Оружейная стойка", "Стойка для оружия" }),
        ["gear-rack"] = ("Стенд со снаряжением", new[] { "Стойка для снаряжения", "Стойка снаряжения" }),
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
        // локаль SPT — необязательный источник: проект под судебным иском BSG
        // и может исчезнуть, тогда русские названия берутся с вики
        var sptRuTask = TryGetJsonAsync(SptRuUrl, ct);
        var itemsEnTask = TryGetJsonAsync(ItemsEnUrl, ct);
        var questNamesTask = TryGetJsonAsync(QuestNamesUrl, ct);

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
            var hasTypes = it.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array;
            if (hasTypes && types.EnumerateArray().Any(t => t.GetString() == "preset"))
            {
                continue;
            }
            var isGun = hasTypes && types.EnumerateArray().Any(t => t.GetString() == "gun");
            // отдельного типа для жетонов нет, но normalizedName у всех начинается
            // с «dogtag-» («dogtag-bear», «dogtag-usec-1»); серебряный значок сюда
            // не попадает — он «silver-badge» и по уровню не умножается
            var isDogtag = Str(it, "normalizedName").StartsWith("dogtag", StringComparison.OrdinalIgnoreCase);

            var ruName = LocaleStr(sptRu, $"{id} Name");
            var ruShort = LocaleStr(sptRu, $"{id} ShortName");
            string? enName = null, enShort = null;
            if (itemsEn.TryGetProperty(id, out var en) && en.ValueKind == JsonValueKind.Object)
            {
                enName = Str(en, "name");
                enShort = Str(en, "shortName");
            }

            // Для предметов новее локалей имя берём из normalizedName — он есть
            // в самом дампе и всегда актуален: «salewa-first-aid-kit» → «Salewa
            // First Aid Kit». Иначе новый предмет выпал бы из базы совсем.
            if (string.IsNullOrEmpty(enName))
                enName = NameFromSlug(Str(it, "normalizedName"));

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
                IsWeapon = isGun,
                IsDogtag = isDogtag,
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
                WikiTitle = WikiTitle(Str(it, "wikiLink")),
                HasRussianName = ruName != null,
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
                // русское имя из локали SPT → английское из TarkovLab → из ссылки
                // на вики (спасает для квестов, вышедших после обновления обеих баз);
                // ниже английские имена по возможности заменяются русскими с вики
                Name = LocaleStr(sptRu, $"{id} name")
                    ?? (questNames.TryGetValue(id, out var qname) ? qname : null)
                    ?? NameFromWikiLink(Str(t, "wikiLink"))
                    ?? "Новый квест",
                WikiTitle = WikiTitle(Str(t, "wikiLink")),
                HasRussianName = LocaleStr(sptRu, $"{id} name") != null,
                TraderName = Traders.TryGetValue(traderId, out var tn) ? tn.Ru : "Торговец",
                // «USEC»/«BEAR» у квестов, которые выдаются только своей фракции
                Faction = Str(t, "factionName") is "USEC" or "BEAR" ? Str(t, "factionName") : "",
                MinPlayerLevel = Int(t, "minPlayerLevel") ?? 0,
                Restartable = t.TryGetProperty("restartable", out var rst) &&
                              rst.ValueKind == JsonValueKind.True,
                // цепочка квестов: учитываем только условие «сдан», остальные
                // статусы (активен, провален) — это ветвления, а не порядок
                Requires = t.TryGetProperty("taskRequirements", out var treq) &&
                           treq.ValueKind == JsonValueKind.Array
                    ? treq.EnumerateArray()
                        .Where(r => r.TryGetProperty("status", out var st) &&
                                    st.ValueKind == JsonValueKind.Array &&
                                    st.EnumerateArray().Any(x => x.GetString() == "complete"))
                        .Select(r => Str(r, "task"))
                        .Where(id => id.Length > 0)
                        .ToList()
                    : new List<string>(),
                KappaRequired = t.TryGetProperty("kappaRequired", out var k) && k.ValueKind == JsonValueKind.True,
            };

            // Условия по торговцам: без них список расходится с игрой — квест
            // может требовать не только уровень лояльности, но и репутацию,
            // причём иногда отрицательную.
            if (t.TryGetProperty("traderRequirements", out var trreq) &&
                trreq.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in trreq.EnumerateArray())
                {
                    var kind = Str(r, "requirementType");
                    if (kind is not ("level" or "reputation")) continue;
                    var trId = Str(r, "trader");
                    quest.TraderConditions.Add(new TraderCondition
                    {
                        TraderName = Traders.TryGetValue(trId, out var tr) ? tr.Ru : "Торговец",
                        Kind = kind,
                        Compare = Str(r, "compareMethod") is { Length: > 0 } cmp ? cmp : ">=",
                        Value = r.TryGetProperty("value", out var v) && v.TryGetDouble(out var dv) ? dv : 0,
                    });
                }
            }

            // Текст задания и целей лежит в локали по идентификаторам: описание
            // под «<id> description», цель — под собственным id. Без локали
            // остаются пустыми, и вкладка просто не покажет описание.
            quest.Description = LocaleStr(sptRu, $"{id} description") ?? "";
            if (t.TryGetProperty("objectives", out var allObjs) && allObjs.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in allObjs.EnumerateArray())
                {
                    var text = LocaleStr(sptRu, Str(o, "id"));
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    quest.Objectives.Add(new QuestObjective
                    {
                        Text = text!,
                        Optional = o.TryGetProperty("optional", out var opt) &&
                                   opt.ValueKind == JsonValueKind.True,
                        Count = Int(o, "count") ?? 0,
                    });
                }
            }

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
                    // условия по другим станциям: по ним потом достраиваем уровни,
                    // которых игрок не отмечал (тренажёрный зал => «Стена» построена)
                    if (l.TryGetProperty("stationLevelRequirements", out var sreqs) &&
                        sreqs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in sreqs.EnumerateArray())
                        {
                            var stationId = Str(r, "station");
                            if (stationId.Length > 0)
                                level.StationRequirements.Add(new StationRequirement
                                {
                                    StationId = stationId,
                                    Level = Int(r, "level") ?? 1,
                                });
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

        await ApplyWikiRussianNamesAsync(result, status, ct);
        return result;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>
    /// То же, но недоступность источника не считается ошибкой: возвращается
    /// пустой объект. Для необязательных баз, которые могут исчезнуть.
    /// </summary>
    private static async Task<JsonDocument> TryGetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            return await GetJsonAsync(url, ct);
        }
        catch
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? LocaleStr(JsonElement locale, string key) =>
        locale.ValueKind == JsonValueKind.Object &&
        locale.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Проставляет станциям русские названия из таблицы выше. Нужно для кеша,
    /// собранного прошлой версией: если название расходится с игрой («Ферма
    /// биткоинов» вместо «Биткоин ферма»), OCR убежища станцию не узнаёт.
    /// Ключ таблицы восстанавливаем из английского названия — оно из него и сделано.
    /// </summary>
    public static void ApplyStationNames(GameData data)
    {
        foreach (var s in data.Stations)
        {
            var key = StationsRu.Keys.FirstOrDefault(k =>
                string.Equals(Humanize(k), s.NameEn, StringComparison.OrdinalIgnoreCase));
            if (key == null) continue;
            var ru = StationsRu[key];
            s.Name = ru.Name;
            s.Aliases = ru.Aliases.ToList();
        }
    }

    /// <summary>Уточнение в скобках у заголовка вики: «Резерв (квест)».</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\((квест|задание|task|quest)\)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex WikiSuffixRegex();

    /// <summary>Русское имя части цепочки: «Путевка в Санаторий. Часть 4».</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(.*?)[\s.,-]*(?:часть|part)\s*(\d+)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RuPartRegex();

    /// <summary>Имена сравниваем без оглядки на регистр и «ё».</summary>
    private static string FoldName(string s) =>
        s.Trim().ToLowerInvariant().Replace('ё', 'е');

    /// <summary>
    /// Индекс вики по семействам цепочек: «путевка в санаторий» → части,
    /// найденные в русских заголовках, с их английскими ключами.
    /// </summary>
    private static Dictionary<string, List<(int Part, string Key)>> BuildFamilyIndex(
        IReadOnlyDictionary<string, string> map)
    {
        var index = new Dictionary<string, List<(int, string)>>();
        foreach (var (key, ru) in map)
        {
            var m = RuPartRegex().Match(ru);
            if (!m.Success || !int.TryParse(m.Groups[2].Value, out var part)) continue;
            var family = FoldName(m.Groups[1].Value);
            if (family.Length == 0) continue;
            if (!index.TryGetValue(family, out var list))
                index[family] = list = new List<(int, string)>();
            list.Add((part, key));
        }
        return index;
    }

    /// <summary>
    /// Нынешнее имя части цепочки. На вики два набора статей: старые, куда
    /// ведёт ссылка из дампа («I Need More Power» → «Путевка в санаторий.
    /// Часть 4»), и нынешние, названные по-новому («Spa Tour - Part 4» →
    /// «Нужно больше энергии»). Английское имя семейства узнаём по любой
    /// части, которая ещё зовётся по-старому, и берём из него свою.
    /// </summary>
    private static string? FamilyName(
        IReadOnlyDictionary<string, string> map,
        Dictionary<string, List<(int Part, string Key)>> index,
        string ruName)
    {
        var m = RuPartRegex().Match(ruName);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var part)) return null;
        var family = FoldName(m.Groups[1].Value);
        if (!index.TryGetValue(family, out var parts)) return null;

        // Ключ с номером надёжнее: он прямо называет семейство. Заголовок без
        // номера берём только если пронумерованных не нашлось — у первой части
        // цепочки номера в заголовке может не быть вовсе.
        string? englishFamily = null;
        foreach (var (otherPart, key) in parts)
        {
            var suffix = $" - Part {otherPart}";
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            englishFamily = key[..^suffix.Length];
            break;
        }
        englishFamily ??= parts.FirstOrDefault(p => p.Part == 1).Key;
        if (englishFamily == null) return null;

        if (!map.TryGetValue($"{englishFamily} - Part {part}", out var fresh)) return null;

        // статья всё ещё названа по-старому — брать нечего
        var check = RuPartRegex().Match(fresh);
        return check.Success && FoldName(check.Groups[1].Value) == family ? null : fresh;
    }

    private static string Humanize(string normalized) =>
        normalized.Length == 0
            ? "Станция"
            : char.ToUpperInvariant(normalized[0]) + normalized[1..].Replace('-', ' ');

    /// <summary>Заголовок статьи вики из ссылки: «…/wiki/Fresh_Stock» → «Fresh Stock».</summary>
    private static string? WikiTitle(string? wikiLink) => NameFromWikiLink(wikiLink);

    /// <summary>
    /// Названия, которых нет на вики: у страницы нет ссылки на английскую
    /// версию, и связать её с квестом автоматически не выходит. Проверено по
    /// списку заданий в самой игре.
    /// </summary>
    private static readonly Dictionary<string, string> ManualRussianNames = new()
    {
        ["Fall Ailment"] = "Осеннее недомогание",
        // дамп ссылается на «Supplements», а статья называется «Vitamins - Part 2»
        ["Supplements"] = "БАДы",
        // этих статей на вики нет вовсе — имена сверены со списком в игре
        ["Secret Message"] = "Тайное послание",
        ["Demonstration Model"] = "Демонстрационный экземпляр",
        // седьмой части «Путевки в санаторий» на вики нет под новым именем
        ["Chemical Experiments"] = "Химические эксперименты",
    };

    /// <summary>
    /// Русское название с вики. Заголовок статьи и ссылка из дампа сходятся
    /// не всегда: регистр гуляет («The Bunker» против «The bunker - Part 1»),
    /// у первой части цепочки номер бывает только в вики, а часть квестов идёт
    /// с пометкой режима («Arena Business [PVE ZONE]»), которой на вики нет.
    /// </summary>
    private static string? WikiName(IReadOnlyDictionary<string, string> map, string wikiTitle)
    {
        foreach (var key in WikiKeys(wikiTitle))
            if (map.TryGetValue(key, out var ru)) return ru;
        return null;
    }

    private static IEnumerable<string> WikiKeys(string wikiTitle)
    {
        // «X - Part 1» важнее «X»: у первой части цепочки на вики бывают обе
        // страницы, и статья без номера хранит прежнее название. У «Friend
        // From the West» это «Друг с запада. Часть 1», а у «Friend From the
        // West - Part 1» — «Друг с Запада», как сейчас в игре.
        yield return wikiTitle + " - Part 1";
        yield return wikiTitle;

        var cut = wikiTitle.IndexOf(" [", StringComparison.Ordinal);
        if (cut <= 0) yield break;
        yield return wikiTitle[..cut] + " - Part 1";
        yield return wikiTitle[..cut];
    }

    /// <summary>
    /// Для квестов и предметов без русского названия (вышли после обновления
    /// локали SPT) достаёт русские имена с вики. Если вики недоступна —
    /// остаются английские, это не ошибка загрузки.
    /// </summary>
    private static async Task ApplyWikiRussianNamesAsync(
        GameData data, IProgress<string>? status, CancellationToken ct)
    {
        var quests = data.Quests
            .Where(q => !q.HasRussianName && !string.IsNullOrEmpty(q.WikiTitle))
            .ToList();
        var items = data.Items
            .Where(i => !i.HasRussianName && !string.IsNullOrEmpty(i.WikiTitle))
            .ToList();
        if (quests.Count == 0 && items.Count == 0) return;

        status?.Report($"Резервный источник: русские названия с вики " +
                       $"({quests.Count} квестов, {items.Count} предметов)…");

        var map = await WikiTitles.ResolveAsync(
            quests.Select(q => q.WikiTitle!).Concat(items.Select(i => i.WikiTitle!)), ct);

        // Обход с русской стороны нужен всегда, а не только для безымянных:
        // BSG переименовывает квесты, и локаль отстаёт на месяцы. Вики ведут
        // игроки, там названия совпадают с текущим клиентом.
        status?.Report("Резервный источник: русские названия квестов с русской вики…");
        map = await WikiTitles.ResolveQuestsFromRuAsync(map, ct);
        // заголовки статей приходят как есть, регистр у них гуляет
        map = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);

        foreach (var q in quests)
        {
            if (WikiName(map, q.WikiTitle!) is { } ru) q.Name = ru;
            else if (ManualRussianNames.TryGetValue(q.WikiTitle!, out var manual)) q.Name = manual;
        }

        // Свежее название с вики важнее названия из локали: BSG переименовывает
        // квесты, а локаль отстаёт на месяцы. Но берём его только при настоящем
        // переименовании: у вики свой стиль оформления («Слава КПСС - Часть 1»
        // вместо «. Часть 1», «Резерв (квест)»), и по нему список разошёлся бы
        // с игрой в другую сторону.
        var families = BuildFamilyIndex(map);
        foreach (var q in data.Quests)
        {
            // сначала пробуем по семейству цепочки: ссылка из дампа ведёт на
            // статью со старым названием, а нынешнее лежит под «X - Part N»
            // Сначала имена, сверенные со списком в самой игре: вики для этих
            // квестов отдаёт устаревший заголовок, и он бы их перебил.
            var ru = (!string.IsNullOrEmpty(q.WikiTitle) &&
                      ManualRussianNames.TryGetValue(q.WikiTitle!, out var manual)
                         ? manual
                         : null)
                     ?? FamilyName(map, families, q.Name)
                     ?? (string.IsNullOrEmpty(q.WikiTitle) ? null : WikiName(map, q.WikiTitle!));
            if (ru == null) continue;

            var clean = WikiSuffixRegex().Replace(ru, "").Trim();
            if (clean.Length == 0 || clean == q.Name) continue;
            if (ItemMatcher.Similarity(clean, q.Name) >= 0.9) continue; // отличается лишь оформлением

            q.NameAlt = q.Name;
            q.Name = clean;
        }

        foreach (var i in items)
        {
            if (!map.TryGetValue(i.WikiTitle!, out var ru)) continue;
            i.NameEn ??= i.Name;
            i.Name = ru;
        }
    }

    /// <summary>«salewa-first-aid-kit» → «Salewa First Aid Kit».</summary>
    private static string? NameFromSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return name.Length > 1 ? name : null;
    }

    /// <summary>
    /// Достаёт название квеста из ссылки на вики:
    /// «…/wiki/Fresh_Stock» → «Fresh Stock». Null, если ссылки нет.
    /// </summary>
    private static string? NameFromWikiLink(string? wikiLink)
    {
        if (string.IsNullOrWhiteSpace(wikiLink)) return null;
        var slug = wikiLink.TrimEnd('/');
        var i = slug.LastIndexOf('/');
        if (i < 0 || i == slug.Length - 1) return null;

        var name = Uri.UnescapeDataString(slug[(i + 1)..]).Replace('_', ' ').Trim();
        return name.Length > 1 ? name : null;
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int? Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt32(out var i) ? i : (int)Math.Round(v.GetDouble()))
            : null;
}
