#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KalkGui.Ui;

/// <summary>
/// Debug-only UI-test hook (KALKGUI_AUTOMATION=1): a WM_COPYDATA tagged "KALK" carrying a PNG path renders
/// every visible app window there. Needed because UI tests run on a hidden desktop that nothing can capture.
/// </summary>
internal static class AutomationSnapshot
{
    private const int WmCopyData = 0x004A;
    private static readonly IntPtr SnapshotTag = new IntPtr(0x4B414C4B);

    public static void Attach(Window window)
    {
        if (Environment.GetEnvironmentVariable("KALKGUI_AUTOMATION") != "1")
        {
            return;
        }
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(OnMessage);
    }

    private static IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmCopyData)
        {
            return IntPtr.Zero;
        }

        CopyData data = Marshal.PtrToStructure<CopyData>(lParam);
        if (data.Tag != SnapshotTag)
        {
            return IntPtr.Zero;
        }

        string path = Marshal.PtrToStringUni(data.Text, data.ByteCount / 2).TrimEnd('\0');
        int windowIndex = 0;
        foreach (Window window in Application.Current.Windows)
        {
            if (window.IsVisible && window.Content is FrameworkElement content)
            {
                string windowPath = windowIndex == 0 ? path : $"{Path.ChangeExtension(path, null)}-{windowIndex}.png";
                Save(window, content, windowPath);
                windowIndex++;
            }
        }

        handled = true;
        return new IntPtr(windowIndex);
    }

    private static void Save(Window window, FrameworkElement content, string path)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        Rect bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
        // The Fluent window background is a DWM backdrop, absent from a visual-tree render
        Brush background = window.Background is SolidColorBrush { Color.A: 255 } solidBackground
            ? solidBackground
            : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(background, null, bounds);
            context.DrawRectangle(new VisualBrush(content), null, bounds);
        }

        RenderTargetBitmap bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX), (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public IntPtr Tag;
        public int ByteCount;
        public IntPtr Text;
    }
}
#endif
