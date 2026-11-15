using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Downloader.Services;

namespace Downloader.Views;

public partial class ResizeGrips : UserControl
{
    private Window? _window;
    private WindowEdge _edge;
    private PixelPoint _startPointer;
    private PixelPoint _startPosition;
    private Size _startSize;
    private Cursor? _windowCursor;

    public ResizeGrips() => InitializeComponent();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window { CanResize: true } window
            || sender is not Control { Tag: string tag } grip
            || !Enum.TryParse<WindowEdge>(tag, out var edge)
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        if (!PlatformInfo.NeedsManualResize)
        {
            window.BeginResizeDrag(edge, e);
            return;
        }
        _window = window;
        _edge = edge;
        _startPointer = window.PointToScreen(e.GetPosition(window));
        _startPosition = window.Position;
        _startSize = window.ClientSize;
        _windowCursor = window.Cursor;
        window.Cursor = grip.Cursor;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_window is not { } window) return;
        var pointer = window.PointToScreen(e.GetPosition(window));
        var scaling = window.DesktopScaling;
        var dx = (pointer.X - _startPointer.X) / scaling;
        var dy = (pointer.Y - _startPointer.Y) / scaling;
        var west = _edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;
        var east = _edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;
        var north = _edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;
        var south = _edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast;
        var width = Math.Clamp(_startSize.Width + (east ? dx : west ? -dx : 0), window.MinWidth, window.MaxWidth);
        var height = Math.Clamp(_startSize.Height + (south ? dy : north ? -dy : 0), window.MinHeight, window.MaxHeight);
        window.Width = width;
        window.Height = height;
        if (west || north)
            window.Position = new PixelPoint(
                west ? _startPosition.X + (int)Math.Round((_startSize.Width - width) * scaling) : _startPosition.X,
                north ? _startPosition.Y + (int)Math.Round((_startSize.Height - height) * scaling) : _startPosition.Y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_window is null) return;
        e.Pointer.Capture(null);
        EndResize();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndResize();
    }

    private void EndResize()
    {
        if (_window is null) return;
        _window.Cursor = _windowCursor;
        _window = null;
    }
}
