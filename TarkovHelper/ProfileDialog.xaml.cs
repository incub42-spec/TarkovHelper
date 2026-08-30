using System.Windows;

namespace TarkovHelper;

/// <summary>
/// Создание и правка профиля персонажа: имя, режим, фракция, уровень.
/// Всё в одном окне — в шапке приложения этим полям не место, там они
/// только занимают место и путают.
/// </summary>
public partial class ProfileDialog : Window
{
    /// <summary>Первый пункт — «не указана»: тогда показываем квесты обеих фракций.</summary>
    public static readonly List<string> FactionOptions = new() { "не указана", "USEC", "BEAR" };

    public ProfileDialog()
    {
        InitializeComponent();
        CmbFaction.ItemsSource = FactionOptions;
        CmbFaction.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            TxtName.SelectAll();
            TxtName.Focus();
        };
    }

    public string ProfileName
    {
        get => TxtName.Text.Trim();
        set => TxtName.Text = value;
    }

    public bool IsPve
    {
        get => RadioPve.IsChecked == true;
        set
        {
            RadioPve.IsChecked = value;
            RadioPvp.IsChecked = !value;
        }
    }

    /// <summary>«USEC», «BEAR» или пусто.</summary>
    public string Faction
    {
        get => CmbFaction.SelectedItem as string is "USEC" or "BEAR"
            ? (string)CmbFaction.SelectedItem
            : "";
        set => CmbFaction.SelectedItem = value is "USEC" or "BEAR" ? value : FactionOptions[0];
    }

    /// <summary>Уровень персонажа; 0 — не указан.</summary>
    public int Level
    {
        get => int.TryParse(TxtLevel.Text.Trim(), out var v) && v is > 0 and <= 79 ? v : 0;
        set => TxtLevel.Text = value > 0 ? value.ToString() : "";
    }

    /// <summary>При переименовании режим менять нельзя — к нему привязан прогресс.</summary>
    public bool ModeEditable
    {
        set => PanelMode.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>При создании профиля фракция и уровень ещё не нужны.</summary>
    public bool DetailsVisible
    {
        set => PanelDetails.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Подпись над полем — окно используется и как обычный запрос названия.</summary>
    public string Prompt
    {
        set => LblName.Text = value;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (ProfileName.Length == 0)
        {
            MessageBox.Show(this, "Введите название.", "Tarkov Helper",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
