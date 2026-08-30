using System.Windows;
using System.Windows.Media;
using TarkovHelper.Models;

namespace TarkovHelper;

/// <summary>
/// Зачем нужен предмет: полный список квестов, построек и обменов. В списке
/// сбора причины сведены в одну строку и обрезаны — у ходовых предметов их
/// десятки, и в столбец они не помещаются.
/// </summary>
public partial class ItemSourcesWindow : Window
{
    private static readonly Brush NowBrush =
        new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));   // зелёный: нужно уже сейчас
    private static readonly Brush LaterBrush =
        new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));   // серый: пригодится позже

    private sealed record SourceRow(
        string Kind, string Source, string Count, string Fir, string When, Brush Brush);

    private sealed record VariantRow(string Name, string Have, string Price, string TraderPrice);

    private static int Flea(Item item) =>
        item.LastLowPrice is > 0 ? item.LastLowPrice.Value
        : item.Avg24hPrice is > 0 ? item.Avg24hPrice.Value : 0;

    public ItemSourcesWindow(string name, ItemNeeds needs, List<ItemNeeds> variants)
    {
        InitializeComponent();
        Title = name;
        TxtTitle.Text = name;

        var progress = App.Services.Progress;
        var have = variants.Sum(v => progress.InStash(v.Item.Id));
        var need = needs.QuestCount + needs.HideoutCount;

        var parts = new List<string>();
        if (need > 0) parts.Add($"нужно {need}, в схроне {have}");
        else if (have > 0) parts.Add($"в схроне {have}");
        if (needs.BarterUses > 0) parts.Add($"обменов: {needs.BarterUses}");
        if (variants.Count > 1) parts.Add($"записей в базе: {variants.Count}");
        TxtSummary.Text = string.Join(";  ", parts);

        ListSources.ItemsSource = needs.Needs
            .OrderBy(n => n.Kind switch
            {
                NeedKind.Quest => 0,
                NeedKind.Hideout => 1,
                _ => 2,
            })
            .ThenByDescending(n => n.Available)
            .ThenBy(n => n.Source, StringComparer.CurrentCulture)
            .Select(n => new SourceRow(
                Kind: n.Kind switch
                {
                    NeedKind.Quest => "Квест",
                    NeedKind.Hideout => "Убежище",
                    _ => "Обмен",
                },
                Source: n.Source,
                // «×15 из 23» — пятнадцать штук любых из двадцати трёх моделей
                Count: "×" + n.Count + (n.Options > 1 ? $" из {n.Options}" : ""),
                Fir: n.FoundInRaid ? "да" : "",
                When: n.Available ? "сейчас" : "позже",
                Brush: n.Available ? NowBrush : LaterBrush))
            .ToList();

        if (variants.Count > 1)
        {
            PanelVariants.Visibility = Visibility.Visible;
            ListVariants.ItemsSource = variants
                .OrderByDescending(v => v.Item.TraderSellPrice ?? 0)
                .Select(v => new VariantRow(
                    // русское имя у вариантов одно на всех — различает английское
                    Name: v.Item.NameEn ?? v.Item.Name,
                    Have: progress.InStash(v.Item.Id) is var h && h > 0 ? h.ToString() : "",
                    Price: Flea(v.Item) is var p && p > 0 ? p.ToString("N0") : "",
                    TraderPrice: v.Item.TraderSellPrice is > 0
                        ? $"{v.Item.TraderSellPrice:N0} ({v.Item.TraderSellName})"
                        : ""))
                .ToList();
        }
    }
}
