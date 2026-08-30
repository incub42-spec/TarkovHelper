using System.Globalization;
using System.Windows;

namespace TarkovHelper;

/// <summary>
/// Уровень лояльности и репутация у каждого торговца. Без них список квестов
/// расходится с игрой: часть заданий выдаётся только при своей репутации,
/// причём иногда отрицательной («Возмещение ущерба» у Скупщика).
/// </summary>
public partial class TradersWindow : Window
{
    /// <summary>Строка таблицы; пустое значение означает «не знаю, не проверять».</summary>
    private sealed class Row
    {
        public string Name { get; init; } = "";
        /// <summary>«не открыт» — торговца у персонажа ещё нет; пусто — не знаю.</summary>
        public List<string> LevelOptions { get; } = new() { "", NotUnlocked, "1", "2", "3", "4" };
        public string Level { get; set; } = "";
        public string Reputation { get; set; } = "";
    }

    /// <summary>Подпись варианта «торговца ещё нет».</summary>
    private const string NotUnlocked = "не открыт";

    private readonly List<Row> _rows;

    public TradersWindow(IEnumerable<string> traders)
    {
        InitializeComponent();

        var p = App.Services.Progress;
        // порядок как в игре: список глазами ищется по привычным местам
        _rows = traders.Select(t => new Row
        {
            Name = t,
            Level = p.LockedTraders.Contains(t)
                ? NotUnlocked
                : p.TraderLevels.TryGetValue(t, out var lvl) && lvl > 0 ? lvl.ToString() : "",
            Reputation = p.TraderRep.TryGetValue(t, out var rep)
                ? rep.ToString("0.##", CultureInfo.CurrentCulture)
                : "",
        }).ToList();

        Rows.ItemsSource = _rows;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var p = App.Services.Progress;
        foreach (var row in _rows)
        {
            if (row.Level == NotUnlocked)
            {
                p.LockedTraders.Add(row.Name);
                p.TraderLevels.Remove(row.Name);
            }
            else
            {
                p.LockedTraders.Remove(row.Name);
                if (int.TryParse(row.Level, out var lvl) && lvl is >= 1 and <= 4)
                    p.TraderLevels[row.Name] = lvl;
                else
                    p.TraderLevels.Remove(row.Name);
            }

            // репутация бывает дробной и отрицательной: «1.75», «-3»
            var text = (row.Reputation ?? "").Trim().Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rep))
                p.TraderRep[row.Name] = rep;
            else
                p.TraderRep.Remove(row.Name);
        }

        App.Services.SaveProgress();
        DialogResult = true;
    }
}
