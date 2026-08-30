using System.Windows;
using TarkovHelper.Services;

namespace TarkovHelper;

/// <summary>
/// Заполнение схрона по скриншотам окна «Поиск предметов». Разбор идёт в
/// облаке страница за страницей, поэтому окно показывает ход дела и даёт
/// посмотреть результат до записи: часть подписей игра обрезает, и опознать
/// их удаётся не всегда.
/// </summary>
public partial class StashScanWindow : Window
{
    private readonly IReadOnlyList<string> _files;
    private readonly CancellationTokenSource _cts = new();
    private StashScanner.Result? _result;

    private sealed record FoundRow(string Name, int Count);

    public StashScanWindow(IReadOnlyList<string> files)
    {
        InitializeComponent();
        _files = files;
        TxtStatus.Text = $"Страниц выбрано: {files.Count}. Читаю…";
        TxtHint.Text = "Каждая страница уходит на распознавание в облако — " +
                       "это примерно секунда и десяток копеек.";
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        var data = App.Services.Data;
        if (data == null)
        {
            Finish("База не загружена — сначала обновите данные в настройках.");
            return;
        }

        var settings = new YandexOcr.AppSettingsView(
            App.Services.Settings.YandexOcrKey, App.Services.Settings.YandexFolderId);

        var progress = new Progress<string>(s => TxtStatus.Text = s);
        try
        {
            _result = await StashScanner.ScanAsync(_files, data, settings, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Finish("Отменено.");
            return;
        }
        catch (Exception ex)
        {
            Finish("Ошибка: " + ex.Message);
            return;
        }

        if (_result.Error != null)
        {
            Finish("Не получилось: " + _result.Error);
            return;
        }

        ListFound.ItemsSource = _result.Counts
            .Select(p => new FoundRow(App.Services.ItemById(p.Key)?.Name ?? p.Key, p.Value))
            .OrderByDescending(r => r.Count)
            .ToList();

        ListUnknown.ItemsSource = _result.Unknown
            .Select(u => $"{u.Label} ×{u.Count}")
            .ToList();

        var parts = new List<string>
        {
            $"страниц разобрано: {_result.Pages}",
            $"подписей: {_result.Labels}",
            $"опознано: {_result.Labels - _result.Unknown.Count}",
            $"строк в схрон: {_result.Counts.Count}",
        };
        if (_result.Skipped.Count > 0) parts.Add($"пропущено страниц: {_result.Skipped.Count}");
        if (_result.Guessed.Count > 0)
            parts.Add($"имя подошло нескольким записям: {_result.Guessed.Count}");

        TxtSummary.Text = string.Join(" · ", parts);
        TxtHint.Text = "«Заменить» — если сняты все страницы поиска подряд. " +
                       "«Добавить» — если это часть схрона.";
        Finish("Готово.");

        BtnReplace.IsEnabled = _result.Counts.Count > 0;
        BtnAdd.IsEnabled = _result.Counts.Count > 0;
    }

    private void Finish(string status)
    {
        TxtStatus.Text = status;
        Bar.IsIndeterminate = false;
        Bar.Value = 0;
    }

    private void OnReplaceClick(object sender, RoutedEventArgs e) => Apply(replace: true);

    private void OnAddClick(object sender, RoutedEventArgs e) => Apply(replace: false);

    private void Apply(bool replace)
    {
        if (_result == null) return;
        var progress = App.Services.Progress;

        if (replace) progress.Stash.Clear();
        foreach (var (id, count) in _result.Counts)
            progress.SetStash(id, replace ? count : progress.InStash(id) + count);

        App.Services.SaveProgress();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts.Cancel();
}
