namespace TarkovHelper.Models;

/// <summary>
/// Профиль персонажа: свой прогресс и свой режим игры. У PvE и PvP персонажи
/// разные, поэтому квесты и убежище хранятся отдельно для каждого.
/// </summary>
public sealed class Progress
{
    /// <summary>Имя профиля, видимое в интерфейсе.</summary>
    public string Name { get; set; } = "Основной";
    /// <summary>Режим PvE: у него свой набор квестов и свои цены.</summary>
    public bool PveMode { get; set; }
    public HashSet<string> CompletedQuests { get; set; } = new();
    /// <summary>Ид станции убежища -> построенный уровень (0 = не построено).</summary>
    public Dictionary<string, int> HideoutLevels { get; set; } = new();

    public string ModeName => PveMode ? "PvE" : "PvP";
}

/// <summary>
/// Общие настройки приложения и список профилей.
/// Хранится в %AppData%\TarkovHelper\progress.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Папка с игрой (для чтения логов). Например C:\Battlestate Games\EFT.</summary>
    public string? GamePath { get; set; }
    /// <summary>Показывать в списке предметы, нужные только для обменов.</summary>
    public bool ShowBarterItems { get; set; }
    /// <summary>Подсвечивать область скриншота при сканировании (отладка OCR).</summary>
    public bool ShowScanRegion { get; set; }
    /// <summary>Клавиша сканирования предмета (виртуальный код Windows). F9 по умолчанию.</summary>
    public uint ItemHotkey { get; set; } = 0x78;
    /// <summary>Клавиша сканирования убежища. F10 по умолчанию.</summary>
    public uint HideoutHotkey { get; set; } = 0x79;

    /// <summary>Имя активного профиля.</summary>
    public string ActiveProfile { get; set; } = "";
    public List<Progress> Profiles { get; set; } = new();

    /// <summary>Активный профиль; при пустом списке создаётся профиль по умолчанию.</summary>
    public Progress Active
    {
        get
        {
            if (Profiles.Count == 0)
                Profiles.Add(new Progress { Name = "PvE", PveMode = true });

            return Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase))
                ?? Profiles[0];
        }
    }
}
