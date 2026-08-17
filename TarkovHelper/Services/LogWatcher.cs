using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TarkovHelper.Services;

/// <summary>
/// Следит за логами игры (папка Logs в каталоге EFT) и автоматически отмечает
/// завершённые квесты по push-уведомлениям (*push-notifications*.log).
///
/// Формат (проверен на клиенте 1.0.x–1.1.x): сообщение чата от торговца с
/// "templateId": "&lt;ид квеста&gt; successMessageText ..." означает сдачу квеста.
/// Строки-кандидаты дублируются в logwatch-debug.log для калибровки на новых патчах.
/// </summary>
public sealed partial class LogWatcher : IDisposable
{
    private readonly string _logsDir;
    private readonly HashSet<string> _questIds;
    private readonly Action<string> _onQuestCompleted;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, long> _positions = new();
    private readonly object _sync = new();
    private bool _disposed;

    public string Status { get; private set; } = "запуск...";

    [GeneratedRegex("\"templateId\":\\s*\"(?<id>[0-9a-f]{24})\\s+(?<kind>successMessageText|failMessageText|startedMessageText)")]
    public static partial Regex QuestMessageRegex();

    public LogWatcher(string gamePath, IEnumerable<string> questIds, Action<string> onQuestCompleted)
    {
        _logsDir = Path.Combine(gamePath, "Logs");
        _questIds = new HashSet<string>(questIds);
        _onQuestCompleted = onQuestCompleted;

        if (!Directory.Exists(_logsDir))
        {
            Status = $"папка не найдена: {_logsDir}";
            _timer = new System.Threading.Timer(_ => { });
            return;
        }

        // существующее содержимое логов не перечитываем — только новые записи
        foreach (var file in EnumerateNotificationLogs())
            _positions[file] = new FileInfo(file).Length;

        Status = "работает";
        _timer = new System.Threading.Timer(_ => Poll(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private IEnumerable<string> EnumerateNotificationLogs() => EnumerateNotificationLogs(_logsDir);

    public static IEnumerable<string> EnumerateNotificationLogs(string logsDir)
    {
        try
        {
            return Directory.EnumerateFiles(logsDir, "*notifications*.log", SearchOption.AllDirectories);
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private void Poll()
    {
        if (_disposed) return;
        lock (_sync)
        {
            try
            {
                foreach (var file in EnumerateNotificationLogs())
                {
                    var length = new FileInfo(file).Length;
                    var pos = _positions.TryGetValue(file, out var p) ? p : 0;
                    if (length <= pos)
                    {
                        _positions[file] = length;
                        continue;
                    }

                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    fs.Seek(pos, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    var chunk = reader.ReadToEnd();
                    _positions[file] = length;

                    ProcessChunk(chunk);
                }
            }
            catch (Exception ex)
            {
                Status = "ошибка: " + ex.Message;
            }
        }
    }

    private void ProcessChunk(string chunk)
    {
        // JSON уведомлений многострочный, поэтому ищем по всему куску
        foreach (Match m in QuestMessageRegex().Matches(chunk))
        {
            var id = m.Groups["id"].Value;
            var kind = m.Groups["kind"].Value;
            AppendDebug($"{kind} {id}");

            if (kind == "successMessageText" && _questIds.Contains(id))
                _onQuestCompleted(id);
        }
    }

    private static void AppendDebug(string line)
    {
        try
        {
            var file = DataStore.LogWatchDebugFile;
            if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line.Trim()}\n");
        }
        catch
        {
            // отладочный лог не должен ронять вотчер
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
