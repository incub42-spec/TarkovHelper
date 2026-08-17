namespace TarkovHelper.Models;

/// <summary>Прогресс игрока и настройки. Хранится в %AppData%\TarkovHelper\progress.json.</summary>
public sealed class Progress
{
    public HashSet<string> CompletedQuests { get; set; } = new();
    /// <summary>Ид станции убежища -> построенный уровень (0 = не построено).</summary>
    public Dictionary<string, int> HideoutLevels { get; set; } = new();
    /// <summary>Папка с игрой (для чтения логов). Например C:\Battlestate Games\EFT.</summary>
    public string? GamePath { get; set; }
    /// <summary>Показывать в списке предметы, нужные только для обменов.</summary>
    public bool ShowBarterItems { get; set; }
    /// <summary>Подсвечивать область скриншота при сканировании (отладка OCR).</summary>
    public bool ShowScanRegion { get; set; }
}
