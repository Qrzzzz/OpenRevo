using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.App;

public sealed partial class MainWindow
{
    private async Task SelectModeAsync(PilotMode target)
    {
        // The current tile remains focusable when ready, but is a read-only selection.
        if (_observer?.Pilot.CurrentMode == ((int)target).ToString()) return;
        await OperateModeAsync(target, false);
    }

    private void RenderModeChoices(ModePilot? pilot)
    {
        var fresh = Sample("Fan/Status")?.Freshness(TimeProvider.System, _connected) == SampleFreshness.Current;
        foreach (var (button, target) in new[] { (_officeMode, PilotMode.Office), (_balancedMode, PilotMode.Balanced), (_turboMode, PilotMode.Turbo), (_customMode, PilotMode.Custom) })
        {
            var current = pilot?.CurrentMode == ((int)target).ToString();
            button.IsEnabled = UiPresentation.ChoiceEnabled(_connected, pilot?.Busy == true, current, pilot?.CanSwitch == true, pilot?.CanSwitchTo(target) == true);
            button.Style = current ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
            _choiceDescriptions[target].Style = current ? null : (Style)Application.Current.Resources["SecondaryBodyStyle"];
            _choiceFrames[target].Style = (Style)Application.Current.Resources[current ? "SelectedModeFrameStyle" : "ModeFrameStyle"];
            _choiceStates[target].Text = current ? fresh ? "当前" : "上次报告" : pilot?.Busy == true ? "核对中" : button.IsEnabled ? "" : "暂不可用";
            _choiceChecks[target].Visibility = current ? Visibility.Visible : Visibility.Collapsed;
            var reason = !_connected ? "设备未连接" : target == PilotMode.Turbo && Sample("Customize/SupportInfo")?.Text("TurboModeSupport") != "1"
                ? "原厂未声明狂暴模式支持" : FriendlyControlReason(pilot?.ControlStatus ?? "等待设备状态");
            AutomationProperties.SetName(button, (current ? fresh ? "当前模式：" : "上次报告的模式：" : "切换到") + DeviceAudit.ModeName(((int)target).ToString()));
            AutomationProperties.SetHelpText(button, current && button.IsEnabled ? "已处于此模式，无需重复发送命令" : button.IsEnabled ? "选择后立即切换并核对结果" : reason);
            ToolTipService.SetToolTip(_choiceFrames[target], button.IsEnabled ? current ? "当前模式已生效" : "选择后立即切换" : reason);
        }
    }

    private static Grid SettingRow(string title, string description, FrameworkElement editor)
    {
        var row = new Grid { ColumnSpacing = 20, RowSpacing = 8, MinHeight = 54 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var label = Text(title, UiStyle.Body, true);
        labels.Children.Add(label); labels.Children.Add(Secondary(description)); row.Children.Add(labels);
        editor.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(editor, title);
        AutomationProperties.SetLabeledBy(editor, label);
        AutomationProperties.SetHelpText(editor, description);
        if (editor is ToggleSwitch toggle) { toggle.Header = null; toggle.OnContent = ""; toggle.OffContent = ""; }
        Grid.SetColumn(editor, 1); row.Children.Add(editor);
        row.SizeChanged += (_, _) =>
        {
            var narrow = row.ActualWidth < 640;
            Grid.SetColumn(editor, narrow ? 0 : 1); Grid.SetRow(editor, narrow ? 1 : 0);
            editor.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Grid.SetColumnSpan(labels, narrow ? 2 : 1);
        };
        return row;
    }
}
