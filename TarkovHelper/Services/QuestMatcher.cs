using System.Text.RegularExpressions;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Сопоставление прочитанных с экрана строк со списком квестов. Вынесено из
/// <see cref="QuestScanner"/> отдельно от захвата экрана, чтобы разбор можно
/// было прогнать по сохранённому логу, а не проверять вслепую в игре.
/// </summary>
internal static partial class QuestMatcher
{
    [GeneratedRegex("(?i)заверш|выполн|complet")]
    private static partial Regex DoneRegex();

    [GeneratedRegex("(?i)активн|active")]
    private static partial Regex ActiveRegex();

    [GeneratedRegex("(?i)провал|fail")]
    private static partial Regex FailedRegex();

    /// <summary>
    /// «новое!» — квест, который игрок ещё не открывал. Он может быть и
    /// доступным, и заблокированным: замок игра пишет только в карточке
    /// справа, в строке списка его не видно.
    /// </summary>
    [GeneratedRegex(@"(?i)^\W*(новое|new)\W*$")]
    private static partial Regex NewRegex();

    /// <summary>
    /// Хвост «. Часть 2» в конце названия. Цифру не требуем: OCR подменяет её
    /// похожей буквой, а хвост всё равно надо отрезать.
    /// </summary>
    [GeneratedRegex(@"(?i)[\s.,-]*(часть|part)\s*[0-9a-zа-я|]?\s*$")]
    private static partial Regex PartSuffixRegex();

    /// <summary>
    /// Заголовок раздела в сгруппированном виде списка: «УРОВЕНЬ ЛОЯЛЬНОСТИ 2».
    /// Игра раскладывает задания по уровню лояльности торговца, и это тот же
    /// уровень, что в базе, — только в базе он указан у 110 квестов из 514,
    /// а на экране виден у всех.
    /// </summary>
    [GeneratedRegex(@"(?i)(уровень\s+лояльн\w*|loyalty\s+level)\D*([0-9a-zа-я|])")]
    private static partial Regex LoyaltyHeaderRegex();

    /// <summary>
    /// Разделы после уровней лояльности: «Ключевые», «Оперативные»,
    /// «Сюжетные». Заголовок надо узнать хотя бы для того, чтобы он не ушёл
    /// в кандидаты на название квеста.
    /// </summary>
    [GeneratedRegex(@"(?i)^.{0,3}?(ключевые|key\s+tasks?)\s*$")]
    private static partial Regex KeyHeaderRegex();

    [GeneratedRegex(@"(?i)^.{0,3}?(оперативные|operational)\s*$")]
    private static partial Regex OperationalHeaderRegex();

    [GeneratedRegex(@"(?i)^.{0,3}?(сюжетные|story)\s*$")]
    private static partial Regex StoryHeaderRegex();

    /// <summary>Эти разделы в списке ниже любого уровня лояльности.</summary>
    public const int KeySection = 5;
    public const int OperationalSection = 6;
    public const int StorySection = 7;

    /// <summary>Номер части в конце названия: «. Часть 3» → 3, иначе null.</summary>
    [GeneratedRegex(@"(?i)(часть|part)\s*([0-9a-zа-я|])\s*$")]
    private static partial Regex PartNumberRegex();

    /// <summary>Название без номера части: игра показывает его короче базы.</summary>
    private static string WithoutPart(string name) => PartSuffixRegex().Replace(name, "").Trim();

    /// <summary>Ряд списка: все строки, прочитанные на одной высоте.</summary>
    private sealed record Row(double X, double Y, List<string> Texts)
    {
        /// <summary>
        /// Варианты для сравнения со штрафом за отрезанное начало. Перед
        /// названием игра рисует иконку типа задания, и OCR читает её как
        /// «ф», «-л», «...a'». На длинном названии такой мусор почти не
        /// мешает, а на коротком решает: «-л Бункер» против «Бункер» — это
        /// уже 0.75, ниже порога. Отрезаем начало, но чуть штрафуем, чтобы
        /// целое совпадение всегда было важнее обрезанного.
        /// </summary>
        public IEnumerable<(string Text, double Penalty)> Variants
        {
            get
            {
                foreach (var text in Texts)
                {
                    yield return (text, 0);

                    var rest = text;
                    for (var i = 0; i < 2; i++)
                    {
                        var space = rest.IndexOf(' ');
                        if (space <= 0 || space > 4) break;
                        rest = rest[(space + 1)..].Trim();
                        if (rest.Length < 4) break;
                        yield return (rest, 0.01);
                    }
                }
            }
        }
    }

    /// <summary>Ниже этого совпадение считаем случайным.</summary>
    private const double Threshold = 0.78;

    public sealed record Region(int X, int Y, int W, int H);

    /// <summary>
    /// Найденное в списке. Completed — со статусом «завершено», Active — со
    /// статусом «активно!», Unknown — название узнали, а статуса в строке нет.
    /// </summary>
    public sealed record Result(
        List<Quest> Completed, List<Quest> Active, List<Quest> Failed, List<Quest> New,
        List<Quest> Unknown, List<Quest> Ordered, Dictionary<string, int> Sections,
        Dictionary<string, string> ShortNames,
        Region Area, int LinesRead, int StatusMarks, string Log, double LastRowY)
    {
        public int Total =>
            Completed.Count + Active.Count + Failed.Count + New.Count + Unknown.Count;

        /// <summary>Все узнанные квесты кадра.</summary>
        public IEnumerable<Quest> Seen =>
            Completed.Concat(Active).Concat(Failed).Concat(New).Concat(Unknown);

        /// <summary>Чей это список: торговец, которому принадлежит большинство строк.</summary>
        public string? Trader => Seen
            .GroupBy(q => q.TraderName)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        /// <summary>
        /// В кадре список без завершённых — значит игра показывает ровно те
        /// квесты, которые сейчас выданы или доступны. Тогда отсутствие
        /// квеста в кадре само по себе информация.
        /// </summary>
        public bool IsAvailableList => Completed.Count == 0 && Total >= 3;

        /// <summary>
        /// Список уместился в кадр целиком. Если строки доходят до нижнего
        /// края, снизу почти наверняка есть ещё — и тогда отсутствие квеста
        /// в кадре не значит ничего: он может быть просто ниже.
        /// </summary>
        public bool ListFitsFrame => Area.H <= 0 || LastRowY < Area.H * 0.88;

        /// <summary>
        /// Строки со статусом, которые не удалось привязать к квесту базы.
        /// Это либо не распознанное название, либо задание, которого в базе
        /// нет вовсе, — событийные и «Выйти с локации» туда не попадают.
        /// </summary>
        public int Unmatched => Math.Max(0, StatusMarks - Total);
    }

    /// <summary>Прочитанная строка экрана: текст и его место в кадре.</summary>
    public sealed record Line(string Text, double X, double Y);

    public static Result Match(
        IReadOnlyList<Line> lines, GameData data, Progress progress, Region area)
    {
        var doneMarks = new List<(double X, double Y)>();
        var activeMarks = new List<(double X, double Y)>();
        var failedMarks = new List<(double X, double Y)>();
        var newMarks = new List<(double X, double Y)>();
        foreach (var l in lines)
        {
            if (FailedRegex().IsMatch(l.Text)) failedMarks.Add((l.X, l.Y));
            else if (DoneRegex().IsMatch(l.Text)) doneMarks.Add((l.X, l.Y));
            else if (ActiveRegex().IsMatch(l.Text)) activeMarks.Add((l.X, l.Y));
            else if (NewRegex().IsMatch(l.Text)) newMarks.Add((l.X, l.Y));
        }

        // Одну строку списка читают оба движка OCR, каждый по-своему: русский
        // приделывает к названию иконку и путает цифры с буквами («Часть З»),
        // английский коверкает кириллицу, зато цифру берёт верно. Собираем
        // строки одного ряда вместе — вместе они дают больше, чем по одной.
        var rows = new List<Row>();
        var sectionMarks = new List<(double Y, int Section)>();
        foreach (var line in lines.OrderBy(l => l.Y))
        {
            var text = line.Text.Trim();
            if (text.Length < 5) continue;
            if (DoneRegex().IsMatch(text) || ActiveRegex().IsMatch(text) ||
                FailedRegex().IsMatch(text) || NewRegex().IsMatch(text)) continue;

            // заголовок раздела — не квест, но всё, что ниже, относится к нему
            if (KeyHeaderRegex().IsMatch(text))
            {
                sectionMarks.Add((line.Y, KeySection));
                continue;
            }
            if (OperationalHeaderRegex().IsMatch(text))
            {
                sectionMarks.Add((line.Y, OperationalSection));
                continue;
            }
            if (StoryHeaderRegex().IsMatch(text))
            {
                sectionMarks.Add((line.Y, StorySection));
                continue;
            }
            if (LoyaltyHeaderRegex().Match(text) is { Success: true } header)
            {
                var digit = FoldDigit(header.Groups[2].Value[0]);
                if (digit is >= '1' and <= '4')
                {
                    sectionMarks.Add((line.Y, digit - '0'));
                    continue;
                }
            }

            var row = rows.LastOrDefault();
            if (row != null && Math.Abs(row.Y - line.Y) <= 12) row.Texts.Add(text);
            else rows.Add(new Row(line.X, line.Y, new List<string> { text }));
        }

        // Кандидатов собираем по всем рядам сразу и раздаём по убыванию счёта.
        // Иначе неверное совпадение занимает квест, и правильный ряд остаётся
        // ни с чем: «Часть З» забирала «Часть 2», а настоящая «Часть 3» вылетала.
        var pairs = new List<(Row Row, Quest Quest, double Score, int Part)>();
        foreach (var row in rows)
        {
            var rowPart = PartNumber(row.Texts);
            foreach (var q in data.Quests)
            {
                if (!progress.Fits(q.Faction)) continue;

                // Номер части — самое надёжное в строке, когда он прочитан:
                // названия внутри цепочки отличаются только им.
                var questPart = PartNumber(new[] { progress.NameOf(q), q.NameAlt ?? "" });
                if (rowPart != null && questPart != null && rowPart != questPart) continue;

                var score = 0.0;
                foreach (var (text, penalty) in row.Variants)
                {
                    score = Math.Max(score, Score(text, q, progress) - penalty);
                    // Номер части совпал — остаток можно сравнивать без хвоста:
                    // игра показывает название короче базы, а OCR теряет конец.
                    if (rowPart != null && rowPart == questPart)
                        score = Math.Max(score,
                            Score(WithoutPart(text), q, progress, stripPart: true) - penalty);
                    // А порой игра показывает название вовсе без номера:
                    // «Бункер» вместо «Бункер. Часть 1». Тогда сравниваем с
                    // названием без хвоста, а какая это часть — решаем ниже.
                    else if (rowPart == null && questPart != null)
                        score = Math.Max(score,
                            Score(text, q, progress, stripPart: true) - 0.02 - penalty);
                }

                if (score >= Threshold) pairs.Add((row, q, score, questPart ?? 0));
            }
        }

        var takenRow = new HashSet<Row>();
        var takenQuest = new HashSet<string>();
        var matched = new Dictionary<Row, (Quest Quest, double Score)>();
        // При равном счёте из частей цепочки берём первую несданную: их
        // проходят по порядку, значит активна именно она.
        foreach (var pair in pairs
                     .OrderByDescending(p => p.Score)
                     .ThenBy(p => progress.CompletedQuests.Contains(p.Quest.Id) ? 1 : 0)
                     .ThenBy(p => p.Part))
        {
            if (takenRow.Contains(pair.Row)) continue;
            if (!takenQuest.Add(pair.Quest.Id)) continue;
            takenRow.Add(pair.Row);
            matched[pair.Row] = (pair.Quest, pair.Score);
        }

        var completed = new List<Quest>();
        var active = new List<Quest>();
        var failed = new List<Quest>();
        var fresh = new List<Quest>();
        var unknown = new List<Quest>();
        // порядок строк сверху вниз: в игре он свой, из данных не выводится
        var ordered = new List<Quest>();
        var sections = new Dictionary<string, int>();
        // имена, которые игра показывает короче, чем они записаны в базе
        var shortNames = new Dictionary<string, string>();
        var debug = new System.Text.StringBuilder();

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];

            // статус игра пишет в том же ряду правее названия
            const double rowTolerance = 22;
            var isDone = doneMarks.Any(m => Math.Abs(m.Y - row.Y) <= rowTolerance && m.X > row.X);
            var isActive = activeMarks.Any(m => Math.Abs(m.Y - row.Y) <= rowTolerance && m.X > row.X);
            var isFailed = failedMarks.Any(m => Math.Abs(m.Y - row.Y) <= rowTolerance && m.X > row.X);
            var isNew = newMarks.Any(m => Math.Abs(m.Y - row.Y) <= rowTolerance && m.X > row.X);
            var status = isFailed ? "провален"
                : isActive ? "активен"
                : isDone ? "завершён"
                : isNew ? "новый" : "без статуса";

            matched.TryGetValue(row, out var hit);
            debug.AppendLine($"  y={row.Y,5:F0} | {string.Join(" / ", row.Texts)}" +
                             $"  => {(hit.Quest == null ? "нет" : hit.Quest.Name)} " +
                             $"({hit.Score:F2}, {status})");

            if (hit.Quest == null) continue;
            ordered.Add(hit.Quest);

            // Игра показывает «Бункер», а в базе он «Бункер. Часть 1»: локаль
            // хранит номер части, которого в клиенте нет. Раз строка уверенно
            // легла на квест и номера в ней не было — запоминаем короткое имя.
            // Берём его из базы, отрезав хвост, а не из OCR: так в названии
            // не появится мусор от распознавания.
            // крайние ряды кадр режет пополам, и от названия может остаться
            // огрызок — по нему сокращать имя нельзя
            var wholeRow = index > 0 && index < rows.Count - 1;
            if (wholeRow && hit.Score >= 0.9 && PartNumber(row.Texts) == null)
            {
                var full = progress.NameOf(hit.Quest);
                if (PartNumber(new[] { full }) != null)
                    shortNames[hit.Quest.Id] = WithoutPart(full);
            }

            // раздел квеста — последний заголовок выше его строки
            var section = sectionMarks.LastOrDefault(m => m.Y < row.Y - 4);
            if (section.Section > 0) sections[hit.Quest.Id] = section.Section;

            if (isFailed) failed.Add(hit.Quest);
            else if (isActive) active.Add(hit.Quest);
            else if (isDone) completed.Add(hit.Quest);
            else if (isNew) fresh.Add(hit.Quest);
            else unknown.Add(hit.Quest);
        }

        return new Result(completed, active, failed, fresh, unknown, ordered, sections, shortNames,
            area, lines.Count,
            doneMarks.Count + activeMarks.Count + failedMarks.Count + newMarks.Count,
            debug.ToString(),
            rows.Count == 0 ? 0 : rows[^1].Y);
    }

    private static int? PartNumber(IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = PartNumberRegex().Match(text.Trim());
            if (!m.Success) continue;
            var digit = FoldDigit(m.Groups[2].Value[0]);
            if (digit is >= '1' and <= '9') return digit - '0';
        }
        return null;
    }

    /// <summary>
    /// OCR подменяет цифры похожими буквами: «Часть 3» читается как «Часть З»,
    /// «Часть 5» — как «Часть Б». В номере части буква может быть только такой
    /// ошибкой, поэтому сводим её обратно к цифре.
    /// </summary>
    private static char FoldDigit(char c) => char.ToLowerInvariant(c) switch
    {
        'з' or 'э' or 'z' => '3',
        'б' or 's' => '5',
        'о' or 'o' => '0',
        'ч' => '4',
        'і' or 'l' or 'i' or '|' => '1',
        'g' => '9',
        var other => other,
    };

    /// <summary>
    /// Похожесть строки на название квеста. Сравниваем и со свежим именем, и с
    /// прежним из локали: у игрока может стоять клиент, где квест ещё не
    /// переименован, а лишний вариант сравнения ничего не портит.
    /// </summary>
    private static double Score(string text, Quest q, Progress progress, bool stripPart = false)
    {
        var name = progress.NameOf(q);
        var alt = q.NameAlt;
        if (stripPart)
        {
            name = WithoutPart(name);
            alt = alt == null ? null : WithoutPart(alt);
        }

        var score = ItemMatcher.Similarity(text, name);
        if (alt != null)
            score = Math.Max(score, ItemMatcher.Similarity(text, alt));
        return score;
    }
}
