using System.Windows;
using TarkovHelper.Overlay;
using TarkovHelper.Services;

namespace TarkovHelper;

public partial class App : Application
{
    public static AppServices Services { get; } = new();

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DataStore.RootDir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(DataStore.RootDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch
        {
            // логирование не должно добивать процесс
        }
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            MessageBox.Show(args.Exception.ToString(), "TarkovHelper — ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };

        UpdateService.CleanupOldFiles();
        Services.Init();

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        // проверка обновлений при каждом запуске, молча и в фоне
        _ = main.CheckUpdateOnStartupAsync();

        var overlay = new OverlayWindow();
        overlay.Show();

        // при первом запуске или устаревшем кеше тянем свежую базу в фоне
        if (Services.Data == null || DateTime.UtcNow - Services.Data.FetchedAtUtc > TimeSpan.FromHours(24))
        {
            var error = await Services.RefreshDataAsync();
            if (error != null && Services.Data == null)
            {
                MessageBox.Show(
                    "Не удалось загрузить базу с tarkov.dev:\n" + error +
                    "\n\nПроверьте интернет и нажмите «Обновить данные» в настройках.",
                    "TarkovHelper", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        StartDailyRefreshTimer();
    }

    /// <summary>
    /// Ежечасно проверяет возраст базы и раз в сутки обновляет её в фоне —
    /// на случай, когда приложение работает без перезапуска несколько дней.
    /// Ошибки сети молча откладываются до следующей проверки.
    /// </summary>
    private void StartDailyRefreshTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(1),
        };
        timer.Tick += async (_, _) =>
        {
            if (_refreshing) return;
            if (Services.Data != null && DateTime.UtcNow - Services.Data.FetchedAtUtc < TimeSpan.FromHours(24))
                return;
            _refreshing = true;
            try
            {
                await Services.RefreshDataAsync();
            }
            finally
            {
                _refreshing = false;
            }
        };
        timer.Start();
    }

    private bool _refreshing;
}
