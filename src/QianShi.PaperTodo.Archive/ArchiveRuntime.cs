using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// The one provider-level Runtime. It owns three jobs:
/// keep a mirror of every live card so a host-side delete can still be rescued ("PaperTodo has no
/// pre-delete hook", and delete events carry no note body); turn those mirrors into trash records;
/// and contribute the archive entry points (global top bar, per-paper context menu, Todo row menu).
///
/// All contributions are volatile: they are rebuilt from <see cref="ArchiveSettings"/> and the store
/// on every relevant change, and PaperTodo removes them automatically when this Runtime ends.
/// </summary>
internal sealed class ArchiveRuntime : IPaperPluginRuntime
{
    private const string TopBarArchiveActionId = "archive.paper";
    private const string TopBarOpenCenterActionId = "archive.center";
    private const string PaperActionArchiveId = "archive.paper";
    private const string PaperActionUnarchiveId = "archive.unarchive";
    private const string PaperActionOpenCenterId = "archive.center";
    private const string TodoActionArchiveId = "archive.todo";

    // Character icons render through the host's own top-bar pipeline (host owns theme/DPI/size).
    private const string ArchiveGlyph = "🗄";
    private const string CenterGlyph = "🗂";

    private static readonly TimeSpan NoteRefreshDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PendingTodoLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockSweepDelay = TimeSpan.FromMilliseconds(200);
    private const int MaximumPendingTodoPapers = 64;

    private readonly PaperPluginRuntimeContext _context;
    private readonly IPaperTodoHostApi _workspace;
    private readonly IPaperWorkspacePresentationApi _presentation;
    private readonly IPaperGlobalTopBarApi _globalTopBar;
    private readonly IPaperPluginPaperActions? _paperActions;
    private readonly IPaperPluginRuntimeTodoActions? _todoActions;
    private readonly IArchiveRepository _store;
    private readonly ArchiveService _service;
    private readonly PaperTodoAction[] _todoRowActions;
    private readonly object _gate = new();
    private readonly HashSet<string> _pendingNotePapers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingTodoRows> _pendingDeletedTodos =
        new(StringComparer.Ordinal);
    private readonly IDisposable _settingsSubscription;
    private readonly IDisposable _workspaceSubscription;
    private System.Threading.Timer? _noteTimer;
    private DispatcherTimer? _lockSweepTimer;
    private ArchiveSettings _settings;
    private volatile bool _disposed;
    private bool _rebuildScheduled;
    private bool _lockSweepRunning;

    public ArchiveRuntime(PaperPluginRuntimeContext context)
        : this(context, ArchiveStore.Shared)
    {
    }

    /// <summary>
    /// Test seam: the same Runtime over an isolated store, so the verification harness can exercise
    /// contribution registration and event capture without writing into the installed plugin folder.
    /// </summary>
    internal ArchiveRuntime(PaperPluginRuntimeContext context, IArchiveRepository repository)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);
        _context = context;
        ArchiveText.Configure(context.UiLanguage);

        _workspace = context.Workspace;
        _globalTopBar = context.GlobalTopBar;
        _paperActions = context.Workspace as IPaperPluginPaperActions;
        _todoActions = context.Workspace as IPaperPluginRuntimeTodoActions;
        _todoRowActions = CreateTodoRowActions();

        _store = repository;
        _store.Load();
        _settings = ArchiveSettings.Parse(context.Settings.Json);
        _store.ApplyRetention(_settings.ToRetention());

        // The management card body and this Runtime share one service so archive behavior cannot
        // drift between the two surfaces.
        // Protocol 2.2 cross-paper presentation is the non-destructive hide primitive, and it is
        // also the only way to re-hide a card the host revealed behind the plugin's back.
        _presentation = context.WorkspacePresentation;
        _service = new ArchiveService(new ArchiveHostContext
        {
            ProviderId = context.ProviderId,
            Workspace = _workspace,
            Presentation = _presentation,
            Repository = _store,
            Settings = () => _settings,
            HostPaperId = null
        });

        // Both surfaces write the same store, so contributions follow the store instead of only
        // this Runtime's own operations.
        _store.Changed += OnStoreChanged;

        SynchronizeMirrors();

        _globalTopBar.SetActionHandler(OnTopBarAction);
        _paperActions?.SetActionHandler(OnPaperAction);
        _todoActions?.SetActionHandler(OnTodoAction);
        // A host read can fail while it is still starting up; the Runtime itself must survive, and
        // the store's Changed event rebuilds the contributions once anything changes.
        TryRun(RebuildContributions);

        _settingsSubscription = context.Settings.Subscribe(OnSettingsChanged);
        _workspaceSubscription = _workspace.Subscribe(BuildEventFilter(), OnWorkspaceEvent);

        // Reviewed last: a constructor failure must not leave a timer behind, because the host can
        // only dispose a Runtime instance it actually received.
        _noteTimer = new System.Threading.Timer(OnNoteTimerTick, null, Timeout.Infinite, Timeout.Infinite);

        // Cards archived while this Runtime was not running can already be on screen (the host's
        // show-all paths never consult the plugin), so the lock gets one sweep at startup too.
        ScheduleLockSweep();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Every teardown step is best effort and none of them waits for a worker: the host already
        // revoked this Runtime's lease, so a host call here can legitimately fail.
        _store.Changed -= OnStoreChanged;
        CancelLockSweep();
        TryDispose(_workspaceSubscription);
        TryDispose(_settingsSubscription);
        try
        {
            _noteTimer?.Dispose();
        }
        catch (Exception ex)
        {
            Warn(ex);
        }

        _noteTimer = null;

        // Each step is independent: one revoked capability must not skip the others.
        TryRun(() => _todoActions?.SetActionHandler(null));
        TryRun(() => _todoActions?.Clear());
        TryRun(() => _paperActions?.SetActionHandler(null));
        TryRun(() => _paperActions?.Clear());
        TryRun(() => _globalTopBar.SetActionHandler(null));
        TryRun(() => _globalTopBar.Clear());

        // Flush synchronously: the host is tearing this Runtime down (often because the app is
        // exiting), and a debounced write would lose the last captured change.
        _store.Save(immediate: true);
    }

    private static PaperTodoEventFilter BuildEventFilter() => new()
    {
        Kinds = new HashSet<PaperTodoEventKind>
        {
            PaperTodoEventKind.PaperCreated,
            PaperTodoEventKind.PaperChanged,
            PaperTodoEventKind.PaperDeleted,
            PaperTodoEventKind.TodoCreated,
            PaperTodoEventKind.TodoChanged,
            PaperTodoEventKind.TodoDeleted,
            PaperTodoEventKind.NoteChanged
        },
        // Our own hide/show/create/delete must never re-enter as a user action.
        ExcludeOwnOperations = true
    };

    private static PaperTodoAction[] CreateTodoRowActions() =>
    [
        new PaperTodoAction
        {
            Id = TodoActionArchiveId,
            Icon = PaperTopBarIcon.Character(ArchiveGlyph),
            // "Archive" + "Todo": no dedicated manifest string exists for one row, and inventing a
            // second catalog would bypass ArchiveText.
            Text = ArchiveText.Format("CapsuleText", ArchiveText.Get("BadgeTodo")),
            ToolTip = string.Empty,
            Priority = 100,
            Placement = PaperTodoActionPlacement.ContextMenu
        }
    ];

    // ---------------------------------------------------------------- workspace events

    private void OnWorkspaceEvent(PaperTodoEvent value)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            switch (value)
            {
                case PaperCreatedEvent created:
                    OnPaperCreated(created.Paper);
                    break;
                case PaperChangedEvent changed:
                    OnPaperChanged(changed);
                    break;
                case PaperDeletedEvent deleted:
                    OnPaperDeleted(deleted);
                    break;
                case TodoCreatedEvent created:
                    PublishTodoRow(created.Todo);
                    RefreshTodoMirror(created.Todo);
                    break;
                case TodoChangedEvent changed:
                    PublishTodoRow(changed.After);
                    RefreshTodoMirror(changed.After);
                    break;
                case TodoDeletedEvent deleted:
                    OnTodoDeleted(deleted.Todo, deleted.Metadata);
                    break;
                case NoteChangedEvent changed:
                    OnNoteChanged(changed);
                    break;
            }
        }
        catch (Exception ex)
        {
            // One failing event must never break PaperTodo or the archive itself.
            Warn(ex);
        }
    }

    private void OnPaperCreated(PaperSnapshot paper)
    {
        if (IsOwnPaper(paper))
        {
            return;
        }

        try
        {
            if (_settings.ContextMenuAction && _paperActions != null)
            {
                _paperActions.SetActions(paper.Id, BuildPaperMenu(paper.Id));
            }
        }
        catch (PaperTodoPluginException ex)
        {
            Trace.TraceWarning("Archive paper menu skipped. Code={0}", ex.Code);
        }

        if (!_settings.AutoBackup)
        {
            return;
        }

        SaveMirror(ArchiveMirror.Capture(_workspace, paper).Mirror);
    }

    private void OnPaperChanged(PaperChangedEvent changed)
    {
        var paper = changed.After;
        if (IsOwnPaper(paper))
        {
            return;
        }

        if ((changed.ChangedFields & PaperChangedFields.Visibility) != 0 &&
            !changed.Before.IsVisible &&
            paper.IsVisible)
        {
            // A card became visible again. Archiving is nothing but IsVisible=false, so the host
            // cannot tell an archived card from a normally hidden one: its show-all paths (tray
            // double-click, the tray "show all" button and the Show/Hide/Toggle visibility
            // shortcut) reveal every card with no filter at all. The lock answers that by hiding
            // archived cards again; without it the reveal is treated as a real restore.
            if (_settings.LockArchivedHidden)
            {
                ScheduleLockSweep();
            }
            else
            {
                RestoreRecordBroughtBackByHost(paper);
            }
        }

        if (!_settings.AutoBackup)
        {
            return;
        }

        if ((changed.ChangedFields & PaperChangedFields.BodyProvider) != 0 &&
            ArchiveMirror.IsNoteType(paper.Type))
        {
            // Another body provider means the previously stored body can never be read again.
            SaveMirror(ArchiveMirror.Capture(_workspace, paper).Mirror);
            return;
        }

        var mirror = _store.FindMirror(paper.Id);
        if (mirror == null)
        {
            SaveMirror(ArchiveMirror.Capture(_workspace, paper).Mirror);
            return;
        }

        if (ArchiveMirror.ApplyPaperSnapshot(mirror, paper))
        {
            SaveMirror(mirror);
        }
    }

    /// <summary>
    /// Lock-off behavior: the user can bring a hidden card back through PaperTodo itself (tray
    /// menu, "show"). The card is then on screen while the record still claims it is archived, so
    /// the record moves to history and the context menu offers "archive" again.
    /// With <see cref="ArchiveSettings.LockArchivedHidden"/> on, the same reveal is undone instead.
    /// </summary>
    private void RestoreRecordBroughtBackByHost(PaperSnapshot paper)
    {
        var archivedEntryId = _store.FindEntryIdByArchivedPaper(paper.Id);
        if (archivedEntryId == null || _store.Find(archivedEntryId) is not { } entry)
        {
            return;
        }

        entry.State = ArchiveEntryState.Restored;
        entry.RestoredAt = DateTimeOffset.Now;
        entry.RestoredPaperId = paper.Id;
        // The store change schedules the contribution rebuild that flips the menu label.
        _store.Upsert(entry);
    }

    // ---------------------------------------------------------------- archive lock

    /// <summary>
    /// Schedules exactly one hide sweep on the UI dispatcher.
    ///
    /// A tray double-click reveals every card at once, so the subscription delivers one reveal per
    /// card. Restarting a short-delay timer per reveal collapses that burst into a single sweep
    /// instead of one ListPapers/hide pair per event. Re-entrancy is guarded twice: our own
    /// HidePaper is a Plugin-origin operation excluded by <see cref="BuildEventFilter"/>, and a
    /// running sweep can never start another one.
    /// </summary>
    private void ScheduleLockSweep()
    {
        if (_disposed || !_settings.LockArchivedHidden)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            // No dispatcher left to coalesce on: hide right away instead of losing the lock.
            RunLockSweep();
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_lockSweepTimer == null)
            {
                _lockSweepTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = LockSweepDelay
                };
                _lockSweepTimer.Tick += OnLockSweepTick;
            }

            // Cancel/replace: the sweep runs once, after the last reveal of the burst.
            _lockSweepTimer.Stop();
            _lockSweepTimer.Start();
        }
    }

    private void OnLockSweepTick(object? sender, EventArgs e)
    {
        _lockSweepTimer?.Stop();
        RunLockSweep();
    }

    /// <summary>Cancels a pending sweep so no timer callback can run after Dispose.</summary>
    private void CancelLockSweep()
    {
        try
        {
            DispatcherTimer? timer;
            lock (_gate)
            {
                timer = _lockSweepTimer;
                _lockSweepTimer = null;
            }

            if (timer == null)
            {
                return;
            }

            timer.Stop();
            timer.Tick -= OnLockSweepTick;
        }
        catch (Exception ex)
        {
            // Dispose must never throw, not even while the dispatcher is shutting down.
            Warn(ex);
        }
    }

    private void RunLockSweep()
    {
        if (_disposed || !_settings.LockArchivedHidden)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _lockSweepRunning)
            {
                // A hide never produces a reveal, so a concurrent second sweep could only repeat work.
                return;
            }

            _lockSweepRunning = true;
        }

        try
        {
            SweepVisibleArchivedCards();
        }
        finally
        {
            lock (_gate)
            {
                _lockSweepRunning = false;
            }
        }
    }

    /// <summary>
    /// Reads the workspace once and re-hides every visible card that still has an Archived record.
    /// The archived-paper index contains only Archived records, so Trashed/Restored cards are never
    /// touched, and the plugin's own management card is skipped explicitly.
    /// </summary>
    private void SweepVisibleArchivedCards()
    {
        IReadOnlyList<PaperSnapshot> papers;
        try
        {
            papers = _workspace.ListPapers();
        }
        catch (Exception ex)
        {
            Warn(ex);
            return;
        }

        foreach (var paper in papers)
        {
            if (_disposed || !paper.IsVisible || IsOwnPaper(paper))
            {
                continue;
            }

            if (_store.FindEntryIdByArchivedPaper(paper.Id) == null)
            {
                continue;
            }

            try
            {
                // Hide only: the record and the data stay exactly as they were, so the user can still
                // restore the card from the Archive Center.
                _presentation.HidePaper(paper.Id);
            }
            catch (Exception ex)
            {
                // A card can disappear between the list and the hide; never abort the whole sweep.
                Warn(ex);
            }
        }
    }

    /// <summary>
    /// A deleted paper arrives after one <c>TodoDeletedEvent</c> per row, and the paper is already
    /// gone from host state. The mirror is the only source of a note body, which is why it is built
    /// long before this point.
    /// </summary>
    private void OnPaperDeleted(PaperDeletedEvent deleted)
    {
        var paper = deleted.Paper;
        var buffered = TakePendingTodos(paper.Id);
        var mirror = _store.FindMirror(paper.Id);
        var origin = MapOrigin(deleted.Metadata.Origin);

        try
        {
            _paperActions?.Clear(paper.Id);
        }
        catch (Exception ex)
        {
            Warn(ex);
        }

        if (mirror == null && buffered.Count > 0)
        {
            // Continuous backup may be off; the per-row events still carry complete todo content.
            mirror = new PaperMirror
            {
                PaperId = paper.Id,
                PaperType = paper.Type,
                Title = paper.Title,
                BodyProviderId = paper.BodyProviderId,
                IsVisible = paper.IsVisible,
                Todos = buffered,
                NoteContentAvailable = false,
                CapturedAt = DateTimeOffset.Now
            };
        }

        if (mirror != null &&
            RecordTrashed(
                mirror,
                origin,
                _settings.CaptureHostDeletes && origin != ArchiveCaptureOrigin.Plugin))
        {
            _store.RemoveMirror(paper.Id);
        }
        // With host-delete capture disabled the mirror is deliberately kept: it is still the only
        // copy of that content and is promoted to a record as soon as the feature is enabled.
    }

    private void OnTodoDeleted(TodoSnapshot todo, PaperTodoEventMetadata metadata)
    {
        try
        {
            _todoActions?.Clear(todo.PaperId, todo.Id);
        }
        catch (Exception ex)
        {
            Warn(ex);
        }

        if (!PaperStillExists(todo.PaperId))
        {
            // The whole card is going away. Keep the mirror intact for the PaperDeletedEvent and
            // buffer the row so a card can still be rescued when continuous backup is off.
            BufferPendingTodo(todo);
            return;
        }

        if (_settings.AutoBackup)
        {
            var mirror = _store.FindMirror(todo.PaperId);
            if (mirror != null && ArchiveMirror.RemoveTodo(mirror, todo.Id))
            {
                _store.SetMirror(mirror);
            }
        }

        var origin = MapOrigin(metadata.Origin);
        if (!_settings.CaptureHostDeletes || origin == ArchiveCaptureOrigin.Plugin)
        {
            return;
        }

        _store.Upsert(ArchiveMirror.ToTrashedTodoEntry(
            todo,
            origin,
            ArchiveText.Get("ReasonTodoDelete")));
    }

    private void OnNoteChanged(NoteChangedEvent changed)
    {
        if (!_settings.AutoBackup)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingNotePapers.Add(changed.PaperId);
        }

        try
        {
            _noteTimer?.Change(NoteRefreshDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown owns teardown; a lost refresh is irrelevant then.
        }
    }

    private void OnNoteTimerTick(object? state) => PostToUi(RefreshPendingNotes);

    /// <summary>
    /// note.changed never carries the body, so the text is re-read after a debounce. Reads never
    /// commit an editor, which is why a mirror can miss the last keystrokes.
    /// </summary>
    private void RefreshPendingNotes()
    {
        if (_disposed)
        {
            return;
        }

        string[] paperIds;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            paperIds = _pendingNotePapers.ToArray();
            _pendingNotePapers.Clear();
        }

        foreach (var paperId in paperIds)
        {
            try
            {
                var paper = _workspace.GetPaper(paperId);
                if (paper == null ||
                    IsOwnPaper(paper) ||
                    !ArchiveMirror.IsNoteType(paper.Type))
                {
                    continue;
                }

                SaveMirror(ArchiveMirror.Capture(_workspace, paper).Mirror);
            }
            catch (Exception ex)
            {
                Warn(ex);
            }
        }
    }

    // ---------------------------------------------------------------- trash capture

    /// <summary>
    /// Turns a mirror into a trash record. Returns true when a record now covers that content, which
    /// is also the only case where the caller may drop the mirror.
    /// </summary>
    private bool RecordTrashed(PaperMirror mirror, ArchiveCaptureOrigin origin, bool allowNewRecord)
    {
        var archivedEntryId = _store.FindEntryIdByArchivedPaper(mirror.PaperId);
        if (archivedEntryId != null && _store.Find(archivedEntryId) is { } archived)
        {
            // This plugin hid the card itself. Keeping the record Archived would claim the data is
            // still inside PaperTodo, so the record moves to the trash instead of a duplicate being
            // added. The frozen payload stays: it was taken from the live card and is complete.
            archived.State = ArchiveEntryState.Trashed;
            archived.Origin = origin;
            archived.Reason = ArchiveText.Get("ReasonHostDelete");
            archived.CapturedAt = DateTimeOffset.Now;
            _store.Upsert(archived);
            return true;
        }

        if (!allowNewRecord)
        {
            return false;
        }

        _store.Upsert(ArchiveMirror.ToTrashedEntry(
            mirror,
            origin,
            ArchiveText.Get("ReasonHostDelete"),
            ArchiveMirror.TrashCaptureHint(mirror)));
        return true;
    }

    private bool PaperStillExists(string paperId)
    {
        try
        {
            return _workspace.GetPaper(paperId) != null;
        }
        catch (Exception ex)
        {
            // Assume alive: that keeps the mirror intact rather than losing recoverable content.
            Warn(ex);
            return true;
        }
    }

    private void BufferPendingTodo(TodoSnapshot todo)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            PrunePendingTodosLocked();
            if (!_pendingDeletedTodos.TryGetValue(todo.PaperId, out var pending))
            {
                if (_pendingDeletedTodos.Count >= MaximumPendingTodoPapers)
                {
                    return;
                }

                pending = new PendingTodoRows();
                _pendingDeletedTodos[todo.PaperId] = pending;
            }

            pending.At = DateTimeOffset.Now;
            pending.Rows.RemoveAll(item =>
                string.Equals(item.Id, todo.Id, StringComparison.Ordinal));
            pending.Rows.Add(ArchiveMirror.ToRecord(todo));
        }
    }

    private List<ArchiveTodoRecord> TakePendingTodos(string paperId)
    {
        lock (_gate)
        {
            PrunePendingTodosLocked();
            if (!_pendingDeletedTodos.Remove(paperId, out var pending))
            {
                return [];
            }

            return pending.Rows;
        }
    }

    private void PrunePendingTodosLocked()
    {
        if (_pendingDeletedTodos.Count == 0)
        {
            return;
        }

        var deadline = DateTimeOffset.Now - PendingTodoLifetime;
        foreach (var paperId in _pendingDeletedTodos
                     .Where(pair => pair.Value.At < deadline)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _pendingDeletedTodos.Remove(paperId);
        }
    }

    private void RefreshTodoMirror(TodoSnapshot todo)
    {
        if (!_settings.AutoBackup)
        {
            return;
        }

        // Only mirrored papers are tracked here. Our own management card is a note and is never
        // mirrored, so no extra host round trip is needed to filter it out.
        var mirror = _store.FindMirror(todo.PaperId);
        if (mirror != null && ArchiveMirror.UpsertTodo(mirror, todo))
        {
            _store.SetMirror(mirror);
        }
    }

    // ---------------------------------------------------------------- mirrors

    private void SynchronizeMirrors()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<PaperSnapshot> papers;
        try
        {
            papers = _workspace.ListPapers();
        }
        catch (Exception ex)
        {
            // Without a live paper list, rescuing orphans would trash every mirror by mistake.
            Warn(ex);
            return;
        }

        var livePaperIds = papers
            .Select(paper => paper.Id)
            .ToHashSet(StringComparer.Ordinal);
        var todosByPaper = ReadTodosByPaper();

        if (_settings.AutoBackup)
        {
            foreach (var paper in papers)
            {
                if (IsOwnPaper(paper))
                {
                    continue;
                }

                try
                {
                    var rows = todosByPaper.TryGetValue(paper.Id, out var list) ? list : null;
                    SaveMirror(ArchiveMirror.Capture(_workspace, paper, rows).Mirror);
                }
                catch (Exception ex)
                {
                    Warn(ex);
                }
            }
        }

        RescueOrphanMirrors(livePaperIds);
        _store.ApplyRetention(_settings.ToRetention());
    }

    /// <summary>
    /// A mirror whose paper no longer exists means the card was deleted while this Runtime was not
    /// running (for example with the management card closed). Turning it into a trash record is the
    /// last chance to keep that content.
    /// </summary>
    private void RescueOrphanMirrors(HashSet<string> livePaperIds)
    {
        foreach (var mirror in _store.SnapshotMirrors())
        {
            if (livePaperIds.Contains(mirror.PaperId))
            {
                continue;
            }

            try
            {
                if (RecordTrashed(mirror, ArchiveCaptureOrigin.System, _settings.CaptureHostDeletes))
                {
                    _store.RemoveMirror(mirror.PaperId);
                }
                // Otherwise capture is disabled; keeping the mirror lets a later enable still turn
                // this content into a record instead of discarding the last copy.
            }
            catch (Exception ex)
            {
                Warn(ex);
            }
        }
    }

    private Dictionary<string, List<TodoSnapshot>> ReadTodosByPaper()
    {
        var result = new Dictionary<string, List<TodoSnapshot>>(StringComparer.Ordinal);
        try
        {
            foreach (var todo in _workspace.ListTodos(includeBlank: false))
            {
                if (!result.TryGetValue(todo.PaperId, out var rows))
                {
                    rows = [];
                    result[todo.PaperId] = rows;
                }

                rows.Add(todo);
            }
        }
        catch (Exception ex)
        {
            Warn(ex);
        }

        return result;
    }

    private void SaveMirror(PaperMirror mirror)
    {
        var existing = _store.FindMirror(mirror.PaperId);
        if (existing != null && ArchiveMirror.HasSameContent(existing, mirror))
        {
            // The store saves on every write; a repeated event must not rewrite the document.
            return;
        }

        _store.SetMirror(mirror);
    }

    // ---------------------------------------------------------------- contributions

    /// <summary>
    /// Archive records changed somewhere in this process (this Runtime or the management card), so
    /// the "archive"/"unarchive" menu labels and the action set must be rebuilt.
    /// </summary>
    private void OnStoreChanged(ArchiveChangeKind kind)
    {
        if (_disposed ||
            kind is ArchiveChangeKind.MirrorsChanged or ArchiveChangeKind.Reloaded)
        {
            return;
        }

        ScheduleContributionRebuild();
    }

    /// <summary>
    /// Coalesces rebuilds: one user operation can write many records (restore-all, a card with many
    /// rows), and rebuilding the host menus once per record would stall the dispatcher.
    /// </summary>
    private void ScheduleContributionRebuild()
    {
        lock (_gate)
        {
            if (_disposed || _rebuildScheduled)
            {
                return;
            }

            _rebuildScheduled = true;
        }

        void RunOnce()
        {
            try
            {
                RebuildContributions();
            }
            finally
            {
                lock (_gate)
                {
                    _rebuildScheduled = false;
                }
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            // No dispatcher to defer to; correctness beats coalescing.
            RunOnce();
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke((Action)RunOnce, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Warn(ex);
            lock (_gate)
            {
                _rebuildScheduled = false;
            }
        }
    }

    private void RebuildContributions()
    {
        if (_disposed)
        {
            return;
        }

        RebuildTopBarActions();
        RebuildPaperMenus();
        RebuildTodoMenus();
    }

    private void RebuildTopBarActions()
    {
        var actions = new List<PaperTopBarAction>(2);
        if (_settings.TopBarAction)
        {
            actions.Add(new PaperTopBarAction
            {
                Id = TopBarArchiveActionId,
                Icon = PaperTopBarIcon.Character(ArchiveGlyph),
                ToolTip = ArchiveText.Get("TopBarArchiveTooltip"),
                Priority = 100
            });
            actions.Add(new PaperTopBarAction
            {
                Id = TopBarOpenCenterActionId,
                Icon = PaperTopBarIcon.Character(CenterGlyph),
                ToolTip = ArchiveText.Get("TopBarOpenCenterTooltip"),
                Priority = 90
            });
        }

        // Replace semantics: an empty list removes the buttons without dropping the handler.
        _globalTopBar.SetActionHandler(OnTopBarAction);
        _globalTopBar.SetActions(actions);
    }

    private void RebuildPaperMenus()
    {
        if (_paperActions == null)
        {
            return;
        }

        _paperActions.Clear();
        if (!_settings.ContextMenuAction)
        {
            return;
        }

        foreach (var paper in _workspace.ListPapers())
        {
            if (IsOwnPaper(paper))
            {
                continue;
            }

            try
            {
                _paperActions.SetActions(paper.Id, BuildPaperMenu(paper.Id));
            }
            catch (PaperTodoPluginException ex)
            {
                // The paper can disappear between listing and registering.
                Trace.TraceWarning("Archive paper menu skipped. Code={0}", ex.Code);
            }
        }
    }

    private List<PaperAction> BuildPaperMenu(string paperId)
    {
        var actions = new List<PaperAction>(2);
        actions.Add(IsArchived(paperId)
            ? new PaperAction { Id = PaperActionUnarchiveId, Text = ArchiveText.Get("MenuUnarchive") }
            : new PaperAction { Id = PaperActionArchiveId, Text = ArchiveText.Get("MenuArchive") });
        actions.Add(new PaperAction
        {
            Id = PaperActionOpenCenterId,
            Text = ArchiveText.Get("MenuOpenCenter")
        });
        return actions;
    }

    private void RebuildTodoMenus()
    {
        if (_todoActions == null)
        {
            return;
        }

        _todoActions.Clear();
        if (!_settings.ContextMenuAction)
        {
            return;
        }

        foreach (var todo in _workspace.ListTodos(includeBlank: false))
        {
            PublishTodoRow(todo);
        }
    }

    private void PublishTodoRow(TodoSnapshot todo)
    {
        if (_disposed || _todoActions == null || !_settings.ContextMenuAction)
        {
            return;
        }

        try
        {
            _todoActions.SetActions(todo.PaperId, todo.Id, _todoRowActions);
        }
        catch (PaperTodoPluginException ex)
        {
            // Host prunes rows of deleted papers; a raced row is not an error worth surfacing.
            Trace.TraceWarning("Archive todo contribution skipped. Code={0}", ex.Code);
        }
    }

    // ---------------------------------------------------------------- invocation handlers

    private void OnTopBarAction(PaperTopBarActionInvocation invocation)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (string.Equals(invocation.ActionId, TopBarArchiveActionId, StringComparison.Ordinal))
            {
                if (string.Equals(
                        invocation.TargetBodyProviderId,
                        _context.ProviderId,
                        StringComparison.Ordinal))
                {
                    // Global actions also render on our own management card; it is not archivable.
                    return;
                }

                // Archiving is non-destructive and one click away from being undone, so it commits
                // immediately. Only the irreversible purge asks for confirmation.
                Report(_service.ArchivePaper(invocation.TargetPaperId));
                return;
            }

            if (string.Equals(invocation.ActionId, TopBarOpenCenterActionId, StringComparison.Ordinal))
            {
                Report(_service.OpenArchiveCenter());
            }
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private void OnPaperAction(PaperActionInvocation invocation)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            switch (invocation.ActionId)
            {
                case PaperActionArchiveId:
                    if (IsOwnPaper(invocation.Paper))
                    {
                        return;
                    }

                    Report(_service.ArchivePaper(invocation.Paper.Id));
                    break;

                case PaperActionUnarchiveId:
                    var entryId = _store.FindEntryIdByArchivedPaper(invocation.Paper.Id);
                    if (entryId != null)
                    {
                        Report(_service.RestoreEntry(entryId));
                    }

                    break;

                case PaperActionOpenCenterId:
                    Report(_service.OpenArchiveCenter());
                    break;
            }
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private void OnTodoAction(PaperTodoActionInvocation invocation)
    {
        if (_disposed ||
            !string.Equals(invocation.ActionId, TodoActionArchiveId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Report(_service.ArchiveTodoItem(invocation.PaperId, invocation.TodoId));
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private void OnSettingsChanged(string json)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var settings = ArchiveSettings.Parse(json);
            var backupEnabled = !_settings.AutoBackup && settings.AutoBackup;
            var captureEnabled = !_settings.CaptureHostDeletes && settings.CaptureHostDeletes;
            var lockEnabled = !_settings.LockArchivedHidden && settings.LockArchivedHidden;
            _settings = settings;
            _store.ApplyRetention(settings.ToRetention());

            if (backupEnabled || captureEnabled)
            {
                // Turning backup on must capture every card that has no mirror yet. Turning
                // host-delete capture on must also promote the orphan mirrors that were kept while
                // it was off, so the promised rescue is immediate instead of next-start.
                SynchronizeMirrors();
            }

            RebuildContributions();

            if (lockEnabled)
            {
                // Cards revealed while the lock was off are already on screen; re-hide them now
                // instead of waiting for the next host reveal.
                RunLockSweep();
            }
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    // ---------------------------------------------------------------- helpers

    private bool IsArchived(string paperId) =>
        _store.FindEntryIdByArchivedPaper(paperId) is not null;

    private bool IsOwnPaper(PaperSnapshot paper) =>
        string.Equals(paper.BodyProviderId, _context.ProviderId, StringComparison.Ordinal);

    private static ArchiveCaptureOrigin MapOrigin(PaperTodoEventOrigin origin) => origin switch
    {
        PaperTodoEventOrigin.User => ArchiveCaptureOrigin.User,
        PaperTodoEventOrigin.Mcp => ArchiveCaptureOrigin.Mcp,
        PaperTodoEventOrigin.Plugin => ArchiveCaptureOrigin.Plugin,
        PaperTodoEventOrigin.System => ArchiveCaptureOrigin.System,
        PaperTodoEventOrigin.Import => ArchiveCaptureOrigin.Import,
        _ => ArchiveCaptureOrigin.Unknown
    };

    private static void Report(ArchiveOperationResult result)
    {
        if (!result.Ok)
        {
            // The abstraction has no transient toast surface; the failure is diagnostic only and
            // is never shown as raw text.
            Trace.TraceWarning("Archive action failed. Code={0}", result.Code);
        }
    }

    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            TryRun(action);
            return;
        }

        if (dispatcher.CheckAccess())
        {
            TryRun(action);
            return;
        }

        try
        {
            // An unhandled exception inside a dispatcher callback would take down the host.
            _ = dispatcher.BeginInvoke((Action)(() => TryRun(action)), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private static void TryDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private static void Warn(Exception ex) =>
        Trace.TraceWarning("Archive runtime: {0}", ex.GetBaseException());

    private sealed class PendingTodoRows
    {
        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;

        public List<ArchiveTodoRecord> Rows { get; } = [];
    }
}
