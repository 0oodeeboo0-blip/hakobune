using System.Runtime.InteropServices;

namespace Hakobune.App.Infrastructure;

/// <summary>タスクバーのボタンの進み具合の色。</summary>
internal enum TaskbarProgressState
{
    /// <summary>出さない。</summary>
    None = 0,

    /// <summary>終わりが分からない（左右に流れる）。</summary>
    Indeterminate = 0x1,

    /// <summary>進んでいる（緑）。</summary>
    Normal = 0x2,

    /// <summary>失敗で止まった（赤）。</summary>
    Error = 0x4,

    /// <summary>一時停止（黄）。</summary>
    Paused = 0x8,
}

/// <summary>
/// タスクバーのボタンに、転送の進み具合を出す（ITaskbarList3）。使えない環境では何もしない。画面のスレッドで使う。
/// </summary>
internal sealed class TaskbarProgress
{
    private readonly nint _window;
    private readonly ITaskbarList3? _taskbar;
    private TaskbarProgressState _state = TaskbarProgressState.None;
    private ulong _value = ulong.MaxValue;

    public TaskbarProgress(nint window)
    {
        _window = window;
        try
        {
            var taskbar = (ITaskbarList3)new TaskbarList();
            taskbar.HrInit();
            _taskbar = taskbar;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            _taskbar = null;
        }
    }

    /// <summary>覚えている状態を忘れる（次の <see cref="Show"/> で必ず出し直す）。</summary>
    public void Reset()
    {
        _state = (TaskbarProgressState)(-1);
        _value = ulong.MaxValue;
    }

    /// <summary>進み具合を出す。<paramref name="percent"/> は 0〜100。</summary>
    public void Show(TaskbarProgressState state, double percent)
    {
        if (_taskbar is null)
            return;
        try
        {
            if (state != _state)
            {
                _taskbar.SetProgressState(_window, state);
                _state = state;
                _value = ulong.MaxValue;
            }
            if (state is TaskbarProgressState.None or TaskbarProgressState.Indeterminate)
                return;
            var value = (ulong)Math.Clamp(Math.Round(percent * 10), 0, 1000);
            if (value != _value)
            {
                _taskbar.SetProgressValue(_window, value, 1000);
                _value = value;
            }
        }
        catch (COMException)
        {
            // タスクバーのボタンがまだ無いとき、エクスプローラーが入れ替わったときなど。次に呼ばれたときにまた出し直す
            Reset();
        }
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3（使うところまで）
        void SetProgressValue(nint hwnd, ulong completed, ulong total);

        void SetProgressState(nint hwnd, TaskbarProgressState state);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }
}
