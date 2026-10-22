using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QianShi.PaperTodo.Archive;

/// <summary>Visual emphasis of a record badge.</summary>
internal enum ArchiveBadgeTone
{
    Neutral,
    Accent,
    Muted,
    Danger
}

/// <summary>
/// Plain WPF factories shared by the Archive Center card. They only build controls; every colour
/// and font is supplied by <see cref="ArchiveCenterTheme"/> so a live theme change can restyle the
/// very same instances instead of rebuilding them.
/// </summary>
internal static class ArchiveUiControls
{
    private const double ActionFontSize = 11.5;
    private const double ChipFontSize = 11;
    private const double BadgeFontSize = 9.5;
    private const double OpacityWhenDisabled = 0.5;

    public static Button CreateActionButton(string text, ArchiveCenterTheme theme, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 6),
            MinHeight = 28,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        WatchInteractionOpacity(button);
        StyleActionButton(button, theme, danger);
        return button;
    }

    public static void StyleActionButton(Button button, ArchiveCenterTheme theme, bool danger)
    {
        button.Foreground = danger ? theme.Danger : theme.Text;
        button.Background = danger ? theme.DangerSurface : theme.Surface;
        button.BorderBrush = danger ? theme.Danger : theme.Border;
        button.FontFamily = theme.Font;
        button.FontSize = theme.FontSize(ActionFontSize);
    }

    /// <summary>A filter chip: same control as an action button, tuned for the filter strip.</summary>
    public static Button CreateChip(string text)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 6),
            MinHeight = 26,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        WatchInteractionOpacity(button);
        return button;
    }

    public static void StyleChip(Button button, ArchiveCenterTheme theme, bool selected)
    {
        button.Foreground = selected ? theme.Accent : theme.Weak;
        button.Background = selected ? theme.AccentSurface : theme.Surface;
        button.BorderBrush = selected ? theme.Accent : theme.Border;
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        button.FontFamily = theme.Font;
        button.FontSize = theme.FontSize(ChipFontSize);
    }

    /// <summary>Small rounded label used for the record type and its lifecycle state.</summary>
    public static Border Badge(string text, ArchiveCenterTheme theme, ArchiveBadgeTone tone)
    {
        SolidColorBrush foreground;
        SolidColorBrush background;
        SolidColorBrush border;
        switch (tone)
        {
            case ArchiveBadgeTone.Accent:
                foreground = theme.Accent;
                background = theme.AccentSurface;
                border = theme.Accent;
                break;
            case ArchiveBadgeTone.Danger:
                foreground = theme.Danger;
                background = theme.DangerSurface;
                border = theme.Danger;
                break;
            case ArchiveBadgeTone.Muted:
                foreground = theme.Weak;
                background = theme.Surface;
                border = theme.Border;
                break;
            default:
                foreground = theme.Text;
                background = theme.SurfaceRaised;
                border = theme.Border;
                break;
        }

        return new Border
        {
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontFamily = theme.Font,
                FontSize = theme.FontSize(BadgeFontSize)
            },
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(0, 0, 5, 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    /// <summary>Secondary explanatory line; notes and limits use the weak text colour by default.</summary>
    public static TextBlock Hint(string text, ArchiveCenterTheme theme, SolidColorBrush? brush = null) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = brush ?? theme.Weak,
        FontFamily = theme.Font,
        FontSize = theme.FontSize(10.5),
        Margin = new Thickness(0, 4, 0, 0)
    };

    /// <summary>
    /// A vertically scrolling, virtualized list with no selection chrome: the Archive Center rows
    /// are cards, not selectable data rows.
    /// </summary>
    public static ListBox CreateVirtualizedList()
    {
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);

        var itemRoot = new FrameworkElementFactory(typeof(Border));
        itemRoot.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        itemRoot.AppendChild(presenter);

        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        itemStyle.Setters.Add(new Setter(KeyboardNavigation.IsTabStopProperty, false));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(
            Control.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(
            Control.TemplateProperty,
            new ControlTemplate(typeof(ListBoxItem)) { VisualTree = itemRoot }));
        list.ItemContainerStyle = itemStyle;
        list.ItemsPanel = new ItemsPanelTemplate(
            new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        return list;
    }

    /// <summary>Cheap hover/press feedback that cannot fight a custom theme brush.</summary>
    private static void WatchInteractionOpacity(Button button)
    {
        button.MouseEnter += (_, _) => button.Opacity = button.IsEnabled ? 0.82 : OpacityWhenDisabled;
        button.MouseLeave += (_, _) => button.Opacity = button.IsEnabled ? 1.0 : OpacityWhenDisabled;
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1.0 : OpacityWhenDisabled;
    }
}
