using Avalonia.Controls;
using Avalonia.Interactivity;
using Downloader.ViewModels;
using Downloader.Views.Settings;

namespace Downloader.Views;

public partial class SettingsWindow : Window
{
    private readonly Dictionary<string, Control> _panels = [];
    private Dictionary<string, object?>? _snapshot;
    private bool _accepted;

    public SettingsWindow()
    {
        InitializeComponent();
        Menu.SelectedIndex = 0;
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _snapshot = Vm.Snapshot();
    }

    // 取消或点 ✕ 关闭：还原到打开时（或上次“应用”时）的设置
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_accepted && _snapshot is not null)
            Vm.Restore(_snapshot);
    }

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

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Vm.Save();
        _accepted = true;
        Close(true);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        Vm.Save();
        _snapshot = Vm.Snapshot();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
