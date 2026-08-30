using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TarkovHelper;

/// <summary>
/// Выполненные квесты отдельным окном. В основном списке они только мешают —
/// их сотни, — а заглядывать в них нужно редко: убедиться, что ничего не
/// отмечено по ошибке. Отдельная вкладка ради такого не нужна: вкладки для
/// того, с чем работают постоянно.
/// </summary>
public partial class QuestListWindow : Window
{
    private readonly List<MainWindow.QuestRow> _all;
    private List<MainWindow.QuestRow> _shown = new();
    private string? _sortProperty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    /// <summary>Фильтры окна: индекс совпадает с порядком в выпадающем списке.</summary>
    private static readonly List<string> StatusOptions =
        new() { "все", "доступные", "закрытые", "выполненные" };

    internal QuestListWindow(IEnumerable<MainWindow.QuestRow> rows, string title, int status)
    {
        InitializeComponent();
        Title = title;
        _all = rows.ToList();
        CmbStatus.ItemsSource = StatusOptions;
        CmbStatus.SelectedIndex = status;
        Apply();
    }

    private void OnStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) Apply();
    }

    private void Apply()
    {
        IEnumerable<MainWindow.QuestRow> filtered = CmbStatus.SelectedIndex switch
        {
            1 => _all.Where(r => r.Status == "доступен"),
            2 => _all.Where(r => r.Status == "закрыт"),
            3 => _all.Where(r => r.IsCompleted),
            _ => _all,
        };

        var q = TxtSearch.Text.Trim();
        var rows = (string.IsNullOrEmpty(q)
            ? filtered
            : filtered.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Trader.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        List.ItemsSource = rows;
        TxtCount.Text = $"квестов: {rows.Count}";
        _shown = rows;

        if (_sortProperty == null) return;
        var view = CollectionViewSource.GetDefaultView(List.ItemsSource);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(_sortProperty, _sortDirection));
        view.Refresh();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) Apply();
    }

    /// <summary>Снятая галочка возвращает квест в основной список.</summary>
    private void OnCompletedChanged(object sender, RoutedEventArgs e)
    {
        // строку не убираем сразу: иначе случайный клик не отменить
        TxtCount.Text = $"квестов: {_shown.Count}";
    }

    /// <summary>Сортировка по столбцу; дата сортируется по значению, а не по подписи.</summary>
    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;

        var property = column.DisplayMemberBinding is Binding { Path.Path: { Length: > 0 } path }
            ? path
            : (column.Header?.ToString() ?? "").TrimEnd(' ', '▲', '▼') switch
            {
                "Выполнен" => "IsCompleted",
                "Отмечен" => "CheckedSort",
                _ => null,
            };
        if (property == null) return;

        if (_sortProperty == property)
        {
            _sortDirection = _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _sortProperty = property;
            _sortDirection = property == "CheckedSort"
                ? ListSortDirection.Descending // свежие отметки сверху
                : ListSortDirection.Ascending;
        }

        if (List.View is GridView grid)
        {
            foreach (var c in grid.Columns)
            {
                var title = (c.Header?.ToString() ?? "").TrimEnd(' ', '▲', '▼');
                var key = c.DisplayMemberBinding is Binding { Path.Path: { Length: > 0 } p }
                    ? p
                    : title switch { "Выполнен" => "IsCompleted", "Отмечен" => "CheckedSort", _ => null };
                if (key == _sortProperty)
                    title += _sortDirection == ListSortDirection.Ascending ? " ▲" : " ▼";
                c.Header = title;
            }
        }

        Apply();
    }
}
