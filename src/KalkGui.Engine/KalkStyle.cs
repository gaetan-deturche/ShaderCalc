using Consolus;

namespace KalkGui.Engine;

/// <summary>The 16 ANSI colours kalk's highlighter emits, plus the terminal default.</summary>
public enum KalkColor
{
    Default,
    Black,
    Red,
    Green,
    Yellow,
    Blue,
    Magenta,
    Cyan,
    White,
    BrightBlack,
    BrightRed,
    BrightGreen,
    BrightYellow,
    BrightBlue,
    BrightMagenta,
    BrightCyan,
    BrightWhite,
}

public readonly record struct KalkTextStyle(KalkColor Foreground, KalkColor Background, bool Bold, bool Underline, bool Reversed);

public readonly record struct StyledSpan(int Start, int Length, KalkTextStyle Style)
{
    public int End => Start + Length;
}

/// <summary>Turns kalk's per-character enable/disable style markers into flat, ordered, non-overlapping spans.</summary>
internal static class KalkStyleConverter
{
    private static readonly Dictionary<ConsoleStyle, KalkColor> ForegroundColors = new Dictionary<ConsoleStyle, KalkColor>
    {
        [ConsoleStyle.Black] = KalkColor.Black,
        [ConsoleStyle.Red] = KalkColor.Red,
        [ConsoleStyle.Green] = KalkColor.Green,
        [ConsoleStyle.Yellow] = KalkColor.Yellow,
        [ConsoleStyle.Blue] = KalkColor.Blue,
        [ConsoleStyle.Magenta] = KalkColor.Magenta,
        [ConsoleStyle.Cyan] = KalkColor.Cyan,
        [ConsoleStyle.White] = KalkColor.White,
        [ConsoleStyle.BrightBlack] = KalkColor.BrightBlack,
        [ConsoleStyle.BrightRed] = KalkColor.BrightRed,
        [ConsoleStyle.BrightGreen] = KalkColor.BrightGreen,
        [ConsoleStyle.BrightYellow] = KalkColor.BrightYellow,
        [ConsoleStyle.BrightBlue] = KalkColor.BrightBlue,
        [ConsoleStyle.BrightMagenta] = KalkColor.BrightMagenta,
        [ConsoleStyle.BrightCyan] = KalkColor.BrightCyan,
        [ConsoleStyle.BrightWhite] = KalkColor.BrightWhite,
    };

    private static readonly Dictionary<ConsoleStyle, KalkColor> BackgroundColors = new Dictionary<ConsoleStyle, KalkColor>
    {
        [ConsoleStyle.BackgroundBlack] = KalkColor.Black,
        [ConsoleStyle.BackgroundRed] = KalkColor.Red,
        [ConsoleStyle.BackgroundGreen] = KalkColor.Green,
        [ConsoleStyle.BackgroundYellow] = KalkColor.Yellow,
        [ConsoleStyle.BackgroundBlue] = KalkColor.Blue,
        [ConsoleStyle.BackgroundMagenta] = KalkColor.Magenta,
        [ConsoleStyle.BackgroundCyan] = KalkColor.Cyan,
        [ConsoleStyle.BackgroundWhite] = KalkColor.White,
        [ConsoleStyle.BackgroundBrightBlack] = KalkColor.BrightBlack,
        [ConsoleStyle.BackgroundBrightRed] = KalkColor.BrightRed,
        [ConsoleStyle.BackgroundBrightGreen] = KalkColor.BrightGreen,
        [ConsoleStyle.BackgroundBrightYellow] = KalkColor.BrightYellow,
        [ConsoleStyle.BackgroundBrightBlue] = KalkColor.BrightBlue,
        [ConsoleStyle.BackgroundBrightMagenta] = KalkColor.BrightMagenta,
        [ConsoleStyle.BackgroundBrightCyan] = KalkColor.BrightCyan,
        [ConsoleStyle.BackgroundBrightWhite] = KalkColor.BrightWhite,
    };

    public static List<StyledSpan> ToSpans(ConsoleText text)
    {
        List<StyledSpan> spans = new List<StyledSpan>();
        List<ConsoleStyle> activeStyles = new List<ConsoleStyle>();
        KalkTextStyle runStyle = default;
        int runStart = 0;

        for (int charIndex = 0; charIndex < text.Count; charIndex++)
        {
            List<ConsoleStyleMarker>? markers = text[charIndex].StyleMarkers;
            if (markers != null)
            {
                foreach (ConsoleStyleMarker marker in markers)
                {
                    if (marker.Enabled)
                    {
                        activeStyles.Add(marker.Style);
                    }
                    else
                    {
                        activeStyles.Remove(marker.Style);
                    }
                }
            }

            KalkTextStyle charStyle = Resolve(activeStyles);
            if (charStyle != runStyle)
            {
                AddSpan(spans, runStart, charIndex, runStyle);
                runStyle = charStyle;
                runStart = charIndex;
            }
        }

        AddSpan(spans, runStart, text.Count, runStyle);
        return spans;
    }

    private static KalkTextStyle Resolve(List<ConsoleStyle> activeStyles)
    {
        KalkColor foreground = KalkColor.Default;
        KalkColor background = KalkColor.Default;
        bool isBold = false;
        bool isUnderline = false;
        bool isReversed = false;

        // Later styles win, as in a terminal
        foreach (ConsoleStyle style in activeStyles)
        {
            if (ForegroundColors.TryGetValue(style, out KalkColor color))
            {
                foreground = color;
            }
            else if (BackgroundColors.TryGetValue(style, out color))
            {
                background = color;
            }
            else if (style.Equals(ConsoleStyle.Bold))
            {
                isBold = true;
            }
            else if (style.Equals(ConsoleStyle.Underline))
            {
                isUnderline = true;
            }
            else if (style.Equals(ConsoleStyle.Reversed))
            {
                isReversed = true;
            }
        }

        return new KalkTextStyle(foreground, background, isBold, isUnderline, isReversed);
    }

    private static void AddSpan(List<StyledSpan> spans, int start, int end, KalkTextStyle style)
    {
        if (end > start && style != default)
        {
            spans.Add(new StyledSpan(start, end - start, style));
        }
    }
}
