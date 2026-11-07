using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Hakobune.App.Controls;

/// <summary>
/// 列の見出しの境目。つかんで左右に動かすと列の幅が変わる（マウスでは左右の矢印のカーソル、キーボードでは ←→）。
/// ダブルクリックで元の幅に戻す。
/// </summary>
public sealed partial class ColumnGrip : Grid
{
    private const double KeyStep = 8;

    private double? _lastX;

    public ColumnGrip()
    {
        // 透明でも塗っておかないと、マウスが当たらない
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Width = 10;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        // カーソルは、この部品の中からしか変えられない
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => Finish(e.Pointer);
        PointerCaptureLost += (_, _) => Finish(null);
        DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            ResetRequested?.Invoke(this, EventArgs.Empty);
        };
        KeyDown += OnKeyDown;
    }

    /// <summary>左右に動かした（左が負）。前回からの量。</summary>
    public event EventHandler<double>? Dragged;

    /// <summary>動かし終わった（幅を保存してよい）。</summary>
    public event EventHandler? DragCompleted;

    /// <summary>元の幅に戻したい（ダブルクリック）。</summary>
    public event EventHandler? ResetRequested;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
            return;
        _lastX = e.GetCurrentPoint(null).Position.X;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_lastX is not { } last)
            return;
        var x = e.GetCurrentPoint(null).Position.X;
        if (Math.Abs(x - last) < 0.5)
            return;
        _lastX = x;
        Dragged?.Invoke(this, x - last);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var delta = e.Key switch
        {
            VirtualKey.Left => -KeyStep,
            VirtualKey.Right => KeyStep,
            _ => 0,
        };
        if (delta == 0)
            return;
        Dragged?.Invoke(this, delta);
        DragCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void Finish(Pointer? pointer)
    {
        if (_lastX is null)
            return;
        _lastX = null;
        if (pointer is not null)
            ReleasePointerCapture(pointer);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
