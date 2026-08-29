using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using static TarkovHelper.Interop.NativeMethods;

namespace TarkovHelper.Services;

/// <summary>
/// Пассивный захват области экрана (GDI, без вмешательства в игру)
/// и распознавание текста встроенным Windows OCR.
/// </summary>
public static class ScreenOcr
{
    /// <summary>Во сколько раз увеличиваем картинку перед OCR — мелкий шрифт тултипов.</summary>
    private const int Scale = 2;

    private static OcrEngine? _engine;
    private static OcrEngine? _altEngine;
    private static bool _altTried;

    public static string? EngineDescription { get; private set; }
    /// <summary>Язык второго движка или null, если второго нет.</summary>
    public static string? AltEngineDescription { get; private set; }

    private static OcrEngine? GetEngine()
    {
        if (_engine != null) return _engine;
        _engine = OcrEngine.TryCreateFromLanguage(new Language("ru"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        EngineDescription = _engine?.RecognizerLanguage?.DisplayName;
        return _engine;
    }

    /// <summary>
    /// Второй движок на другом языке. Русский движок латиницу не читает, а
    /// подгоняет под свой словарь: «Magnum Research» превращается в «Мадпит
    /// РеБеагсћ», и предмет не находится, хотя он есть в базе. У половины
    /// оружия в Tarkov название латиницей, поэтому тот же кадр прогоняем
    /// ещё и английским движком, а строки объединяем.
    /// </summary>
    private static OcrEngine? GetAltEngine()
    {
        if (_altTried) return _altEngine;
        _altTried = true;

        var primary = GetEngine()?.RecognizerLanguage?.LanguageTag ?? "";
        var wanted = primary.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            ? new[] { "en-US", "en-GB" }
            : new[] { "ru", "ru-RU" };

        foreach (var tag in wanted)
        {
            _altEngine = OcrEngine.TryCreateFromLanguage(new Language(tag));
            if (_altEngine != null) break;
        }

        AltEngineDescription = _altEngine?.RecognizerLanguage?.DisplayName;
        return _altEngine;
    }

    public static bool IsAvailable => GetEngine() != null;

    /// <summary>Распознанная строка с позицией (в физических пикселях экрана).</summary>
    public sealed record Line(string Text, double X, double Y);

    /// <summary>Распознаёт текст в прямоугольнике экрана (координаты в физических пикселях).</summary>
    public static async Task<string[]> RecognizeAsync(int x, int y, int width, int height) =>
        (await RecognizeLayoutAsync(x, y, width, height)).Select(l => l.Text).ToArray();

    /// <summary>То же, но с координатами строк (для разбора экранов с раскладкой).
    /// savePngPath — сохранить снятый кадр как PNG (отладка: видно, что ушло в OCR).
    /// binarize — светлый текст на тёмном фоне превратить в чёрный на белом:
    /// так читаются мелкие цифры уровня на иконках убежища.</summary>
    public static async Task<List<Line>> RecognizeLayoutAsync(
        int x, int y, int width, int height, int scaleHint = Scale, string? savePngPath = null,
        bool binarize = false, bool bothLanguages = false)
    {
        var engine = GetEngine()
            ?? throw new InvalidOperationException(
                "Windows OCR недоступен: установите языковой пакет (Параметры → Время и язык).");

        // не превышаем максимальный размер картинки для OCR
        var scale = scaleHint;
        var maxDim = (int)OcrEngine.MaxImageDimension;
        while (scale > 1 && Math.Max(width, height) * scale > maxDim)
            scale--;

        var pixels = CaptureBgra(x, y, width, height, scale, out var outW, out var outH);

        if (binarize) Binarize(pixels);

        if (savePngPath != null)
        {
            try
            {
                var src = System.Windows.Media.Imaging.BitmapSource.Create(
                    outW, outH, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                    pixels, outW * 4);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                using var fs = System.IO.File.Create(savePngPath);
                enc.Save(fs);
            }
            catch
            {
                // отладочный снимок не должен мешать распознаванию
            }
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, outW, outH, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());

        var lines = ToLines(await engine.RecognizeAsync(bitmap), scale);

        // тот же кадр вторым движком: латинские названия русский движок коверкает
        if (bothLanguages && GetAltEngine() is { } alt)
        {
            foreach (var line in ToLines(await alt.RecognizeAsync(bitmap), scale))
            {
                // одинаковый текст на том же месте — это одна и та же строка
                if (lines.Any(l => l.Text == line.Text && Math.Abs(l.Y - line.Y) < 4)) continue;
                lines.Add(line);
            }
        }

        return lines.OrderBy(l => l.Y).ToList();
    }

    private static List<Line> ToLines(OcrResult result, int scale) =>
        result.Lines
            .Select(l =>
            {
                var r = l.Words.Count > 0 ? l.Words[0].BoundingRect : default;
                return new Line(l.Text, r.X / scale, r.Y / scale);
            })
            .ToList();

    /// <summary>
    /// Порог по яркости: светлые пиксели (текст интерфейса) становятся чёрными,
    /// тёмный фон — белым. Windows OCR заметно лучше читает мелкие цифры,
    /// когда контраст доведён до предела, а фон однородный.
    /// </summary>
    private static void Binarize(byte[] bgra)
    {
        const int threshold = 140;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var lum = (bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000;
            var v = (byte)(lum > threshold ? 0 : 255);
            bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
            bgra[i + 3] = 255;
        }
    }

    private static byte[] CaptureBgra(int x, int y, int w, int h, int scale, out int outW, out int outH)
    {
        outW = w * scale;
        outH = h * scale;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = outW,
            biHeight = -outH, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var hBitmap = CreateDIBSection(memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("Не удалось создать буфер для захвата экрана.");
        }

        var oldBitmap = SelectObject(memDc, hBitmap);
        try
        {
            SetStretchBltMode(memDc, HALFTONE);
            SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
            StretchBlt(memDc, 0, 0, outW, outH, screenDc, x, y, w, h, SRCCOPY);

            var buffer = new byte[outW * outH * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            // GDI оставляет альфу нулевой — приводим к непрозрачной
            for (var i = 3; i < buffer.Length; i += 4)
                buffer[i] = 255;
            return buffer;
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
