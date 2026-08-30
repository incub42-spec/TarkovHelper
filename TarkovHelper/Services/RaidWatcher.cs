using System.IO;
using System.Text.RegularExpressions;

namespace TarkovHelper.Services;

/// <summary>
/// Какая локация загружена последней. Игра пишет это в свой журнал строкой
/// «[Transit] Flag:Common, RaidId:…, Locations:factory4_day», и по ней видно,
/// куда игрок пошёл, — без чтения памяти и вмешательства в процесс.
///
/// Конца рейда в журнале нет, поэтому «текущей» считаем последнюю запись:
/// панель полезна и в меню, когда к вылазке только готовятся.
/// </summary>
public static partial class RaidWatcher
{
    [GeneratedRegex(@"Locations:([A-Za-z0-9_]+)")]
    private static partial Regex LocationRegex();

    /// <summary>
    /// Внутренние имена локаций из журнала. В базе карты названы иначе, а
    /// сопоставлять по звучанию тут нечего — список короткий и конечный.
    /// </summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["factory4_day"] = "Завод",
        ["factory4_night"] = "Завод",
        ["bigmap"] = "Таможня",
        ["woods"] = "Лес",
        ["shoreline"] = "Берег",
        ["rezervbase"] = "Резерв",
        ["interchange"] = "Развязка",
        ["lighthouse"] = "Маяк",
        ["tarkovstreets"] = "Улицы Таркова",
        ["laboratory"] = "Лаборатория",
        ["sandbox"] = "Эпицентр",
        ["sandbox_high"] = "Эпицентр",
        ["labyrinth"] = "Лабиринт",
        ["terminal"] = "Терминал",
        ["icebreaker"] = "Ледокол",
    };

    /// <summary>Последняя загруженная локация и когда это было.</summary>
    public sealed record Raid(string MapName, string RawName, DateTime When);

    /// <summary>
    /// Читает свежий журнал игры. Возвращает null, если папка не задана или
    /// записей о заходе в рейд ещё нет.
    /// </summary>
    public static Raid? Current(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;

        var logsDir = Path.Combine(gamePath, "Logs");
        if (!Directory.Exists(logsDir)) return null;

        try
        {
            // журналов много, нас интересует последний по времени
            var file = new DirectoryInfo(logsDir)
                .EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .SelectMany(d => d.EnumerateFiles("*application*.log"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file == null) return null;

            string? last = null;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
                if (line.Contains("[Transit]", StringComparison.Ordinal)) last = line;

            if (last == null) return null;

            var m = LocationRegex().Match(last);
            if (!m.Success) return null;

            var raw = m.Groups[1].Value;
            var name = Names.TryGetValue(raw, out var known) ? known : raw;
            return new Raid(name, raw, file.LastWriteTimeUtc.ToLocalTime());
        }
        catch
        {
            // журнал может быть занят игрой — панель просто останется пустой
            return null;
        }
    }
}
