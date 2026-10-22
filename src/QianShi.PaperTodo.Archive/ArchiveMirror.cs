using System.Diagnostics;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// Builds and maintains the per-paper mirror that makes a host-side delete recoverable.
///
/// Why this type exists: PaperTodo has no pre-delete hook, and its delete events carry almost no
/// content. <c>PaperDeletedEvent</c> has only a snapshot (id/type/title/visibility/provider) and
/// <c>TodoDeletedEvent</c> has one row. A note body exists nowhere except in a mirror captured
/// beforehand, so every event that can change content updates the mirror, not only the archive
/// record.
/// </summary>
internal static class ArchiveMirror
{
    internal sealed record CaptureResult(PaperMirror Mirror, string? CaptureNote);

    public static bool IsTodoType(string? type) =>
        string.Equals(type, ArchiveRules.TodoPaperType, StringComparison.Ordinal);

    public static bool IsNoteType(string? type) =>
        string.Equals(type, ArchiveRules.NotePaperType, StringComparison.Ordinal);

    /// <summary>
    /// Reads the current content of one live paper. Reading never commits an editor, so the result
    /// can lag the last keystrokes; callers must be honest about that through the capture note.
    /// </summary>
    public static CaptureResult Capture(
        IPaperTodoHostApi workspace,
        PaperSnapshot paper,
        IReadOnlyList<TodoSnapshot>? todos = null)
    {
        var mirror = new PaperMirror
        {
            PaperId = paper.Id,
            PaperType = paper.Type,
            Title = paper.Title,
            BodyProviderId = paper.BodyProviderId,
            IsVisible = paper.IsVisible,
            CapturedAt = DateTimeOffset.Now
        };

        if (IsTodoType(paper.Type))
        {
            var rows = todos ?? ListTodosSafely(workspace, paper.Id);
            mirror.Todos.AddRange(rows.Select(ToRecord));
            return new CaptureResult(mirror, null);
        }

        if (!IsNoteType(paper.Type))
        {
            return new CaptureResult(mirror, null);
        }

        mirror.NoteContentAvailable = TryReadNote(workspace, paper.Id, out var content);
        mirror.NoteContent = mirror.NoteContentAvailable ? content : null;
        return new CaptureResult(
            mirror,
            NoteCaptureHint(paper.BodyProviderId, mirror.NoteContentAvailable));
    }

    /// <summary>Frozen record of one live paper, used when the host re-created a card for us.</summary>
    public static PaperMirror FromEntry(ArchiveEntry entry, string paperId) => new()
    {
        PaperId = paperId,
        PaperType = entry.PaperType,
        Title = entry.Title,
        BodyProviderId = entry.BodyProviderId,
        IsVisible = true,
        Todos = entry.Todos.Select(item => item.Clone()).ToList(),
        NoteContent = entry.NoteContent,
        NoteContentAvailable = entry.NoteContentAvailable,
        CapturedAt = DateTimeOffset.Now
    };

    public static ArchiveTodoRecord ToRecord(TodoSnapshot todo) => new()
    {
        Id = todo.Id,
        Text = todo.Text,
        Done = todo.Done,
        Order = todo.Order,
        LinkedPaperId = todo.LinkedPaperId,
        LinkedPath = todo.LinkedPath,
        LinkedPathIsDirectory = todo.LinkedPathIsDirectory,
        ReminderAt = todo.ReminderAt
    };

    public static ArchiveEntry ToTrashedEntry(
        PaperMirror mirror,
        ArchiveCaptureOrigin origin,
        string reason,
        string? captureNote)
    {
        var entry = ArchiveRules.FromMirror(mirror, ArchiveEntryState.Trashed, origin, reason);
        entry.CaptureNote = captureNote;
        return entry;
    }

    /// <summary>
    /// One row deleted on its own. The record is a Trashed record rather than an Archived one
    /// because the row really left the host: only this snapshot can bring it back.
    /// </summary>
    public static ArchiveEntry ToTrashedTodoEntry(
        TodoSnapshot todo,
        ArchiveCaptureOrigin origin,
        string reason) => new()
    {
        Kind = ArchiveEntryKind.TodoItem,
        State = ArchiveEntryState.Trashed,
        Origin = origin,
        PaperId = todo.PaperId,
        PaperType = ArchiveRules.TodoPaperType,
        BodyProviderId = string.Empty,
        PaperTitle = todo.PaperTitle,
        Title = todo.Text,
        CapturedAt = DateTimeOffset.Now,
        Todos = [ToRecord(todo)],
        NoteContentAvailable = false,
        Reason = reason
    };

    /// <summary>Refreshes only the members a PaperChanged event can carry. Returns true on change.</summary>
    public static bool ApplyPaperSnapshot(PaperMirror mirror, PaperSnapshot paper)
    {
        var changed = false;
        if (!string.Equals(mirror.PaperType, paper.Type, StringComparison.Ordinal))
        {
            mirror.PaperType = paper.Type;
            changed = true;
        }

        if (!string.Equals(mirror.Title, paper.Title, StringComparison.Ordinal))
        {
            mirror.Title = paper.Title;
            changed = true;
        }

        if (!string.Equals(mirror.BodyProviderId, paper.BodyProviderId, StringComparison.Ordinal))
        {
            mirror.BodyProviderId = paper.BodyProviderId;
            changed = true;
        }

        if (mirror.IsVisible != paper.IsVisible)
        {
            mirror.IsVisible = paper.IsVisible;
            changed = true;
        }

        if (changed)
        {
            mirror.CapturedAt = DateTimeOffset.Now;
        }

        return changed;
    }

    public static bool UpsertTodo(PaperMirror mirror, TodoSnapshot todo)
    {
        var record = ToRecord(todo);
        var index = mirror.Todos.FindIndex(item =>
            string.Equals(item.Id, todo.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            mirror.Todos.Add(record);
        }
        else if (SameTodo(mirror.Todos[index], record))
        {
            return false;
        }
        else
        {
            mirror.Todos[index] = record;
        }

        mirror.Todos = mirror.Todos.OrderBy(item => item.Order).ToList();
        mirror.CapturedAt = DateTimeOffset.Now;
        return true;
    }

    public static bool RemoveTodo(PaperMirror mirror, string todoId)
    {
        var index = mirror.Todos.FindIndex(item =>
            string.Equals(item.Id, todoId, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        mirror.Todos.RemoveAt(index);
        mirror.CapturedAt = DateTimeOffset.Now;
        return true;
    }

    /// <summary>
    /// Content comparison that ignores CapturedAt, so a repeated event does not rewrite the whole
    /// store document (the store saves on every mirror write).
    /// </summary>
    public static bool HasSameContent(PaperMirror left, PaperMirror right)
    {
        if (!string.Equals(left.PaperId, right.PaperId, StringComparison.Ordinal) ||
            !string.Equals(left.PaperType, right.PaperType, StringComparison.Ordinal) ||
            !string.Equals(left.Title, right.Title, StringComparison.Ordinal) ||
            !string.Equals(left.BodyProviderId, right.BodyProviderId, StringComparison.Ordinal) ||
            left.IsVisible != right.IsVisible ||
            left.NoteContentAvailable != right.NoteContentAvailable ||
            !string.Equals(left.NoteContent, right.NoteContent, StringComparison.Ordinal) ||
            left.Todos.Count != right.Todos.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Todos.Count; index++)
        {
            if (!SameTodo(left.Todos[index], right.Todos[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Localized limit note for a mirror captured from a live paper.</summary>
    public static string NoteCaptureHint(string? bodyProviderId, bool contentAvailable)
    {
        if (!ArchiveRules.IsBuiltinMarkdown(bodyProviderId))
        {
            // Another plugin owns this body: there is no readable or writable text channel at all.
            return ArchiveText.Get("HintNotRestorable");
        }

        // Note images are never restorable because the host exposes no image write API.
        return contentAvailable
            ? ArchiveText.Get("HintNoteImages")
            : ArchiveText.Get("HintContentPartial");
    }

    /// <summary>Localized limit note for a record frozen at delete time.</summary>
    public static string? TrashCaptureHint(PaperMirror mirror)
    {
        if (!IsNoteType(mirror.PaperType))
        {
            return null;
        }

        var hint = ArchiveText.Get("HintContentPartial");
        return mirror.NoteContentAvailable
            ? hint + " " + ArchiveText.Get("HintNoteImages")
            : hint;
    }

    private static IReadOnlyList<TodoSnapshot> ListTodosSafely(
        IPaperTodoHostApi workspace,
        string paperId)
    {
        try
        {
            return workspace.ListTodos(paperId, includeBlank: false);
        }
        catch (PaperTodoPluginException ex)
        {
            // A declared permission can still be revoked by reinstalling the plugin; an empty row
            // list keeps the card itself archivable instead of failing the whole operation.
            Trace.TraceWarning("Archive mirror could not read todos. Code={0}", ex.Code);
            return [];
        }
    }

    private static bool TryReadNote(
        IPaperTodoHostApi workspace,
        string paperId,
        out string content)
    {
        content = string.Empty;
        try
        {
            var note = workspace.GetNote(paperId);
            if (note == null || !note.ContentAvailable)
            {
                return false;
            }

            content = note.Content ?? string.Empty;
            return true;
        }
        catch (PaperTodoPluginException ex)
        {
            Trace.TraceWarning("Archive mirror could not read the note. Code={0}", ex.Code);
            return false;
        }
    }

    private static bool SameTodo(ArchiveTodoRecord left, ArchiveTodoRecord right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
        left.Done == right.Done &&
        left.Order == right.Order &&
        string.Equals(left.LinkedPaperId, right.LinkedPaperId, StringComparison.Ordinal) &&
        string.Equals(left.LinkedPath, right.LinkedPath, StringComparison.Ordinal) &&
        left.LinkedPathIsDirectory == right.LinkedPathIsDirectory &&
        left.ReminderAt == right.ReminderAt;
}
