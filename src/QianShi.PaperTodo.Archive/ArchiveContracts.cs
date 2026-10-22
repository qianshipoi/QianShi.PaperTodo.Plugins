namespace QianShi.PaperTodo.Archive;

/// <summary>
/// Everything an archive operation needs from one host surface. The provider Runtime and the
/// management card body have different lifetimes and different host objects, but archive business
/// behavior must stay identical, so both build one of these and hand it to
/// <see cref="ArchiveService"/>.
/// </summary>
public sealed class ArchiveHostContext
{
    /// <summary>This plugin's id, which is also the BodyProviderId of its own cards.</summary>
    public required string ProviderId { get; init; }

    public required IPaperTodoHostApi Workspace { get; init; }

    public required IPaperWorkspacePresentationApi Presentation { get; init; }

    public required IArchiveRepository Repository { get; init; }

    /// <summary>Read on demand so a settings change needs no rebuild.</summary>
    public required Func<ArchiveSettings> Settings { get; init; }

    /// <summary>
    /// Paper id that hosts the calling body session, if any. A session must not treat its own
    /// paper as archivable content.
    /// </summary>
    public string? HostPaperId { get; init; }

    /// <summary>Raised after an operation changed the store, so a surface can refresh.</summary>
    public Action? StateChanged { get; init; }
}

/// <summary>What changed inside the plugin-owned store.</summary>
public enum ArchiveChangeKind
{
    Reloaded = 0,
    EntryAdded = 1,
    EntryUpdated = 2,
    EntryRemoved = 3,
    MirrorsChanged = 4
}

/// <summary>
/// Plugin-owned archive storage. It is the single authority for archive records and content
/// mirrors; the host never provides a business recovery system for plugins, so this plugin owns
/// format versioning, atomic writes and its own recovery.
/// </summary>
public interface IArchiveRepository
{
    /// <summary>Raised after a mutation is applied in memory. May be raised on any thread.</summary>
    event Action<ArchiveChangeKind>? Changed;

    /// <summary>Absolute path of the backing file, shown in the management surface.</summary>
    string StoragePath { get; }

    /// <summary>Last localized read/write failure, or null.</summary>
    string? LastError { get; }

    void Load();

    /// <summary>Newest first.</summary>
    IReadOnlyList<ArchiveEntry> Snapshot();

    ArchiveEntry? Find(string entryId);

    /// <summary>Entry id currently hiding that paper, or null.</summary>
    string? FindEntryIdByArchivedPaper(string paperId);

    PaperMirror? FindMirror(string paperId);

    IReadOnlyList<PaperMirror> SnapshotMirrors();

    void Upsert(ArchiveEntry entry, ArchiveChangeKind kind = ArchiveChangeKind.EntryUpdated);

    void Remove(string entryId);

    void SetMirror(PaperMirror mirror);

    void RemoveMirror(string paperId);

    void ClearMirrors();

    /// <summary>Queues a debounced write; immediate forces a synchronous flush.</summary>
    void Save(bool immediate = false);

    void ApplyRetention(ArchiveRetention retention);

    ArchiveStatistics Statistics();
}

/// <summary>
/// User-intent operations shared by the provider Runtime and the management surface. Every method
/// is safe to call from the UI thread; the implementation marshals host calls as the host requires.
/// </summary>
public interface IArchiveOperations
{
    ArchiveOperationResult ArchivePaper(string paperId);

    ArchiveOperationResult ArchiveTodoItem(string paperId, string todoId);

    ArchiveOperationResult RestoreEntry(string entryId);

    ArchiveOperationResult PurgeEntry(string entryId);

    ArchiveOperationResult RestoreAll();

    ArchiveOperationResult OpenArchiveCenter();

    bool IsArchived(string paperId);
}

/// <summary>
/// Outcome of one archive operation. <paramref name="Code"/> is a stable identifier in
/// <see cref="ArchiveResultCodes"/>; the management surface localizes it through
/// <see cref="ArchiveText.ForResult"/> instead of showing raw text.
/// </summary>
public sealed record ArchiveOperationResult(bool Ok, string Code, string? Detail = null)
{
    public static ArchiveOperationResult Success(string code, string? detail = null) =>
        new(true, code, detail);

    public static ArchiveOperationResult Failure(string code, string? detail = null) =>
        new(false, code, detail);
}

/// <summary>Stable result codes. Every code has a text entry in <see cref="ArchiveText"/>.</summary>
public static class ArchiveResultCodes
{
    public const string ArchivePaper = "ok.archive.paper";
    public const string ArchiveTodoItem = "ok.archive.todo";
    public const string Restore = "ok.restore";
    public const string RestoreAll = "ok.restoreAll";
    public const string Purge = "ok.purge";
    public const string OpenCenter = "ok.openCenter";

    public const string AlreadyArchived = "err.already_archived";
    public const string NotArchived = "err.not_archived";
    public const string NotFound = "err.not_found";
    public const string SelfPaper = "err.self_paper";
    public const string PluginPaper = "err.plugin_paper";
    public const string PermissionDenied = "err.permission";
    public const string NotRestorable = "err.not_restorable";
    public const string ContentMissing = "err.content_missing";
    public const string CreateFailed = "err.create_failed";
    public const string PresentationUnavailable = "err.presentation_unavailable";
    public const string NothingToDo = "err.nothing_to_do";
    public const string Storage = "err.storage";
    public const string Unknown = "err.unknown";
}

/// <summary>How the management surface should show a record.</summary>
public enum ArchiveListFilter
{
    All = 0,
    Archived = 1,
    Trashed = 2,
    Restored = 3
}
