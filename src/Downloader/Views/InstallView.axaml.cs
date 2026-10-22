using Avalonia.Controls;
using Avalonia.Interactivity;
using Downloader.ViewModels;

namespace Downloader.Views;

public partial class InstallView : UserControl
{
    public InstallView() => InitializeComponent();

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InstallViewModel vm && await FolderPicker.PickAsync(this, $"选择 {vm.Product.Name} 的安装位置", vm.ResolvedInstallPath) is { } folder)
            vm.SetLocationFromFolder(folder);
    }
}
