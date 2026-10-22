using Avalonia.Controls;
using Avalonia.Interactivity;
using Downloader.Services;
using Downloader.ViewModels;

namespace Downloader.Views;

/// <summary>软件详情。需要对话框的操作交给所在的 <see cref="MainWindow"/>。</summary>
public partial class ProductView : UserControl
{
    public ProductView() => InitializeComponent();

    private ProductViewModel? Product => DataContext as ProductViewModel;

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (Product is { } product && Host is { } host)
            await host.RunPrimaryActionAsync(product);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (Product is { } product)
            Host?.Cancel(product);
    }

    private void OnRepairClick(object? sender, RoutedEventArgs e)
    {
        if (Product is { } product)
            Host?.Repair(product);
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Product is { } product && Host is { } host)
            await host.OpenFolderAsync(product);
    }

    private void OnNotesClick(object? sender, RoutedEventArgs e)
    {
        if (Product?.NotesUrl is { } url)
            Shell.Open(url);
    }

    private async void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        if (Product is { } product && Host is { } host)
            await host.UninstallAsync(product);
    }
}
