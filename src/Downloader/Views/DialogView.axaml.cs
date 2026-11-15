using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Downloader.Views;

public enum DialogResult
{
    None,
    Primary,
    Secondary
}

/// <summary>窗口内模态对话框。通过 <see cref="MainWindow.ShowDialogAsync"/> 显示。</summary>
public partial class DialogView : UserControl
{
    private readonly TaskCompletionSource<DialogResult> _result = new();

    public DialogView()
    {
        InitializeComponent();
    }

    public DialogView(string title, object body, string? primaryText, string closeText = "取消", string? secondaryText = null)
        : this()
    {
        TitleText.Text = title;
        BodyPresenter.Content = body;

        PrimaryButton.Content = primaryText;
        PrimaryButton.IsVisible = primaryText is not null;
        CloseButton.Content = closeText;
        SecondaryButton.Content = secondaryText;
        SecondaryButton.IsVisible = secondaryText is not null;
    }

    public Task<DialogResult> Result => _result.Task;

    public bool IsPrimaryEnabled
    {
        get => PrimaryButton.IsEnabled;
        set => PrimaryButton.IsEnabled = value;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Complete(DialogResult.None);
            e.Handled = true;
        }
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e) => Complete(DialogResult.Primary);

    private void OnSecondaryClick(object? sender, RoutedEventArgs e) => Complete(DialogResult.Secondary);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Complete(DialogResult.None);

    private void Complete(DialogResult result) => _result.TrySetResult(result);

    /// <summary>关闭对话框，相当于点“取消”（Esc）。</summary>
    public void Dismiss() => Complete(DialogResult.None);

    /// <summary>把焦点放到对话框里：主按钮可用时是主按钮，否则是关闭按钮。</summary>
    public void FocusInitial()
    {
        if (PrimaryButton is { IsVisible: true, IsEffectivelyEnabled: true })
            PrimaryButton.Focus(NavigationMethod.Tab);
        else
            CloseButton.Focus(NavigationMethod.Tab);
    }
}
