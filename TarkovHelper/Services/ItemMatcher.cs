using System.Text;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>Сопоставление распознанного OCR текста с предметом из базы (нечёткое сравнение).</summary>
public sealed class ItemMatcher
{
    private sealed record Entry(string Normalized, Item Item, double Weight);

    private readonly List<Entry> _entries = new();

    public ItemMatcher(GameData data)
    {
        foreach (var item in data.Items)
        {
            AddEntry(item.Name, item, 1.0);
            AddEntry(item.ShortName, item, 0.95);
            AddEntry(item.NameEn, item, 1.0);
            AddEntry(item.ShortNameEn, item, 0.95);
        }
    }

    private void AddEntry(string? name, Item item, double weight)
    {
        var norm = Normalize(name);
        if (norm.Length >= 3)
            _entries.Add(new Entry(norm, item, weight));
    }

    public sealed record MatchResult(Item Item, double Score, string MatchedLine);

    /// <summary>Ищет лучший предмет среди строк, распознанных OCR.</summary>
    public MatchResult? Match(IEnumerable<string> ocrLines) =>
        MatchDetailed(ocrLines.Select(l => (l, 1.0)), null).Accepted;

    /// <summary>
    /// То же, но каждая строка идёт со своим весом (обычно — близость к курсору),
    /// чтобы при нескольких кандидатах в кадре побеждал тот, на который навели.
    /// </summary>
    public MatchResult? Match(IEnumerable<(string Text, double Weight)> weightedLines) =>
        MatchDetailed(weightedLines, null).Accepted;

    /// <summary>
    /// Полный разбор: принятый результат (score >= threshold) либо лучший отклонённый —
    /// чтобы отличать «в кадре нет названия» от «прочитал, но не уверен».
    /// В diag пишутся все кандидаты с оценками. Порог ниже 0.70 имеет смысл только
    /// для зоны тултипа, где метки ячеек уже отфильтрованы.
    /// </summary>
    public (MatchResult? Accepted, MatchResult? BestRejected) MatchDetailed(
        IEnumerable<(string Text, double Weight)> weightedLines, StringBuilder? diag,
        double threshold = 0.70)
    {
        MatchResult? best = null;
        var cands = diag == null ? null : new List<MatchResult>();

        foreach (var (raw, lineWeight) in weightedLines)
        {
            var line = Normalize(raw);
            if (line.Length < 3) continue;

            foreach (var entry in _entries)
            {
                // Грубый фильтр по длине, чтобы не считать Левенштейна зря.
                // Раньше здесь было «+ 4», и это молча выбрасывало правильные
                // варианты: OCR регулярно теряет слово («Штурмовая винтовка
                // Desert Tech MDR» → «Штурмовая винтовка Tech MDR»), и название
                // из базы оказывается заметно длиннее прочитанной строки.
                // Порог согласован с отсечкой Левенштейна ниже: при большей
                // разнице длин дистанция всё равно выйдет за предел.
                if (entry.Normalized.Length > line.Length * 1.5 + 2) continue;

                double score;
                if (line == entry.Normalized)
                {
                    score = 1.0;
                }
                else if (entry.Normalized.Length >= 5 && line.Contains(entry.Normalized))
                {
                    // имя целиком содержится в строке (в тултипе бывают лишние символы)
                    score = 0.90 + 0.05 * entry.Normalized.Length / line.Length;

                    // короткое имя внутри длинной строки — почти наверняка метка
                    // ячейки, слипшаяся с соседями («Рубли Доллары»), а не тултип
                    if (entry.Normalized.Length <= 8)
                        score *= 0.75;
                }
                else
                {
                    var maxLen = Math.Max(line.Length, entry.Normalized.Length);
                    var dist = Levenshtein(line, entry.Normalized, (int)(maxLen * 0.35) + 1);
                    if (dist < 0)
                    {
                        // Посимвольно слишком далеко — но так выглядит и склейка
                        // показаний двух движков: название в ней есть целиком,
                        // просто вперемешку с чужим прочтением тех же слов.
                        score = TokenCoverage(line, entry.Normalized);
                        if (score <= 0) continue;
                    }
                    else
                    {
                        score = 1.0 - (double)dist / maxLen;

                        // Расхождение в цифрах — признак предметов-близнецов («комната
                        // 108»/«118», «магазин на 30»/«на 17»). Чем ближе строки, тем
                        // подозрительнее; при большой дистанции это чаще шум OCR.
                        if (Digits(line) != Digits(entry.Normalized))
                            score *= dist <= 3 ? 0.75 : dist <= 8 ? 0.90 : 1.0;

                        // Названия часто наполовину русские, наполовину латиницей
                        // («Активные беруши CENS "ProFlex DX5"»), и каждый движок
                        // читает верно только свою половину. Покрытие по словам
                        // видит такое название там, где посимвольное сравнение
                        // даёт мало.
                        score = Math.Max(score, TokenCoverage(line, entry.Normalized));
                    }
                }

                // совпадение с коротким именем («Лес», «Ф-1») несёт мало информации:
                // такие метки есть на каждой ячейке инвентаря. Длинное имя
                // (тултип, заголовок осмотра) при равном качестве должно побеждать.
                score *= Math.Min(1.0, 0.75 + 0.025 * entry.Normalized.Length);

                score *= entry.Weight * lineWeight;

                if (cands != null && score >= 0.45)
                    cands.Add(new MatchResult(entry.Item, score, raw.Trim()));
                if (best == null || score > best.Score)
                    best = new MatchResult(entry.Item, score, raw.Trim());
            }
        }

        if (diag != null && cands != null)
        {
            foreach (var c in cands.OrderByDescending(c => c.Score).Take(8))
                diag.AppendLine($"  cand {c.Score:F2} | {c.Item.Name} <= «{c.MatchedLine}»");
        }

        return best != null && best.Score >= threshold ? (best, null) : (null, best);
    }

    /// <summary>
    /// Доля названия, покрытая словами строки (0, если покрытие неполное).
    /// Работает там, где посимвольное сравнение бессильно: строка длиннее
    /// названия в разы, потому что в ней склеены показания двух движков.
    /// </summary>
    private static double TokenCoverage(string line, string entry)
    {
        var want = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // одно-два слова покрытием не проверяем: слишком легко совпасть случайно
        if (want.Length < 3) return 0;

        var have = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matched = 0;
        var matchedLen = 0;
        foreach (var w in want)
        {
            if (!have.Any(h => SameWord(h, w))) continue;
            matched++;
            matchedLen += w.Length;
        }

        // пропущенное слово названия — почти всегда другой предмет из той же
        // серии («PMAG 30» и «PMAG 30 W»), поэтому требуем совпадения всех
        if (matched < want.Length || matchedLen < 10) return 0;
        return 0.95;
    }

    /// <summary>Слова совпадают целиком либо у них общий корень («биткоинов» = «биткоин»).</summary>
    private static bool SameWord(string a, string b)
    {
        if (a == b) return true;
        // короткие слова («dx5», «l6») сравниваем только точно: иначе цепляем чужие
        if (a.Length < 5 || b.Length < 5) return false;

        var common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common]) common++;
        return common >= 5 && common * 10 >= Math.Min(a.Length, b.Length) * 6;
    }

    private static string Digits(string s) =>
        new(s.Where(char.IsDigit).ToArray());

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        var prevSpace = true;
        foreach (var ch in s.ToLowerInvariant())
        {
            var c = FoldHomoglyph(ch == 'ё' ? 'е' : ch);
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Кириллические двойники латиницы сводятся к латинице: OCR читает «MPX» как
    /// «МРХ», «TTI» как «ТП» и т.п. Свёртка применяется к обеим сторонам сравнения,
    /// поэтому русские слова остаются равны сами себе.
    /// </summary>
    private static char FoldHomoglyph(char c) => c switch
    {
        'а' => 'a',
        'в' => 'b',
        'е' => 'e',
        'к' => 'k',
        'м' => 'm',
        'о' => 'o',
        'р' => 'p',
        'с' => 'c',
        'т' => 't',
        'у' => 'y',
        'х' => 'x',
        _ => c,
    };

    /// <summary>Расстояние Левенштейна с отсечкой: возвращает -1, если больше maxDist.</summary>
    private static int Levenshtein(string a, string b, int maxDist)
    {
        if (Math.Abs(a.Length - b.Length) > maxDist) return -1;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, curr[j]);
            }
            if (rowMin > maxDist) return -1;
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length] <= maxDist ? prev[b.Length] : -1;
    }
}
