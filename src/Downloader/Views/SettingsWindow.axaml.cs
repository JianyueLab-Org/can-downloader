using Avalonia.Controls;
using Avalonia.Interactivity;
using Downloader.ViewModels;
using Downloader.Views.Settings;

namespace Downloader.Views;

public partial class SettingsWindow : Window
{
    private readonly Dictionary<string, Control> _panels = [];

    public SettingsWindow()
    {
        InitializeComponent();
        Menu.SelectedIndex = 0;
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    private void OnMenuSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Menu.SelectedItem is not ListBoxItem { Tag: string tag })
            return;

        if (!_panels.TryGetValue(tag, out var panel))
        {
            panel = CreatePanel(tag);
            _panels[tag] = panel;
        }

        PanelHost.Content = panel;
    }

    private static Control CreatePanel(string tag) => tag switch
    {
        "General" => new GeneralPage(),
        "Download" => new DownloadPage(),
        "Update" => new UpdatePage(),
        "Network" => new NetworkPage(),
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, null)
    };

    // 界面原型：设置尚未持久化，仅重置“应用”按钮状态
    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Vm.MarkSaved();
        Close(true);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => Vm.MarkSaved();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
