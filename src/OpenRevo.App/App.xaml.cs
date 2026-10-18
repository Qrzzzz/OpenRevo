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
            File.AppendAllText(Path.Combine(directory, "crash.log"), $"{DateTimeOffset.UtcNow:O} {e.Exception}\n");
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
