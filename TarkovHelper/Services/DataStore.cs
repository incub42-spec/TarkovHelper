using System.IO;
using System.Text.Json;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>Файловое хранилище: кеш базы игры и прогресс игрока в %AppData%\TarkovHelper.</summary>
public static class DataStore
{
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TarkovHelper");

    /// <summary>Кеш базы отдельный для PvE и PvP: наборы квестов и цены различаются.</summary>
    public static string DataFileFor(bool pve) =>
        Path.Combine(RootDir, pve ? "data-pve.json" : "data-pvp.json");

    public static string ProgressFile => Path.Combine(RootDir, "progress.json");
    public static string LogWatchDebugFile => Path.Combine(RootDir, "logwatch-debug.log");
    public static string HideoutOcrDebugFile => Path.Combine(RootDir, "hideout-ocr-debug.log");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static GameData? LoadData(bool pve)
    {
        try
        {
            var file = DataFileFor(pve);
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<GameData>(File.ReadAllText(file));
        }
        catch
        {
            return null; // битый кеш — просто перекачаем
        }
    }

    public static void SaveData(GameData data, bool pve)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(DataFileFor(pve), JsonSerializer.Serialize(data));
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(ProgressFile)) return Defaults();

            var json = File.ReadAllText(ProgressFile);
            using var doc = JsonDocument.Parse(json);

            // файл до появления профилей: один плоский прогресс
            if (!doc.RootElement.TryGetProperty("Profiles", out _))
                return MigrateFlat(doc.RootElement);

            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded == null || loaded.Profiles.Count == 0) return Defaults();
            return loaded;
        }
        catch
        {
            // не теряем приложение из-за битого файла
        }
        return Defaults();
    }

    /// <summary>Переносит старый плоский progress.json в профиль текущего режима.</summary>
    private static AppSettings MigrateFlat(JsonElement root)
    {
        var pve = root.TryGetProperty("PveMode", out var m) && m.ValueKind == JsonValueKind.True;
        var profile = new Progress
        {
            Name = pve ? "PvE" : "PvP",
            PveMode = pve,
        };

        if (root.TryGetProperty("CompletedQuests", out var quests) &&
            quests.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in quests.EnumerateArray())
                if (q.GetString() is { } id) profile.CompletedQuests.Add(id);
        }
        if (root.TryGetProperty("HideoutLevels", out var levels) &&
            levels.ValueKind == JsonValueKind.Object)
        {
            foreach (var l in levels.EnumerateObject())
                if (l.Value.TryGetInt32(out var lvl)) profile.HideoutLevels[l.Name] = lvl;
        }

        return new AppSettings
        {
            GamePath = Str(root, "GamePath"),
            ShowBarterItems = Bool(root, "ShowBarterItems"),
            ShowScanRegion = Bool(root, "ShowScanRegion"),
            ItemHotkey = UInt(root, "ItemHotkey", 0x78),
            HideoutHotkey = UInt(root, "HideoutHotkey", 0x79),
            ActiveProfile = profile.Name,
            Profiles = new List<Progress> { profile },
        };
    }

    private static AppSettings Defaults()
    {
        var profile = new Progress { Name = "PvE", PveMode = true };
        return new AppSettings { ActiveProfile = profile.Name, Profiles = { profile } };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static uint UInt(JsonElement e, string name, uint fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetUInt32(out var n) ? n : fallback;

    public static void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(ProgressFile, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
