using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KalkGui.Ui;

internal static class WindowZOrder
{
    private static readonly IntPtr HwndBottom = new IntPtr(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Puts the window behind every other window without activating it.</summary>
    public static void SendToBack(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        SetWindowPos(handle, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
