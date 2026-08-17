using System.IO;
using System.Text.Json;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>Файловое хранилище: кеш базы игры и прогресс игрока в %AppData%\TarkovHelper.</summary>
public static class DataStore
{
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TarkovHelper");

    public static string DataFile => Path.Combine(RootDir, "data.json");
    public static string ProgressFile => Path.Combine(RootDir, "progress.json");
    public static string LogWatchDebugFile => Path.Combine(RootDir, "logwatch-debug.log");
    public static string HideoutOcrDebugFile => Path.Combine(RootDir, "hideout-ocr-debug.log");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static GameData? LoadData()
    {
        try
        {
            if (!File.Exists(DataFile)) return null;
            return JsonSerializer.Deserialize<GameData>(File.ReadAllText(DataFile));
        }
        catch
        {
            return null; // битый кеш — просто перекачаем
        }
    }

    public static void SaveData(GameData data)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(DataFile, JsonSerializer.Serialize(data));
    }

    public static Progress LoadProgress()
    {
        try
        {
            if (File.Exists(ProgressFile))
                return JsonSerializer.Deserialize<Progress>(File.ReadAllText(ProgressFile)) ?? new Progress();
        }
        catch
        {
            // не теряем приложение из-за битого файла
        }
        return new Progress();
    }

    public static void SaveProgress(Progress progress)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(ProgressFile, JsonSerializer.Serialize(progress, JsonOpts));
    }
}
