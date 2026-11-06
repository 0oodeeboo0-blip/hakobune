using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Hakobune.App.Controls;

/// <summary>
/// 上下の境目をつかんで、下の一覧の高さを変える棒。マウスでは上下の矢印のカーソルになり、キーボードでは ↑↓ で動かせる。
/// </summary>
public sealed partial class SplitterBar : Grid
{
    private const double KeyStep = 24;

    private double? _lastY;

    public SplitterBar()
    {
        // 透明でも塗っておかないと、マウスが当たらない
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, "転送の一覧の高さ（↑↓で変える）");
        // カーソルは、この部品の中からしか変えられない
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => Finish(e.Pointer);
        PointerCaptureLost += (_, _) => Finish(null);
        KeyDown += OnKeyDown;
    }

    /// <summary>上下に動かした（上が負）。前回からの量。</summary>
    public event EventHandler<double>? Dragged;

    /// <summary>動かし終わった（高さを保存してよい）。</summary>
    public event EventHandler? DragCompleted;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
            return;
        _lastY = e.GetCurrentPoint(null).Position.Y;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_lastY is not { } last)
            return;
        var y = e.GetCurrentPoint(null).Position.Y;
        if (Math.Abs(y - last) < 0.5)
            return;
        _lastY = y;
        Dragged?.Invoke(this, y - last);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var delta = e.Key switch
        {
            VirtualKey.Up => -KeyStep,
            VirtualKey.Down => KeyStep,
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
        if (_lastY is null)
            return;
        _lastY = null;
        if (pointer is not null)
            ReleasePointerCapture(pointer);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
