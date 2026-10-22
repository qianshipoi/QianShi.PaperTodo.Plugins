using System.Text.Json;
using System.Text.Json.Serialization;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// What one archive record describes. A <see cref="Paper"/> record covers a whole Todo or Note
/// card; a <see cref="TodoItem"/> record covers a single Todo row that was deleted on its own.
/// </summary>
public enum ArchiveEntryKind
{
    Paper = 0,
    TodoItem = 1
}

/// <summary>
/// Lifecycle of one archive record.
/// Archived: the card still exists in PaperTodo, it is only hidden. Data is fully intact.
/// Trashed: the host deleted the content; only this snapshot remains.
/// Restored: the record was already restored and is kept for history.
/// </summary>
public enum ArchiveEntryState
{
    Archived = 0,
    Trashed = 1,
    Restored = 2
}

/// <summary>Where a captured change came from, mirrored from the host event metadata.</summary>
public enum ArchiveCaptureOrigin
{
    Unknown = 0,
    User = 1,
    Mcp = 2,
    Plugin = 3,
    System = 4,
    Import = 5
}

/// <summary>One Todo row copied out of PaperTodo. Field set matches the host TodoSnapshot.</summary>
public sealed class ArchiveTodoRecord
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
    public int Order { get; set; }
    public string? LinkedPaperId { get; set; }
    public string? LinkedPath { get; set; }
    public bool? LinkedPathIsDirectory { get; set; }
    public DateTimeOffset? ReminderAt { get; set; }

    public ArchiveTodoRecord Clone() => new()
    {
        Id = Id,
        Text = Text,
        Done = Done,
        Order = Order,
        LinkedPaperId = LinkedPaperId,
        LinkedPath = LinkedPath,
        LinkedPathIsDirectory = LinkedPathIsDirectory,
        ReminderAt = ReminderAt
    };
}

/// <summary>
/// One archive record. It freezes the content at capture time so later edits or a host delete
/// cannot change what can be restored.
/// </summary>
public sealed class ArchiveEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ArchiveEntryKind Kind { get; set; } = ArchiveEntryKind.Paper;
    public ArchiveEntryState State { get; set; } = ArchiveEntryState.Archived;
    public ArchiveCaptureOrigin Origin { get; set; } = ArchiveCaptureOrigin.Unknown;

    /// <summary>Paper id at capture time. The paper may no longer exist in the host.</summary>
    public string PaperId { get; set; } = string.Empty;

    /// <summary>Host paper type at capture time: "todo" or "note".</summary>
    public string PaperType { get; set; } = "todo";

    /// <summary>Host body provider id at capture time, for example "builtin.markdown".</summary>
    public string BodyProviderId { get; set; } = string.Empty;

    /// <summary>For a TodoItem record this is the owning paper title.</summary>
    public string PaperTitle { get; set; } = string.Empty;

    /// <summary>Display title of the record itself.</summary>
    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Set when the record moved out of the active/trash lists.</summary>
    public DateTimeOffset? RestoredAt { get; set; }

    /// <summary>Paper id the content was restored into, when a new paper had to be created.</summary>
    public string? RestoredPaperId { get; set; }

    /// <summary>Frozen Todo rows for a Todo card or for one archived/deleted Todo row.</summary>
    public List<ArchiveTodoRecord> Todos { get; set; } = [];

    /// <summary>Frozen Markdown body for a Note card that uses the built-in Markdown provider.</summary>
    public string? NoteContent { get; set; }

    /// <summary>
    /// False when the captured note body cannot be written back, for example a card owned by
    /// another plugin body provider, or content that could not be read at capture time.
    /// </summary>
    public bool NoteContentAvailable { get; set; }

    /// <summary>Extra user-facing note about capture limits, already localized.</summary>
    public string? CaptureNote { get; set; }

    /// <summary>Short localized reason, for example "deleted in PaperTodo".</summary>
    public string? Reason { get; set; }

    public ArchiveEntry Clone()
    {
        var copy = (ArchiveEntry)MemberwiseClone();
        copy.Todos = Todos.Select(item => item.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// Latest known content of one live paper. The mirror is the safety net that makes a host-side
/// delete recoverable: PaperTodo delete events never carry note body text.
/// </summary>
public sealed class PaperMirror
{
    public string PaperId { get; set; } = string.Empty;
    public string PaperType { get; set; } = "todo";
    public string Title { get; set; } = string.Empty;
    public string BodyProviderId { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public List<ArchiveTodoRecord> Todos { get; set; } = [];
    public string? NoteContent { get; set; }
    public bool NoteContentAvailable { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;

    public PaperMirror Clone()
    {
        var copy = (PaperMirror)MemberwiseClone();
        copy.Todos = Todos.Select(item => item.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// On-disk document for the plugin-owned store at &lt;plugin&gt;/.runtime/archive-store.json.
/// It is not host-managed state: the plugin owns versioning, atomic writes and recovery.
/// </summary>
public sealed class ArchiveDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<ArchiveEntry> Entries { get; set; } = [];

    public Dictionary<string, PaperMirror> Mirrors { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, string> ArchivedPaperIndex { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>Counting summary used by the management surface.</summary>
public sealed record ArchiveStatistics(
    int Archived,
    int Trashed,
    int Restored,
    int MirroredPapers);

/// <summary>Automatic cleanup limits. Archived cards are never pruned automatically.</summary>
public sealed record ArchiveRetention(int RetentionDays, int MaxEntries, bool KeepRestored);

/// <summary>Pure helpers shared by the store, the operations and the management surface.</summary>
public static class ArchiveRules
{
    public const string TodoPaperType = "todo";
    public const string NotePaperType = "note";
    public const string BuiltinMarkdownProviderId = "builtin.markdown";

    /// <summary>True when the record can still be turned back into live PaperTodo content.</summary>
    public static bool IsRestorable(ArchiveEntry entry)
    {
        if (entry.State == ArchiveEntryState.Restored)
        {
            return false;
        }

        if (entry.Kind == ArchiveEntryKind.TodoItem)
        {
            return true;
        }

        if (string.Equals(entry.PaperType, NotePaperType, StringComparison.Ordinal))
        {
            // A note body kept by another plugin provider has no readable or writable text channel
            // for this plugin, so it must not pretend to be restorable.
            return IsBuiltinMarkdown(entry.BodyProviderId) && entry.NoteContentAvailable;
        }

        return true;
    }

    public static bool IsBuiltinMarkdown(string? bodyProviderId) =>
        string.IsNullOrEmpty(bodyProviderId) ||
        string.Equals(bodyProviderId, BuiltinMarkdownProviderId, StringComparison.Ordinal);

    /// <summary>Builds the frozen record of one live paper.</summary>
    public static ArchiveEntry FromMirror(
        PaperMirror mirror,
        ArchiveEntryState state,
        ArchiveCaptureOrigin origin,
        string? reason,
        DateTimeOffset? capturedAt = null)
    {
        var isNote = string.Equals(mirror.PaperType, NotePaperType, StringComparison.Ordinal);
        return new ArchiveEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = ArchiveEntryKind.Paper,
            State = state,
            Origin = origin,
            PaperId = mirror.PaperId,
            PaperType = mirror.PaperType,
            BodyProviderId = mirror.BodyProviderId,
            PaperTitle = mirror.Title,
            Title = string.IsNullOrWhiteSpace(mirror.Title) ? mirror.PaperType : mirror.Title,
            CapturedAt = capturedAt ?? DateTimeOffset.Now,
            Todos = mirror.Todos.Select(item => item.Clone()).ToList(),
            NoteContent = isNote ? mirror.NoteContent : null,
            NoteContentAvailable = isNote && mirror.NoteContentAvailable,
            Reason = reason
        };
    }

    /// <summary>Human readable one-line summary of what a record holds.</summary>
    public static string SummarizeContent(ArchiveEntry entry, Func<string, string> text)
    {
        if (entry.Kind == ArchiveEntryKind.TodoItem)
        {
            return entry.Todos.Count > 0 ? entry.Todos[0].Text : entry.Title;
        }

        if (string.Equals(entry.PaperType, NotePaperType, StringComparison.Ordinal))
        {
            var length = entry.NoteContent?.Length ?? 0;
            return text("ContentNoteLength").Replace("{0}", length.ToString(), StringComparison.Ordinal);
        }

        var done = entry.Todos.Count(item => item.Done);
        return text("ContentTodoCount")
            .Replace("{0}", entry.Todos.Count.ToString(), StringComparison.Ordinal)
            .Replace("{1}", done.ToString(), StringComparison.Ordinal);
    }
}
