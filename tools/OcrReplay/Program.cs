using System.Text;
using System.Text.Json;
using TarkovHelper.Models;
using TarkovHelper.Services;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("Языки OCR в системе:");
foreach (var l in OcrEngine.AvailableRecognizerLanguages)
    Console.WriteLine($"   {l.LanguageTag} — {l.DisplayName}");

var ru = OcrEngine.TryCreateFromLanguage(new Language("ru"));
var en = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
Console.WriteLine($"ru движок: {(ru != null ? "есть" : "НЕТ")}, en движок: {(en != null ? "есть" : "НЕТ")}\n");

var dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TarkovHelper", "data-pve.json");
var data = JsonSerializer.Deserialize<GameData>(File.ReadAllText(dataPath))!;
var matcher = new ItemMatcher(data);
Console.WriteLine($"База: предметов {data.Items.Count}\n");

async Task<List<(string Text, double X, double Y)>> Read(OcrEngine engine, SoftwareBitmap bmp)
{
    var res = await engine.RecognizeAsync(bmp);
    return res.Lines.Select(l =>
    {
        var r = l.Words.Count > 0 ? l.Words[0].BoundingRect : default;
        return (l.Text, r.X, r.Y);
    }).ToList();
}

foreach (var arg in args)
{
    var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(arg));
    using var stream = await file.OpenAsync(FileAccessMode.Read);
    var decoder = await BitmapDecoder.CreateAsync(stream);
    var bmp = await decoder.GetSoftwareBitmapAsync();

    Console.WriteLine($"===== {Path.GetFileName(arg)}");

    // кадр снят с увеличением 3x, координаты приводим к экранным
    const double scale = 3.0;
    // проход 1 строится как (курсор - 480, курсор - 185), значит курсор всегда здесь
    const double cx = 480, cy = 185;

    var lines = new List<(string Text, double X, double Y)>();
    if (ru != null)
        foreach (var l in await Read(ru, bmp)) lines.Add((l.Text, l.X / scale, l.Y / scale));
    if (en != null)
        foreach (var l in await Read(en, bmp))
        {
            var line = (l.Text, X: l.X / scale, Y: l.Y / scale);
            if (lines.Any(x => x.Text == line.Text && Math.Abs(x.Y - line.Y) < 4)) continue;
            lines.Add(line);
        }
    foreach (var l in lines) Console.WriteLine($"  | {l.Text}");

    double Dist((string Text, double X, double Y) l) =>
        Math.Sqrt((l.X - cx) * (l.X - cx) + (l.Y - cy) * (l.Y - cy));
    double SoftWeight(double d) => d <= 60 ? 1.0 : Math.Max(0.60, 1.0 - (d - 60) / 900.0);

    // как в проходе 1 оверлея: короткие строки — метки ячеек, их не берём вовсе
    var weighted = new List<(string Text, double Weight)>();
    foreach (var l in lines)
    {
        var norm = ItemMatcher.Normalize(l.Text);
        var w = norm.Length <= 8 ? 0 : SoftWeight(Dist(l));
        if (w > 0) weighted.Add((l.Text, w));
    }

    // склейка строк, лежащих друг под другом — точно как в оверлее,
    // включая тройную склейку и вес по ближайшей из строк
    var ordered = lines.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
    bool NextRow((string Text, double X, double Y) top, (string Text, double X, double Y) bottom) =>
        bottom.Y - top.Y is >= 8 and <= 55 && Math.Abs(bottom.X - top.X) <= 140;

    for (var i = 0; i < ordered.Count; i++)
    for (var j = i + 1; j < ordered.Count; j++)
    {
        if (ordered[j].Y - ordered[i].Y > 55) break;
        if (!NextRow(ordered[i], ordered[j])) continue;

        var a = ordered[i];
        var b = ordered[j];
        var pair = a.Text + " " + b.Text;
        weighted.Add((pair, SoftWeight(Math.Min(Dist(a), Dist(b)))));

        var thirds = 0;
        for (var k = j + 1; k < ordered.Count && thirds < 3; k++)
        {
            if (ordered[k].Y - b.Y > 55) break;
            if (!NextRow(b, ordered[k])) continue;
            weighted.Add((pair + " " + ordered[k].Text,
                SoftWeight(Math.Min(Dist(a), Math.Min(Dist(b), Dist(ordered[k]))))));
            thirds++;
        }
    }

    if (Environment.GetEnvironmentVariable("SHOW_LINES") == "1")
        foreach (var w in weighted) Console.WriteLine($"   w={w.Weight:F2} | {w.Text}");

    var diag = new StringBuilder();
    var (ok, rejected) = matcher.MatchDetailed(weighted, diag, 0.62);
    Console.Write(diag.ToString());
    Console.WriteLine(ok != null
        ? $"  => ПРИНЯТ: {ok.Item.Name} ({ok.Score:F2})"
        : $"  => отклонён: {rejected?.Item.Name} ({rejected?.Score:F2})");
    Console.WriteLine();
}
