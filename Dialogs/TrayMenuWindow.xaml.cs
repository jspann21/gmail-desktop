using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GmailDesktop.Dialogs;

public partial class TrayMenuWindow : Window
{
    private readonly Action _openAction;
    private readonly Action _exitAction;

    public bool IsClosing { get; private set; }

    public TrayMenuWindow(Action openAction, Action exitAction)
    {
        _openAction = openAction;
        _exitAction = exitAction;
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionBesideCursor();
        Activate();
    }

    private void PositionBesideCursor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var cursor = Forms.Cursor.Position;
        var monitor = MonitorFromPoint(new NativePoint(cursor.X, cursor.Y), MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var dpi = GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / 96d : 1;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        const int gap = 6;

        var left = cursor.X - width + 22;
        var top = cursor.Y - height - gap;
        var maximumLeft = Math.Max(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Right - width);
        var maximumTop = Math.Max(monitorInfo.WorkArea.Top, monitorInfo.WorkArea.Bottom - height);
        left = Math.Clamp(left, monitorInfo.WorkArea.Left, maximumLeft);
        top = Math.Clamp(top, monitorInfo.WorkArea.Top, maximumTop);

        _ = SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        RunAfterClose(_openAction);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        RunAfterClose(_exitAction);
    }

    private void RunAfterClose(Action action)
    {
        if (IsClosing) return;
        Close();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, action);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        IsClosing = true;
        base.OnClosing(e);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!IsClosing) Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
