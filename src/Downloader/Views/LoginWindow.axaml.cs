using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Downloader.Views;

public partial class LoginWindow : Window
{
    public LoginWindow() => InitializeComponent();

    // 记住了账号时直接把焦点放在密码框
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (string.IsNullOrEmpty(CanIdBox.Text) ? CanIdBox : PasswordBox).Focus();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not TextBox && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
