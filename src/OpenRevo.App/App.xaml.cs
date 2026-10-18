using Microsoft.UI.Xaml;

namespace OpenRevo.App;

public partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo");
            Directory.CreateDirectory(directory);
            try
            {
                var file = Path.Combine(directory, "crash.log");
                if (File.Exists(file) && new FileInfo(file).Length > 65536) File.WriteAllText(file, "");
                var diagnostic = OpenRevo.Core.MonitoringDiagnostic.From(e.Exception);
                File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} {diagnostic.Code}: {diagnostic.Message}\n");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
