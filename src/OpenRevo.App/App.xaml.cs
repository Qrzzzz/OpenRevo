using Microsoft.UI.Xaml;

namespace OpenRevo.App;

public partial class App : Application
{
    private MainWindow? _window;
    private Mutex? _uiInstance;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo");
            try
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "crash.log");
                if (File.Exists(file) && new FileInfo(file).Length > 65536) File.WriteAllText(file, "");
                var diagnostic = OpenRevo.Core.MonitoringDiagnostic.From(e.Exception);
                File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} {diagnostic.Code}: {diagnostic.Message}\n");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        OpenRevo.Core.TrayAction? trayAction = OpenRevo.Core.TrayActions.TryParse(Environment.GetCommandLineArgs()[1..], out var parsed) ? parsed : null;
        _uiInstance = new Mutex(false, @"Local\OpenRevo.Ui.v1");
        var ownsInstance = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { ownsInstance = _uiInstance.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstance = true; }
            if (ownsInstance || DesktopLifetime.ShowExisting(trayAction)) break;
            // The first window may still be loading, or finishing its clean shutdown.
            await Task.Delay(100);
        }
        if (!ownsInstance)
        {
            _uiInstance.Dispose(); _uiInstance = null;
            Exit(); return;
        }
        _window = new MainWindow();
        _window.Closed += (_, _) => { _uiInstance.ReleaseMutex(); _uiInstance.Dispose(); _uiInstance = null; };
        _window.Activate();
        if (trayAction is not null) _window.RequestTrayAction(trayAction.Value);
    }
}
