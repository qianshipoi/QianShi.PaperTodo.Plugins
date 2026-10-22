using System.Windows;
using System.Windows.Controls;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// Builds one record row of the archive list. Rows are disposable WPF trees: the session rebuilds
/// them from the repository snapshot whenever the archive changes, so a row never holds state.
/// </summary>
internal static class ArchiveCenterRowBuilder
{
    public static Border Build(
        ArchiveEntry entry,
        ArchiveCenterTheme theme,
        bool canOperate,
        Action<ArchiveEntry> requestRestore,
        Action<ArchiveEntry> requestPurge)
    {
        var isNote = string.Equals(entry.PaperType, ArchiveRules.NotePaperType, StringComparison.Ordinal);
        var restorable = ArchiveRules.IsRestorable(entry);
        var title = DisplayTitle(entry);

        var badgeRow = new WrapPanel();
        badgeRow.Children.Add(ArchiveUiControls.Badge(
            isNote ? ArchiveText.Get("BadgeNote") : ArchiveText.Get("BadgeTodo"),
            theme,
            ArchiveBadgeTone.Neutral));
        badgeRow.Children.Add(ArchiveUiControls.Badge(StateLabel(entry), theme, StateTone(entry)));

        var titleText = new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Foreground = theme.Text,
            FontFamily = theme.Font,
            FontSize = theme.FontSize(13),
            Margin = new Thickness(0, 5, 0, 0)
        };

        var content = new StackPanel();
        content.Children.Add(badgeRow);
        content.Children.Add(titleText);

        var summary = ArchiveRules.SummarizeContent(entry, ArchiveText.Get);
        if (!string.IsNullOrWhiteSpace(summary) &&
            !string.Equals(summary, title, StringComparison.Ordinal))
        {
            content.Children.Add(new TextBlock
            {
                Text = summary,
                TextWrapping = TextWrapping.Wrap,
                Foreground = theme.Weak,
                FontFamily = theme.Font,
                FontSize = theme.FontSize(11),
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        content.Children.Add(BuildMeta(entry, theme));

        if (!string.IsNullOrWhiteSpace(entry.CaptureNote))
        {
            content.Children.Add(ArchiveUiControls.Hint(entry.CaptureNote!, theme));
        }

        if (isNote)
        {
            content.Children.Add(ArchiveUiControls.Hint(ArchiveText.Get("HintNoteImages"), theme));
        }

        if (!restorable && entry.State != ArchiveEntryState.Restored)
        {
            content.Children.Add(ArchiveUiControls.Hint(
                ArchiveText.Get("HintNotRestorable"),
                theme,
                theme.Danger));
        }

        var actions = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var restoreButton = ArchiveUiControls.CreateActionButton(ArchiveText.Get("ButtonRestore"), theme);
        restoreButton.IsEnabled = canOperate && restorable;
        if (!restorable)
        {
            restoreButton.ToolTip = ArchiveText.Get("HintNotRestorable");
        }

        restoreButton.Click += (_, _) => requestRestore(entry);
        actions.Children.Add(restoreButton);

        var purgeButton = ArchiveUiControls.CreateActionButton(ArchiveText.Get("ButtonPurge"), theme, danger: true);
        purgeButton.IsEnabled = canOperate;
        purgeButton.Click += (_, _) => requestPurge(entry);
        actions.Children.Add(purgeButton);

        content.Children.Add(actions);

        return new Border
        {
            Child = content,
            Background = theme.Surface,
            BorderBrush = theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 0, 2, 7)
        };
    }

    /// <summary>Title shown for a record, falling back to frozen content and finally to a label.</summary>
    public static string DisplayTitle(ArchiveEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Title))
        {
            return entry.Title;
        }

        if (entry.Kind == ArchiveEntryKind.TodoItem &&
            entry.Todos.Count > 0 &&
            !string.IsNullOrWhiteSpace(entry.Todos[0].Text))
        {
            return entry.Todos[0].Text;
        }

        if (!string.IsNullOrWhiteSpace(entry.PaperTitle))
        {
            return entry.PaperTitle;
        }

        return ArchiveText.Get("Untitled");
    }

    private static TextBlock BuildMeta(ArchiveEntry entry, ArchiveCenterTheme theme)
    {
        var separator = ArchiveText.Get("MetaSeparator");
        var parts = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(entry.PaperTitle) &&
            !string.Equals(entry.PaperTitle, entry.Title, StringComparison.Ordinal))
        {
            parts.Add(ArchiveText.Format("FromPaper", entry.PaperTitle));
        }

        parts.Add(ArchiveText.Format("CapturedAt", FormatWhen(entry.CapturedAt)));
        if (entry.RestoredAt.HasValue)
        {
            parts.Add(ArchiveText.Format("RestoredAt", FormatWhen(entry.RestoredAt.Value)));
        }

        parts.Add(ArchiveText.ForOrigin(entry.Origin));

        if (!string.IsNullOrWhiteSpace(entry.Reason))
        {
            parts.Add(entry.Reason!);
        }

        return new TextBlock
        {
            Text = string.Join(separator, parts),
            TextWrapping = TextWrapping.Wrap,
            Foreground = theme.Weak,
            FontFamily = theme.Font,
            FontSize = theme.FontSize(10.5),
            Margin = new Thickness(0, 5, 0, 0)
        };
    }

    private static string StateLabel(ArchiveEntry entry) => entry.State switch
    {
        ArchiveEntryState.Trashed => ArchiveText.Get("BadgeTrashed"),
        ArchiveEntryState.Restored => ArchiveText.Get("BadgeRestored"),
        _ => ArchiveText.Get("BadgeArchived")
    };

    private static ArchiveBadgeTone StateTone(ArchiveEntry entry) => entry.State switch
    {
        ArchiveEntryState.Trashed => ArchiveBadgeTone.Danger,
        ArchiveEntryState.Restored => ArchiveBadgeTone.Muted,
        _ => ArchiveBadgeTone.Accent
    };

    private static string FormatWhen(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
