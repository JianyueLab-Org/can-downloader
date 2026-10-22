using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Downloader.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
