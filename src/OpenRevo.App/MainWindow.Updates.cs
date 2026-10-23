using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using Windows.System;

namespace OpenRevo.App;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private Action? _checkUpdates;
    private FrameworkElement? _updateCard;
    private bool _revealUpdateCard;

    private void RevealUpdateCard()
    {
        _revealUpdateCard = true;
        if (_updateCard?.IsLoaded != true) return;
        _revealUpdateCard = false;
        _updateCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private FrameworkElement UpdateSettings()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Text("版本更新", 18, true));
        panel.Children.Add(Secondary($"当前版本 {AppVersion.Current} · 手动从 GitHub 检查正式版，不在后台定时联网。"));
        var status = Text("尚未检查更新", 14);
        status.IsTextSelectionEnabled = true;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var check = new Button { Content = "检查更新" };
        var release = new HyperlinkButton { Content = "打开 GitHub 发布页", NavigateUri = GitHubUpdates.ReleasesUrl, Padding = new Thickness(0) };
        var download = new HyperlinkButton { Content = "下载 Windows 便携版", Padding = new Thickness(0), Visibility = Visibility.Collapsed };
        var notes = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 220, Visibility = Visibility.Collapsed, Header = "发布说明" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        actions.Children.Add(check); actions.Children.Add(release);
        async void CheckUpdates()
        {
            if (!check.IsEnabled) return;
            check.IsEnabled = false; status.Text = "正在连接 GitHub…";
            download.Visibility = notes.Visibility = Visibility.Collapsed;
            release.NavigateUri = GitHubUpdates.ReleasesUrl;
            try
            {
                using var client = new HttpClient();
                var result = await new GitHubUpdates(client).CheckAsync(AppVersion.Current, _updateLifetime.Token);
                if (_closed) return;
                status.Text = result.Status switch
                {
                    UpdateStatus.Available => $"发现新版本 {result.Version}（当前 {AppVersion.Current}）",
                    UpdateStatus.Current => $"当前已是最新正式版 {result.Version}",
                    _ => $"当前版本 {AppVersion.Current} 高于 GitHub 最新正式版 {result.Version}"
                };
                status.Text += $" · 检查于 {DateTime.Now:HH:mm}";
                release.NavigateUri = result.ReleaseUrl;
                if (result.Status == UpdateStatus.Available && result.DownloadUrl is not null)
                { download.NavigateUri = result.DownloadUrl; download.Visibility = Visibility.Visible; }
                notes.Text = result.Notes;
                if (result.Notes.Length != 0) notes.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) when (!_updateLifetime.IsCancellationRequested)
            { status.Text = "连接 GitHub 超时，请稍后重试，或打开发布页查看。"; }
            catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            { if (!_closed) status.Text = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.NotFound } ? ex.Message : "未能检查更新，请确认网络连接后重试，或打开 GitHub 发布页查看。"; }
            finally
            {
                if (!_closed)
                {
                    check.IsEnabled = true;
                    FrameworkElementAutomationPeer.CreatePeerForElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            }
        }
        _checkUpdates = CheckUpdates;
        check.Click += (_, _) => CheckUpdates();
        panel.Children.Add(actions); panel.Children.Add(status); panel.Children.Add(download); panel.Children.Add(notes);
        panel.Children.Add(Secondary("下载由浏览器完成。更新时先完全退出 OpenRevo，再将新版解压到独立文件夹运行；设置会保留。"));
        _updateCard = Card(panel);
        _updateCard.Loaded += (_, _) => { if (_revealUpdateCard) RevealUpdateCard(); };
        return _updateCard;
    }
}
