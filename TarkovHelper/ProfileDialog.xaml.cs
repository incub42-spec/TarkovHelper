using System.Windows;

namespace TarkovHelper;

/// <summary>Создание и переименование профиля персонажа.</summary>
public partial class ProfileDialog : Window
{
    public ProfileDialog()
    {
        InitializeComponent();
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

    /// <summary>При переименовании режим менять нельзя — к нему привязан прогресс.</summary>
    public bool ModeEditable
    {
        set => PanelMode.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
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
