using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>Загрузка базы предметов/квестов/обменов/убежища с api.tarkov.dev (GraphQL, без ключа).</summary>
public static class TarkovDevClient
{
    private const string Endpoint = "https://api.tarkov.dev/graphql";

    private const string Query = """
    {
      items(lang: ru) {
        id name shortName basePrice avg24hPrice lastLowPrice
        sellFor { priceRUB vendor { normalizedName name } }
      }
      itemsEn: items(lang: en) { id name shortName }
      tasks(lang: ru) {
        id name kappaRequired minPlayerLevel
        trader { name }
        objectives {
          type __typename
          ... on TaskObjectiveItem { count foundInRaid items { id } }
          ... on TaskObjectiveQuestItem { count questItem { id name } }
        }
      }
      barters(lang: ru) {
        id level
        trader { name }
        requiredItems { count item { id } }
        rewardItems { count item { id name } }
      }
      hideoutStations(lang: ru) {
        id name
        levels { level itemRequirements { count item { id } } }
      }
    }
    """;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/0.1");
        return c;
    }

    public static async Task<GameData> FetchAsync(CancellationToken ct = default)
    {
        using var resp = await Http.PostAsJsonAsync(Endpoint, new { query = Query }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("errors", out var errors) &&
            (!doc.RootElement.TryGetProperty("data", out var d) || d.ValueKind == JsonValueKind.Null))
        {
            throw new InvalidOperationException("tarkov.dev вернул ошибку: " + errors.ToString());
        }

        var data = doc.RootElement.GetProperty("data");
        var result = new GameData { FetchedAtUtc = DateTime.UtcNow };

        // --- предметы (ru) + английские имена для OCR английского клиента ---
        var enNames = new Dictionary<string, (string Name, string Short)>();
        if (data.TryGetProperty("itemsEn", out var itemsEn) && itemsEn.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in itemsEn.EnumerateArray())
                enNames[Str(it, "id")] = (Str(it, "name"), Str(it, "shortName"));
        }

        foreach (var it in data.GetProperty("items").EnumerateArray())
        {
            var item = new Item
            {
                Id = Str(it, "id"),
                Name = Str(it, "name"),
                ShortName = Str(it, "shortName"),
                BasePrice = Int(it, "basePrice") ?? 0,
                Avg24hPrice = Int(it, "avg24hPrice"),
                LastLowPrice = Int(it, "lastLowPrice"),
            };
            if (it.TryGetProperty("sellFor", out var sells) && sells.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sells.EnumerateArray())
                {
                    // барахолка в sellFor не считается «торговцем»
                    if (!s.TryGetProperty("vendor", out var vendor)) continue;
                    if (Str(vendor, "normalizedName") == "flea-market") continue;
                    var p = Int(s, "priceRUB");
                    if (p is not > 0 || p <= (item.TraderSellPrice ?? 0)) continue;
                    item.TraderSellPrice = p;
                    item.TraderSellName = Str(vendor, "name");
                }
            }
            if (enNames.TryGetValue(item.Id, out var en))
            {
                item.NameEn = en.Name;
                item.ShortNameEn = en.Short;
            }
            result.Items.Add(item);
        }

        // --- квесты ---
        var questItems = new Dictionary<string, Item>(); // квестовые предметы как псевдо-предметы
        foreach (var t in data.GetProperty("tasks").EnumerateArray())
        {
            var quest = new Quest
            {
                Id = Str(t, "id"),
                Name = Str(t, "name"),
                KappaRequired = t.TryGetProperty("kappaRequired", out var k) && k.ValueKind == JsonValueKind.True,
                MinPlayerLevel = Int(t, "minPlayerLevel") ?? 0,
                TraderName = t.TryGetProperty("trader", out var tr) && tr.ValueKind == JsonValueKind.Object
                    ? Str(tr, "name") : "",
            };

            if (t.TryGetProperty("objectives", out var objs) && objs.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in objs.EnumerateArray())
                {
                    var type = Str(o, "type");
                    // findItem дублирует giveItem по счёту, учитываем только сдачу/закладку
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
                            objective.ItemIds.Add(Str(v, "id"));
                    }
                    else if (o.TryGetProperty("questItem", out var qi) && qi.ValueKind == JsonValueKind.Object)
                    {
                        var qid = Str(qi, "id");
                        objective.ItemIds.Add(qid);
                        if (!questItems.ContainsKey(qid))
                        {
                            questItems[qid] = new Item
                            {
                                Id = qid,
                                Name = Str(qi, "name"),
                                ShortName = Str(qi, "name"),
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

        // --- обмены ---
        if (data.TryGetProperty("barters", out var barters) && barters.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in barters.EnumerateArray())
            {
                var barter = new Barter
                {
                    Id = Str(b, "id"),
                    Level = Int(b, "level") ?? 1,
                    TraderName = b.TryGetProperty("trader", out var btr) && btr.ValueKind == JsonValueKind.Object
                        ? Str(btr, "name") : "",
                };
                if (b.TryGetProperty("requiredItems", out var req) && req.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in req.EnumerateArray())
                    {
                        if (r.TryGetProperty("item", out var ri) && ri.ValueKind == JsonValueKind.Object)
                            barter.Required.Add(new TradeRequirement { ItemId = Str(ri, "id"), Count = Int(r, "count") ?? 1 });
                    }
                }
                if (b.TryGetProperty("rewardItems", out var rew) && rew.ValueKind == JsonValueKind.Array)
                {
                    var first = rew.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("item", out var rwi))
                        barter.Reward = Str(rwi, "name");
                }
                if (barter.Required.Count > 0)
                    result.Barters.Add(barter);
            }
        }

        // --- убежище ---
        if (data.TryGetProperty("hideoutStations", out var stations) && stations.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in stations.EnumerateArray())
            {
                var station = new HideoutStation { Id = Str(s, "id"), Name = Str(s, "name") };
                if (s.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in levels.EnumerateArray())
                    {
                        var level = new HideoutLevel { Level = Int(l, "level") ?? 0 };
                        if (l.TryGetProperty("itemRequirements", out var reqs) && reqs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var r in reqs.EnumerateArray())
                            {
                                if (r.TryGetProperty("item", out var ri) && ri.ValueKind == JsonValueKind.Object)
                                    level.Requirements.Add(new TradeRequirement { ItemId = Str(ri, "id"), Count = Int(r, "count") ?? 1 });
                            }
                        }
                        station.Levels.Add(level);
                    }
                    station.Levels.Sort((a, b) => a.Level.CompareTo(b.Level));
                }
                result.Stations.Add(station);
            }
        }

        if (result.Items.Count == 0 || result.Quests.Count == 0)
            throw new InvalidOperationException("tarkov.dev вернул пустые данные, попробуйте позже.");

        return result;
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int? Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt32(out var i) ? i : (int)Math.Round(v.GetDouble()))
            : null;
}
