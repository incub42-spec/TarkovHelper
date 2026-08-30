using System.Windows;
using System.Windows.Controls;
using TarkovHelper.Models;

namespace TarkovHelper;

/// <summary>
/// Связывание прочитанных строк с квестами базы. Локаль отстаёт от игры, и
/// часть заданий там называется по-старому — «Ночь распродаж» вместо
/// «Следопыт». Вписывать такие случаи в код бессмысленно: их находит игрок,
/// он же и связывает, а имя остаётся в профиле и переживает обновление базы.
/// </summary>
public partial class LinkQuestsWindow : Window
{
    private readonly string _trader;

    /// <summary>Строка списка квестов: имя и сам квест.</summary>
    private sealed record QuestOption(string Title, Quest Quest);

    private List<QuestOption> _all = new();

    public LinkQuestsWindow(string trader)
    {
        InitializeComponent();
        _trader = trader;
        Title = $"Нераспознанные строки — {trader}";
        Reload();
    }

    private void Reload()
    {
        var rows = App.Services.Progress.UnmatchedRows.TryGetValue(_trader, out var kept)
            ? kept
            : new List<string>();
        ListRows.ItemsSource = rows.ToList();
        if (ListRows.Items.Count > 0) ListRows.SelectedIndex = 0;

        // сначала те, что торговец может выдать: обычно строка именно о них
        _all = (App.Services.Data?.Quests ?? new List<Quest>())
            .Where(q => q.TraderName == _trader)
            .Where(q => !App.Services.Progress.CompletedQuests.Contains(q.Id))
            .OrderByDescending(q => App.Services.Progress.IsAvailable(q))
            .ThenBy(q => App.Services.Progress.NameOf(q), StringComparer.CurrentCulture)
            .Select(q => new QuestOption(
                App.Services.Progress.NameOf(q) +
                (App.Services.Progress.IsAvailable(q) ? "" : "  (закрыт)"), q))
            .ToList();
        ApplyFilter();

        TxtHint.Text = rows.Count == 0
            ? "Нераспознанных строк нет."
            : $"Строк: {rows.Count}. Выполненные квесты в списке справа не показаны.";
        BtnLink.IsEnabled = rows.Count > 0;
        BtnForget.IsEnabled = rows.Count > 0;
    }

    private void ApplyFilter()
    {
        var q = TxtSearch.Text.Trim();
        ListQuests.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(x => x.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    /// <summary>Подсказываем поиском: чаще всего имя отличается лишь частью слов.</summary>
    private void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ListRows.SelectedItem is not string row) return;
        var first = row.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(w => w.Length > 4);
        TxtSearch.Text = first ?? "";
    }

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (ListRows.SelectedItem is not string row)
        {
            MessageBox.Show(this, "Выберите строку слева.", "Tarkov Helper",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ListQuests.SelectedItem is not QuestOption option)
        {
            MessageBox.Show(this, "Выберите квест справа.", "Tarkov Helper",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        App.Services.LinkUnmatched(_trader, row, option.Quest);
        Reload();
    }

    private void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (ListRows.SelectedItem is not string row) return;
        App.Services.ForgetUnmatched(_trader, row);
        Reload();
    }
}
