using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace GmailDesktop.Dialogs;

internal static class DialogPlacement
{
    public static void FitToOwnerWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var ownerHandle = window.Owner is { } owner
            ? new WindowInteropHelper(owner).Handle
            : IntPtr.Zero;
        var referenceHandle = ownerHandle != IntPtr.Zero ? ownerHandle : handle;
        var workArea = Forms.Screen.FromHandle(referenceHandle).WorkingArea;
        var dpi = GetDpiForWindow(referenceHandle);
        var scale = dpi > 0 ? dpi / 96d : 1;
        var margin = (int)Math.Ceiling(12 * scale);
        var availableWidth = Math.Max(1, workArea.Width - margin * 2);
        var availableHeight = Math.Max(1, workArea.Height - margin * 2);
        var width = Math.Min((int)Math.Ceiling(window.Width * scale), availableWidth);
        var height = Math.Min((int)Math.Ceiling(window.Height * scale), availableHeight);

        // Keep all coordinates in screen pixels so monitors with different DPI
        // settings or negative desktop coordinates use the same coordinate space.
        var left = workArea.Left + (workArea.Width - width) / 2;
        var top = workArea.Top + (workArea.Height - height) / 2;
        if (ownerHandle != IntPtr.Zero && GetWindowRect(ownerHandle, out var ownerBounds))
        {
            left = ownerBounds.Left + (ownerBounds.Right - ownerBounds.Left - width) / 2;
            top = ownerBounds.Top + (ownerBounds.Bottom - ownerBounds.Top - height) / 2;
        }

        var minimumLeft = workArea.Left + margin;
        var minimumTop = workArea.Top + margin;
        left = Math.Clamp(left, minimumLeft, Math.Max(minimumLeft, workArea.Right - margin - width));
        top = Math.Clamp(top, minimumTop, Math.Max(minimumTop, workArea.Bottom - margin - height));

        _ = SetWindowPos(handle, IntPtr.Zero, left, top, width, height, NoZOrder | NoActivate);
    }

    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

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
