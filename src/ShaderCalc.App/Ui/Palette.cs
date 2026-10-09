using System.Windows.Media;
using Microsoft.Win32;

namespace ShaderCalc.App.Ui;

/// <summary>Editor and result colours, in a dark and a light variant following the Windows app theme.</summary>
internal sealed class Palette
{
    public static Palette Current { get; } = IsDarkTheme() ? CreateDark() : CreateLight();

    public required bool IsDark { get; init; }

    public required Brush EditorBackground { get; init; }

    public required Brush EditorForeground { get; init; }

    public required Brush ResultBackground { get; init; }

    public required Brush LineNumbers { get; init; }

    public required Color Comment { get; init; }

    public required Color Keyword { get; init; }

    public required Color Type { get; init; }

    public required Color Intrinsic { get; init; }

    public required Color Number { get; init; }

    public required Color String { get; init; }

    public required Color Preprocessor { get; init; }

    public required Brush Result { get; init; }

    public required Brush Dim { get; init; }

    public required Brush Error { get; init; }

    public required Brush Warning { get; init; }

    public required Brush Match { get; init; }

    public required Brush Approximate { get; init; }

    public required Brush SelectedLine { get; init; }

    public required Brush CodeBackground { get; init; }

    private static Palette CreateDark() => new Palette
    {
        IsDark = true,
        EditorBackground = Brush(0x1E1E1E),
        EditorForeground = Brush(0xD4D4D4),
        ResultBackground = Brush(0x252526),
        LineNumbers = Brush(0x6E7681),
        Comment = Color(0x6A9955),
        Keyword = Color(0x569CD6),
        Type = Color(0x4EC9B0),
        Intrinsic = Color(0xDCDCAA),
        Number = Color(0xB5CEA8),
        String = Color(0xCE9178),
        Preprocessor = Color(0xC586C0),
        Result = Brush(0x9CDCFE),
        Dim = Brush(0x808080),
        Error = Brush(0xF14C4C),
        Warning = Brush(0xCCA700),
        Match = Brush(0x4EC94E),
        Approximate = Brush(0xD7BA7D),
        SelectedLine = Brush(0x264F78, 0x80),
        CodeBackground = Brush(0x181818),
    };

    private static Palette CreateLight() => new Palette
    {
        IsDark = false,
        EditorBackground = Brush(0xFFFFFF),
        EditorForeground = Brush(0x1F1F1F),
        ResultBackground = Brush(0xF5F5F5),
        LineNumbers = Brush(0x8A8A8A),
        Comment = Color(0x008000),
        Keyword = Color(0x0000FF),
        Type = Color(0x267F99),
        Intrinsic = Color(0x795E26),
        Number = Color(0x098658),
        String = Color(0xA31515),
        Preprocessor = Color(0xAF00DB),
        Result = Brush(0x001080),
        Dim = Brush(0x6E6E6E),
        Error = Brush(0xE51400),
        Warning = Brush(0xBF8803),
        Match = Brush(0x388A34),
        Approximate = Brush(0x9A6B00),
        SelectedLine = Brush(0xADD6FF, 0x80),
        CodeBackground = Brush(0xF3F3F3),
    };

    private static bool IsDarkTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static Color Color(int rgb) => System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Brush Brush(int rgb, byte alpha = 0xFF)
    {
        Color color = Color(rgb);
        SolidColorBrush brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
