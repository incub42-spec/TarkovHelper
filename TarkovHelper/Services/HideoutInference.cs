using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Достраивает уровни станций, которые игрок не отмечал, по условиям постройки
/// из базы. Если станция построена, значит всё, что для неё требовалось, тоже
/// построено — иначе игра не дала бы её построить.
///
/// Главный случай — «Стена»: её нет в нижней панели у тех, кто уже прошёл
/// сквозь неё, отсканировать нечего. Но тренажёрный зал, стенд со снаряжением
/// и уголок боевой славы требуют «Стену» 6 уровня, поэтому наличие любого из
/// них означает, что стена достроена полностью. Это важно не ради галочки:
/// у стены свои предметы на уровнях 4–6, и пока её уровень занижен, они
/// висят в списке нужного лута, хотя давно не нужны.
///
/// Два ограничения, без которых вывод врёт:
///
/// 1. Заполняем только станции, которые ещё ни разу не подтверждались. Что
///    игрок отсканировал или выставил руками — не трогаем: сканирование знает
///    факт, а вывод только предполагает.
/// 2. Не делаем выводов от «Склада». Издания игры (EOD, Unheard) дают склад
///    4 уровня сразу, без построек, которые для него по базе требуются, —
///    иначе от одного склада «достраивается» половина убежища.
/// </summary>
internal static class HideoutInference
{
    public sealed record Implied(HideoutStation Station, int From, int To);

    /// <summary>Станции, которые может выдать издание игры, а не постройка.</summary>
    private static bool GrantedByEdition(HideoutStation s) =>
        string.Equals(s.NameEn, "Stash", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Поднимает уровни станций до следующих из условий постройки.
    /// Возвращает то, что изменилось (для отчёта в интерфейсе).
    /// </summary>
    public static List<Implied> Apply(GameData data, Progress progress)
    {
        var changed = new List<Implied>();
        var byId = data.Stations.ToDictionary(s => s.Id);

        // условия ссылаются на другие станции, а те — на третьи, поэтому
        // повторяем, пока уровни не перестанут расти; счётчик от зацикливания
        for (var pass = 0; pass < 10; pass++)
        {
            var grew = false;

            foreach (var station in data.Stations)
            {
                var built = progress.HideoutLevels.TryGetValue(station.Id, out var l) ? l : 0;
                if (built <= 0 || GrantedByEdition(station)) continue;

                foreach (var level in station.Levels.Where(x => x.Level <= built))
                {
                    foreach (var req in level.StationRequirements)
                    {
                        if (!byId.TryGetValue(req.StationId, out var target)) continue;
                        // подтверждённое сканом или руками не переписываем
                        if (progress.HideoutCheckedUtc.ContainsKey(target.Id)) continue;

                        var have = progress.HideoutLevels.TryGetValue(target.Id, out var cur) ? cur : 0;
                        if (have >= req.Level) continue;

                        progress.HideoutLevels[target.Id] = req.Level;
                        // помечаем как выведенное, а не проверенное: это нижняя граница
                        progress.HideoutImpliedUtc[target.Id] = DateTime.UtcNow;
                        changed.Add(new Implied(target, have, req.Level));
                        grew = true;
                    }
                }
            }

            if (!grew) break;
        }

        return changed;
    }
}
