using System.Windows;
using System.Windows.Media;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// Design tokens derived from the host paper theme. The Archive Center is a real paper surface, so
/// no widget carries a fixed colour: every background, border, text and font follows
/// <see cref="PaperBodyTheme"/> and is rebuilt when the host reports a theme or typography change.
/// </summary>
internal sealed class ArchiveCenterTheme
{
    private ArchiveCenterTheme(PaperBodyTheme source)
    {
        var scale = source.FontScale;
        Scale = double.IsFinite(scale) ? Math.Clamp(scale, 0.7, 2.5) : 1.0;

        Text = Parse(source.TextColor, "#202020");
        Weak = Parse(source.WeakTextColor, "#707070");
        Border = Parse(source.BorderColor, "#807050");
        Accent = Parse(source.AccentColor, "#B07A31");
        Paper = Parse(source.PaperColor, source.IsDark ? "#FF1E1E1E" : "#FFFFFFFF");
        if (Paper.Color.A < 128)
        {
            // A translucent paper colour would let the dimmed card show through the confirmation
            // panel, so that one surface always resolves to an opaque variant.
            Paper = Parse(source.IsDark ? "#FF1E1E1E" : "#FFFFFFFF", "#FFFFFFFF");
        }
        Surface = Parse(source.IsDark ? "#18FFFFFF" : "#0C000000", "#0C000000");
        SurfaceRaised = Parse(source.IsDark ? "#26FFFFFF" : "#14000000", "#14000000");
        AccentSurface = FromColor(WithAlpha(Accent.Color, source.IsDark ? (byte)54 : (byte)28));

        // The host theme has no danger token, so a muted signal red is the one intentional fixed
        // pair here. Both variants keep contrast against dark and light paper.
        Danger = Parse(source.IsDark ? "#FFD97B72" : "#FFC44B42", "#FFC44B42");
        DangerSurface = Parse(source.IsDark ? "#28D06A5F" : "#18C44B42", "#18C44B42");
        Overlay = Parse(source.IsDark ? "#B3000000" : "#73000000", "#73000000");

        Font = ResolveFont(source.FontFamily);
    }

    /// <summary>Clamped host font scale. Every font size in the card is expressed through it.</summary>
    public double Scale { get; }

    public SolidColorBrush Text { get; }

    public SolidColorBrush Weak { get; }

    public SolidColorBrush Border { get; }

    public SolidColorBrush Accent { get; }

    /// <summary>Opaque paper colour, used by the confirmation panel that floats above the card.</summary>
    public SolidColorBrush Paper { get; }

    /// <summary>Low contrast fill for rows, cards and inputs.</summary>
    public SolidColorBrush Surface { get; }

    /// <summary>Slightly stronger fill, used by neutral badges.</summary>
    public SolidColorBrush SurfaceRaised { get; }

    public SolidColorBrush AccentSurface { get; }

    public SolidColorBrush Danger { get; }

    public SolidColorBrush DangerSurface { get; }

    /// <summary>Backdrop of the inline confirmation overlay.</summary>
    public SolidColorBrush Overlay { get; }

    public FontFamily Font { get; }

    public static ArchiveCenterTheme From(PaperBodyTheme theme) => new(theme);

    /// <summary>Scales one of the card's base font sizes by the host text zoom.</summary>
    public double FontSize(double baseSize) => baseSize * Scale;

    private static FontFamily ResolveFont(string? family)
    {
        if (!string.IsNullOrWhiteSpace(family))
        {
            try
            {
                return new FontFamily(family);
            }
            catch (ArgumentException)
            {
                // Malformed host font name: fall through to the shell font.
            }
        }

        return SystemFonts.MessageFontFamily;
    }

    private static SolidColorBrush Parse(string? value, string fallback)
    {
        if (TryColor(value, out var color) || TryColor(fallback, out color))
        {
            return FromColor(color);
        }

        return FromColor(Colors.Gray);
    }

    private static bool TryColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(value) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
            // Host supplied an unparsable colour; caller uses its fallback.
        }
        catch (NotSupportedException)
        {
            // Same as above, for converters that reject the input shape.
        }

        return false;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static SolidColorBrush FromColor(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
