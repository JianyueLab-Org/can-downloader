using Avalonia.Controls;
using Avalonia.Interactivity;
using Downloader.ViewModels;

namespace Downloader.Views.Settings;

public partial class DownloadPage : UserControl
{
    public DownloadPage() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private async void OnBrowseInstallRootClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await FolderPicker.PickAsync(this, "选择默认安装目录", vm.InstallRootPath) is { } path)
            vm.InstallRoot = path;
    }

    private async void OnBrowseCacheFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await FolderPicker.PickAsync(this, "选择下载缓存目录", vm.CacheFolderPath) is { } path)
            vm.CacheFolder = path;
    }
}
