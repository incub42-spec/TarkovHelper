using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovHelper.Models;
using TarkovHelper.Services;

// Прогон разбора списка квестов по сохранённому quest-ocr-debug.log.
// Проверять сопоставление в игре дорого: надо зайти к торговцу, нажать
// клавишу и глазами сверить результат. Лог хранит ровно то, что прочитал
// OCR, — значит те же строки можно прогнать через боевой QuestMatcher.

var root = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TarkovHelper");
var logPath = args.Length > 0 ? args[0] : Path.Combine(root, "quest-ocr-debug.log");

Console.OutputEncoding = Encoding.UTF8;

var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var data = JsonSerializer.Deserialize<GameData>(
    File.ReadAllText(Path.Combine(root, "data-pve.json")), opts)!;

using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "progress.json")));
var activeName = settings.RootElement.GetProperty("ActiveProfile").GetString();
var profileJson = settings.RootElement.GetProperty("Profiles").EnumerateArray()
    .First(p => p.GetProperty("Name").GetString() == activeName);
var progress = JsonSerializer.Deserialize<Progress>(profileJson.GetRawText(), opts)!;

Console.WriteLine($"база: {data.Quests.Count} квестов, профиль {progress.Name}, " +
                  $"выполнено {progress.CompletedQuests.Count}");

// два формата лога: старый построчный «  x=  41 y=  249 | текст  => …»
// и нынешний порядовый «  y=  249 | текст / текст  => …»
var lineRegex = new Regex(@"^\s*x=\s*(-?\d+)\s+y=\s*(-?\d+)\s*\|\s*(.*?)\s+=>\s");
var rowRegex = new Regex(@"^\s*y=\s*(-?\d+)\s*\|\s*(.*?)\s+=>\s");
var blocks = new List<List<QuestMatcher.Line>>();
List<QuestMatcher.Line>? current = null;
var headers = new List<string>();

foreach (var raw in File.ReadLines(logPath))
{
    if (raw.StartsWith("====="))
    {
        current = new List<QuestMatcher.Line>();
        blocks.Add(current);
        headers.Add(raw.Trim());
        continue;
    }
    if (current == null) continue;

    var m = lineRegex.Match(raw);
    if (m.Success)
    {
        current.Add(new QuestMatcher.Line(
            m.Groups[3].Value,
            double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        continue;
    }

    // в порядовом формате координаты по X нет, а строки ряда записаны через «/»
    var r = rowRegex.Match(raw);
    if (!r.Success) continue;
    var y = double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
    foreach (var part in r.Groups[2].Value.Split(" / "))
        current.Add(new QuestMatcher.Line(part.Trim(), 0, y));
}

// в новом формате лога координата одна на ряд — такие блоки прогонять нечего
var usable = blocks
    .Select((b, i) => (Block: b, Header: headers[i]))
    .Where(x => x.Block.Count > 0)
    .ToList();

if (usable.Count == 0)
{
    Console.WriteLine("В логе нет блоков со строками старого формата (x=… y=… | текст  => …).");
    return;
}

var take = usable.TakeLast(args.Length > 1 ? int.Parse(args[1]) : 1);
foreach (var (block, header) in take)
{
    Console.WriteLine();
    Console.WriteLine(header);
    var result = QuestMatcher.Match(block, data, progress, new QuestMatcher.Region(0, 0, 0, 0));
    Console.Write(result.Log);
    Console.WriteLine($"  итог: узнано {result.Total} " +
                      $"(завершено {result.Completed.Count}, активных {result.Active.Count}, " +
                      $"без статуса {result.Unknown.Count})");
}
