using System.IO;
using System.Text;
using System.Text.Json;
using TarkovHelper.Models;
using TarkovHelper.Services;

// Прогон разбора схрона по сохранённым скриншотам окна «Поиск предметов».
// Берём боевой StashScanner — тот же, что стоит за кнопкой в программе:
// проверять сопоставление в игре дорого, а страницы лежат на диске.
//
// Ключ и каталог облака читаются из настроек программы.

Console.OutputEncoding = Encoding.UTF8;

var appData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TarkovHelper");
var folder = args.FirstOrDefault(a => !a.StartsWith("--"))
             ?? Path.Combine(Path.GetTempPath(), "stash");
var probeOnly = args.Contains("--probe");

var files = Directory.GetFiles(folder, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
if (files.Count == 0) { Console.WriteLine($"нет файлов в {folder}"); return; }

if (probeOnly)
{
    // только рамка сетки: она ищется по пикселям и ломается чаще прочего
    var rect = StashScanner.DetectGrid(files);
    Console.WriteLine(rect.Width == 0
        ? "сетка не найдена"
        : $"сетка: {rect.X},{rect.Y} {rect.Width}x{rect.Height}");
    return;
}

var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var data = JsonSerializer.Deserialize<GameData>(
    File.ReadAllText(Path.Combine(appData, "data-pve.json")), opts)!;

using var settingsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(appData, "progress.json")));
var settings = new YandexOcr.AppSettingsView(
    settingsDoc.RootElement.GetProperty("YandexOcrKey").GetString(),
    settingsDoc.RootElement.GetProperty("YandexFolderId").GetString());

var progress = new Progress<string>(Console.WriteLine);
var result = await StashScanner.ScanAsync(files, data, settings, progress);

var report = new StringBuilder();
void Say(string line) { Console.WriteLine(line); report.AppendLine(line); }

Say("");
if (result.Error != null) Say($"ошибка: {result.Error}");
Say($"страниц разобрано: {result.Pages}, пропущено: {result.Skipped.Count}");
Say($"подписей: {result.Labels}, сопоставлено: {result.Labels - result.Unknown.Count}");
Say($"строк в схрон: {result.Counts.Count}, штук всего: {result.Counts.Values.Sum()}");

Say("");
Say("=== не найдено в базе ===");
foreach (var (label, count) in result.Unknown) Say($"  {label} ×{count}");

Say("");
Say("=== имя подошло нескольким записям, взята первая ===");
foreach (var (label, item, others) in result.Guessed) Say($"  {label} → {item.Name} (ещё {others})");

Say("");
Say("=== что записываем (топ 40) ===");
var byId = data.Items.ToDictionary(i => i.Id);
foreach (var (id, n) in result.Counts.OrderByDescending(p => p.Value).Take(40))
    Say($"  {n,8}  {byId[id].Name}");

File.WriteAllText(Path.Combine(Path.GetTempPath(), "stash-report.txt"), report.ToString());
