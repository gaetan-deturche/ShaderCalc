using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using KalkGui.Engine;

namespace KalkGui.Ui;

/// <summary>Windows Terminal "Campbell" palette, so colours match kalk running in the terminal.</summary>
internal static class KalkPalette
{
    public static readonly Brush Background = CreateBrush(0x181818);
    public static readonly Brush Foreground = CreateBrush(0xCCCCCC);
    public static readonly Brush Prompt = CreateBrush(0x767676);
    public static readonly Brush Error = CreateBrush(0xE74856);
    public static readonly Brush Code = CreateBrush(0x61D6D6);

    // Indexed by KalkColor
    private static readonly Brush[] Colors =
    {
        Foreground,
        CreateBrush(0x0C0C0C), CreateBrush(0xC50F1F), CreateBrush(0x13A10E), CreateBrush(0xC19C00),
        CreateBrush(0x3B78FF), CreateBrush(0xB4009E), CreateBrush(0x3A96DD), CreateBrush(0xCCCCCC),
        CreateBrush(0x767676), CreateBrush(0xE74856), CreateBrush(0x16C60C), CreateBrush(0xF9F1A5),
        CreateBrush(0x3B78FF), CreateBrush(0xB4009E), CreateBrush(0x61D6D6), CreateBrush(0xF2F2F2),
    };

    public static void Apply(Run run, KalkTextStyle style)
    {
        (Brush foreground, Brush? background) = Resolve(style);
        run.Foreground = foreground;
        if (background != null)
        {
            run.Background = background;
        }
        if (style.Bold)
        {
            run.FontWeight = FontWeights.SemiBold;
        }
        if (style.Underline)
        {
            run.TextDecorations = TextDecorations.Underline;
        }
    }

    public static void Apply(VisualLineElement element, KalkTextStyle style)
    {
        (Brush foreground, Brush? background) = Resolve(style);
        element.TextRunProperties.SetForegroundBrush(foreground);
        if (background != null)
        {
            element.TextRunProperties.SetBackgroundBrush(background);
        }
        if (style.Bold)
        {
            Typeface typeface = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(new Typeface(typeface.FontFamily, typeface.Style, FontWeights.SemiBold, typeface.Stretch));
        }
        if (style.Underline)
        {
            element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
        }
    }

    private static (Brush Foreground, Brush? Background) Resolve(KalkTextStyle style)
    {
        // Terminals render bold default-coloured text (kalk's numbers) as bright white
        KalkColor foregroundColor = style.Foreground == KalkColor.Default && style.Bold ? KalkColor.BrightWhite : style.Foreground;
        Brush foreground = Colors[(int)foregroundColor];
        Brush? background = style.Background == KalkColor.Default ? null : Colors[(int)style.Background];
        return style.Reversed ? (background ?? Background, foreground) : (foreground, background);
    }

    private static Brush CreateBrush(int rgb)
    {
        SolidColorBrush brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
