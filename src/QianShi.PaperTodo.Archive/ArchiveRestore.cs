using System.Diagnostics;
using System.Globalization;

namespace QianShi.PaperTodo.Archive;

/// <summary>Outcome of rebuilding PaperTodo content from a frozen archive record.</summary>
internal sealed record ArchiveRestoreOutcome(string? PaperId, string? Detail, string? FailureCode)
{
    public bool Ok => PaperId is not null;

    public static ArchiveRestoreOutcome Restored(string paperId, string? detail = null) =>
        new(paperId, detail, null);

    public static ArchiveRestoreOutcome Failed(string failureCode, string? detail = null) =>
        new(null, detail, failureCode);
}

/// <summary>
/// Turns a frozen <see cref="ArchiveEntry"/> back into live PaperTodo content.
///
/// Restoring is a best-effort write with three host constraints baked in:
/// every value is re-validated against the *current* host state (links, reminders, lengths)
/// because the host rejects the whole request otherwise; a rejected title is dropped rather than
/// failing the content restore; and large todo cards are split because one request may carry at
/// most <see cref="MaximumTodosPerRequest"/> rows.
/// </summary>
internal static class ArchiveRestore
{
    /// <summary>Mirrors PaperWindow.NoteTextMaxLength, which the host enforces before writing.</summary>
    private const int MaximumNoteTextLength = 100000;

    /// <summary>Mirrors PaperWindow.TodoTextMaxLength.</summary>
    private const int MaximumTodoTextLength = 5000;

    /// <summary>Mirrors PaperWindow.MaxPastedTodoLines, the host cap for one request.</summary>
    private const int MaximumTodosPerRequest = 200;

    /// <summary>Mirrors PaperTitles.MaxTitleLength; the configured limit can still be smaller.</summary>
    private const int MaximumTitleLength = 20;

    public static ArchiveRestoreOutcome Recreate(
        IPaperTodoHostApi workspace,
        ArchiveEntry entry,
        IReadOnlySet<string> livePaperIds)
    {
        var warnings = new List<string>();
        var title = TrimToTextElements(entry.Title, MaximumTitleLength);

        if (ArchiveMirror.IsNoteType(entry.PaperType))
        {
            return RecreateNote(workspace, entry, title, warnings);
        }

        try
        {
            return RecreateTodoCard(
                workspace,
                entry,
                title,
                livePaperIds,
                allowLinks: true,
                allowReminders: true,
                warnings);
        }
        catch (PaperTodoPluginException ex) when (ex.Code is "paper_links_disabled" or "reminders_disabled")
        {
            // Host settings can forbid links or reminders entirely. The record is still valuable
            // without the offending field, and nothing was created yet: the host validates the
            // request before mutating state.
            warnings.Add(ArchiveText.Get("HintContentPartial"));
            return RecreateTodoCard(
                workspace,
                entry,
                title,
                livePaperIds,
                allowLinks: ex.Code != "paper_links_disabled",
                allowReminders: ex.Code != "reminders_disabled",
                warnings);
        }
    }

    /// <summary>Re-creates a minimal todo card for a single archived row whose parent is gone.</summary>
    public static ArchiveRestoreOutcome RecreateTodoRow(
        IPaperTodoHostApi workspace,
        ArchiveEntry entry,
        ArchiveTodoRecord row,
        IReadOnlySet<string> livePaperIds)
    {
        var warnings = new List<string>();
        var title = TrimToTextElements(entry.PaperTitle, MaximumTitleLength);
        if (title.Length == 0)
        {
            title = TrimToTextElements(entry.Title, MaximumTitleLength);
        }

        var item = BuildTodoItem(row, livePaperIds, targetPaperId: null, out _, allowLinks: true, allowReminders: true, warnings);
        var created = CreatePaper(workspace, new CreatePaperRequest
        {
            Type = ArchiveRules.TodoPaperType,
            Title = title.Length == 0 ? null : title,
            Show = true,
            Todos = [item]
        });

        return ArchiveRestoreOutcome.Restored(created.PaperId, JoinWarnings(warnings));
    }

    public static TodoCreateItem BuildTodoItem(
        ArchiveTodoRecord row,
        IReadOnlySet<string> livePaperIds,
        string? targetPaperId,
        out bool clearedLink,
        bool allowLinks = true,
        bool allowReminders = true,
        List<string>? warnings = null)
    {
        var text = row.Text ?? string.Empty;
        if (text.Length > MaximumTodoTextLength)
        {
            text = text[..MaximumTodoTextLength];
            warnings?.Add(ArchiveText.Get("HintContentPartial"));
        }

        var linkedPaperId = row.LinkedPaperId;
        if (!string.IsNullOrWhiteSpace(linkedPaperId) &&
            (!allowLinks ||
             string.Equals(linkedPaperId, targetPaperId, StringComparison.Ordinal) ||
             !livePaperIds.Contains(linkedPaperId)))
        {
            // Restoring a card creates a new paper id, so an old link may dangle. The host rejects
            // any unknown or self link, which would fail the whole restore.
            linkedPaperId = null;
        }

        clearedLink = !string.Equals(linkedPaperId, row.LinkedPaperId, StringComparison.Ordinal);

        var reminder = allowReminders ? row.ReminderAt : null;
        if (row.Done || reminder is null || reminder <= DateTimeOffset.Now)
        {
            // The host rejects a completed row that starts with a reminder and any past reminder.
            reminder = null;
        }

        return new TodoCreateItem
        {
            Text = text,
            Done = row.Done,
            LinkedPaperId = string.IsNullOrWhiteSpace(linkedPaperId) ? null : linkedPaperId,
            ReminderAt = reminder
        };
    }

    private static ArchiveRestoreOutcome RecreateNote(
        IPaperTodoHostApi workspace,
        ArchiveEntry entry,
        string title,
        List<string> warnings)
    {
        var content = entry.NoteContent ?? string.Empty;
        if (content.Length > MaximumNoteTextLength)
        {
            content = content[..MaximumNoteTextLength];
            warnings.Add(ArchiveText.Get("HintContentPartial"));
        }

        var created = CreatePaper(workspace, new CreatePaperRequest
        {
            Type = ArchiveRules.NotePaperType,
            Title = title.Length == 0 ? null : title,
            Show = true,
            Content = content
        });

        return ArchiveRestoreOutcome.Restored(created.PaperId, JoinWarnings(warnings));
    }

    private static ArchiveRestoreOutcome RecreateTodoCard(
        IPaperTodoHostApi workspace,
        ArchiveEntry entry,
        string title,
        IReadOnlySet<string> livePaperIds,
        bool allowLinks,
        bool allowReminders,
        List<string> warnings)
    {
        var items = new List<TodoCreateItem>(entry.Todos.Count);
        foreach (var row in entry.Todos)
        {
            if (string.IsNullOrWhiteSpace(row.Text))
            {
                // The host rejects blank rows and mirrors never contain them, but a hand-edited or
                // older record must not break the whole restore.
                continue;
            }

            items.Add(BuildTodoItem(
                row,
                livePaperIds,
                targetPaperId: null,
                out _,
                allowLinks,
                allowReminders,
                warnings));
        }

        if (entry.Todos.Count > 0 && items.Count == 0)
        {
            // Every frozen row was blank: creating an empty card would hide that the content is gone.
            return ArchiveRestoreOutcome.Failed(
                ArchiveResultCodes.ContentMissing,
                ArchiveText.Get("HintContentPartial"));
        }

        var created = CreatePaper(workspace, new CreatePaperRequest
        {
            Type = ArchiveRules.TodoPaperType,
            Title = title.Length == 0 ? null : title,
            Show = true,
            Todos = items.Take(MaximumTodosPerRequest).ToArray()
        });

        for (var offset = MaximumTodosPerRequest; offset < items.Count; offset += MaximumTodosPerRequest)
        {
            var batch = items.Skip(offset).Take(MaximumTodosPerRequest).ToArray();
            try
            {
                workspace.AppendTodos(new AppendTodosRequest
                {
                    PaperId = created.PaperId,
                    Todos = batch
                });
            }
            catch (PaperTodoPluginException ex)
            {
                // The card already exists with the earlier rows; report a partial restore instead of
                // failing and leaving the user without any card at all.
                Trace.TraceWarning(
                    "Archive restore appended only part of a large todo card. Code={0}",
                    ex.Code);
                warnings.Add(ArchiveText.Get("HintContentPartial"));
                break;
            }
        }

        return ArchiveRestoreOutcome.Restored(created.PaperId, JoinWarnings(warnings));
    }

    /// <summary>
    /// Creates the paper. The configured title limit can be lower than it was at capture time, and
    /// the host validates titles strictly, so a rejected title is dropped instead of losing content.
    /// </summary>
    private static PaperMutationResult CreatePaper(
        IPaperTodoHostApi workspace,
        CreatePaperRequest request)
    {
        try
        {
            return workspace.CreatePaper(request);
        }
        catch (PaperTodoPluginException ex) when (
            ex.Code == "invalid_params" && !string.IsNullOrEmpty(request.Title))
        {
            Trace.TraceWarning("Archive restore dropped a title PaperTodo rejected.");
            return workspace.CreatePaper(request with { Title = null });
        }
    }

    private static string TrimToTextElements(string? value, int maximum)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || maximum <= 0)
        {
            return string.Empty;
        }

        try
        {
            var info = new StringInfo(text);
            return info.LengthInTextElements <= maximum
                ? text
                : info.SubstringByTextElements(0, maximum);
        }
        catch (ArgumentException)
        {
            return text.Length <= maximum ? text : text[..maximum];
        }
    }

    private static string? JoinWarnings(List<string> warnings) =>
        warnings.Count == 0
            ? null
            : string.Join(" ", warnings.Distinct(StringComparer.Ordinal));
}
