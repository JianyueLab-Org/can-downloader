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
        if (!TrySave())
            return;
        _accepted = true;
        Close(true);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (TrySave())
            _snapshot = Vm.Snapshot();
    }

    /// <summary>保存设置。失败时在底部显示原因，窗口不关闭、修改保持未保存，“取消”仍还原到上次成功保存的设置。</summary>
    private bool TrySave()
    {
        try
        {
            Vm.Save();
            SaveErrorText.IsVisible = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveErrorText.Text = $"保存设置失败：{ex.Message}";
            ToolTip.SetTip(SaveErrorText, SaveErrorText.Text);
            SaveErrorText.IsVisible = true;
            return false;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
