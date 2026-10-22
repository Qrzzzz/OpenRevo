using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace OpenRevo.App;

public sealed partial class MainWindow
{
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo", "preferences.json");
    private AppPreferences _preferences = AppPreferences.Load(PreferencesPath);
    private bool _placementReady;
    private bool _started;
    private bool _closeReady;

    private void ApplyTheme() => _root.RequestedTheme = _preferences.Theme switch
    { AppThemePreference.Light => ElementTheme.Light, AppThemePreference.Dark => ElementTheme.Dark, _ => ElementTheme.Default };

    private void RestoreWindowPlacement()
    {
        var scale = _root.XamlRoot?.RasterizationScale ?? 1;
        var saved = _preferences.Window;
        var area = saved is null ? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea
            : DisplayArea.GetFromRect(new Windows.Graphics.RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.Nearest).WorkArea;
        var bounds = WindowBounds.Fit(saved, new WindowBounds(area.X, area.Y, area.Width, area.Height), scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        _preferences = _preferences with { Window = bounds };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(bounds.Width, (int)(720 * scale));
            presenter.PreferredMinimumHeight = Math.Min(bounds.Height, (int)(440 * scale));
            _placementReady = true;
            if (_preferences.Maximized) presenter.Maximize();
        }
        AppWindow.Changed += (_, _) => CaptureWindowPlacement();
    }

    private void CaptureWindowPlacement()
    {
        if (!_placementReady || _closed || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized) _preferences = _preferences with { Maximized = true };
        else if (presenter.State == OverlappedPresenterState.Restored)
            _preferences = _preferences with
            {
                Maximized = false,
                Window = new WindowBounds(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height)
            };
        // Minimizing must not replace the last normal bounds or maximized state.
    }

    private void SavePreferences()
    {
        try { _preferences.Save(PreferencesPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_closed) ShowNotice("设置暂未保存", "当前设置已应用，但下次启动可能恢复默认。请确认本地目录可写。", InfoBarSeverity.Warning);
        }
    }

    private async void CloseWindow(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeReady) return;
        args.Cancel = true;
        if (_closed) return;
        CaptureWindowPlacement();
        _closed = true;
        _root.IsHitTestVisible = false;
        _timer.Stop();
        DisablePowerNotifications();
        if (_themeSettings is not null) _themeSettings.Changed -= HighContrastChanged;
        _stop?.Cancel();
        // Finish the observer (including its final journal/health state) before writing the report.
        if (_session is not null) await _session;
        _connected = false;
        SavePreferences();
        SaveLastSession();
        _stop?.Dispose();
        if (_exitRequested || !_preferences.StayInTray) DesktopLifetime.StopBackground();
        else
        {
            try { DesktopLifetime.EnsureBackground(); }
            catch (Exception ex) when (LifetimeFailure(ex)) { /* Closing must still release the UI if the tray cannot start. */ }
        }
        DesktopLifetime.RemoveProp(_nativeWindow, DesktopLifetime.UiProperty);
        if (_lifetimeProcedure is not null) DesktopLifetime.RemoveWindowSubclass(_nativeWindow, _lifetimeProcedure, 1);
        _closeReady = true;
        Close();
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        _notice.Title = title; _notice.Message = message; _notice.Severity = severity; _notice.IsOpen = true;
        FrameworkElementAutomationPeer.CreatePeerForElement(_notice)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
