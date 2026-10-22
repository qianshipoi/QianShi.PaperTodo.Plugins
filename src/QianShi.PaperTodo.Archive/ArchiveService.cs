using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// Archive behavior shared by the provider Runtime and the Archive Center card body. Both surfaces
/// build one <see cref="ArchiveHostContext"/> and get identical non-destructive semantics.
///
/// Three host facts drive the design:
/// PaperTodo has no pre-delete hook, so archiving must be a plugin-provided action;
/// <c>AppController.HidePaper</c> only clears <c>IsVisible</c>, which is the non-destructive "remove
/// from the interface" primitive and never touches the data;
/// <c>DeletePaper</c> really destroys content, so it is reachable only from an explicit
/// "delete forever" purge.
/// </summary>
internal sealed class ArchiveService : IArchiveOperations
{
    private readonly ArchiveHostContext _context;
    private readonly IPaperTodoHostApi _workspace;
    private readonly IPaperWorkspacePresentationApi _presentation;
    private readonly IArchiveRepository _repository;
    private readonly string _providerId;

    public ArchiveService(ArchiveHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _workspace = context.Workspace;
        _presentation = context.Presentation;
        _repository = context.Repository;
        _providerId = context.ProviderId;
    }

    public bool IsArchived(string paperId) =>
        !string.IsNullOrWhiteSpace(paperId) &&
        _repository.FindEntryIdByArchivedPaper(paperId.Trim()) is not null;

    public ArchiveOperationResult ArchivePaper(string paperId) =>
        Run(() => ArchivePaperCore(paperId));

    public ArchiveOperationResult ArchiveTodoItem(string paperId, string todoId) =>
        Run(() => ArchiveTodoItemCore(paperId, todoId));

    public ArchiveOperationResult RestoreEntry(string entryId) =>
        Run(() => RestoreEntryCore(entryId));

    public ArchiveOperationResult PurgeEntry(string entryId) =>
        Run(() => PurgeEntryCore(entryId));

    public ArchiveOperationResult RestoreAll() => Run(RestoreAllCore);

    public ArchiveOperationResult OpenArchiveCenter() => Run(OpenArchiveCenterCore);

    private ArchiveOperationResult ArchivePaperCore(string paperId)
    {
        var id = Normalize(paperId);
        if (id.Length == 0)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotFound);
        }

        if (string.Equals(id, _context.HostPaperId, StringComparison.Ordinal))
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.SelfPaper);
        }

        if (Denied(PaperTodoPermissionNames.PapersRead) is { } missingRead)
        {
            return missingRead;
        }

        var paper = GetPaperOrNull(id);
        if (paper == null)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotFound);
        }

        if (IsOwnBody(paper.BodyProviderId))
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.PluginPaper);
        }

        if (_repository.FindEntryIdByArchivedPaper(id) is not null)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.AlreadyArchived);
        }

        var capture = ArchiveMirror.Capture(_workspace, paper);
        var entry = ArchiveRules.FromMirror(
            capture.Mirror,
            ArchiveEntryState.Archived,
            ArchiveCaptureOrigin.User,
            ArchiveText.Get("ReasonUserArchive"));
        entry.CaptureNote = capture.CaptureNote;

        _repository.Upsert(entry);
        _repository.SetMirror(capture.Mirror);

        try
        {
            Present(() => _presentation.HidePaper(id));
        }
        catch (PaperTodoPluginException ex)
        {
            // Never keep a record that claims "archived" while the card is still on screen: the
            // record is what makes AlreadyArchived/Restore meaningful.
            _repository.Remove(entry.Id);
            return ArchiveOperationResult.Failure(MapHostCode(ex.Code));
        }

        NotifyChanged();
        return ArchiveOperationResult.Success(ArchiveResultCodes.ArchivePaper);
    }

    private ArchiveOperationResult ArchiveTodoItemCore(string paperId, string todoId)
    {
        var id = Normalize(paperId);
        var normalizedTodoId = todoId?.Trim() ?? string.Empty;
        if (id.Length == 0 || normalizedTodoId.Length == 0)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotFound);
        }

        if (Denied(PaperTodoPermissionNames.TodosRead) is { } missingRead)
        {
            return missingRead;
        }

        if (Denied(PaperTodoPermissionNames.TodosDelete) is { } missingDelete)
        {
            return missingDelete;
        }

        var paper = GetPaperOrNull(id);
        if (paper == null)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotFound);
        }

        if (IsOwnBody(paper.BodyProviderId))
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.PluginPaper);
        }

        var todo = FindTodo(id, normalizedTodoId);
        if (todo == null)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotFound);
        }

        var entry = ArchiveMirror.ToTrashedTodoEntry(
            todo,
            ArchiveCaptureOrigin.User,
            ArchiveText.Get("ReasonTodoDelete"));
        _repository.Upsert(entry);

        try
        {
            // The only place this plugin deletes anything: the row is frozen in the record first.
            _workspace.DeleteTodo(new DeleteTodoRequest { PaperId = id, TodoId = todo.Id });
        }
        catch (PaperTodoPluginException ex)
        {
            _repository.Remove(entry.Id);
            return ArchiveOperationResult.Failure(MapHostCode(ex.Code));
        }

        // Our own delete is excluded from the Runtime subscription, so drop the row from the mirror
        // here; otherwise a later host delete would capture a row that no longer exists.
        var mirror = _repository.FindMirror(id);
        if (mirror != null && ArchiveMirror.RemoveTodo(mirror, todo.Id))
        {
            _repository.SetMirror(mirror);
        }

        NotifyChanged();
        return ArchiveOperationResult.Success(ArchiveResultCodes.ArchiveTodoItem);
    }

    private ArchiveOperationResult RestoreEntryCore(string entryId)
    {
        var entry = _repository.Find(entryId);
        if (entry == null)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotFound);
        }

        if (entry.State == ArchiveEntryState.Restored)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotRestorable);
        }

        if (entry.Kind == ArchiveEntryKind.TodoItem)
        {
            return RestoreTodoRow(entry);
        }

        if (Denied(PaperTodoPermissionNames.PapersRead) is { } missingRead)
        {
            return missingRead;
        }

        var paper = GetPaperOrNull(entry.PaperId);
        if (paper != null && !IsOwnBody(paper.BodyProviderId))
        {
            // Claim the restore before showing the card. The archive lock re-hides every card that
            // still has an Archived record, and a sweep scheduled before this point may still be
            // pending, so the record must stop claiming "archived" first. The host also reports our
            // own ShowPaper as a Plugin-origin operation (excluded by the Runtime subscription), but
            // the ordering must not depend on that.
            MarkRestored(entry, entry.PaperId);
            try
            {
                Present(() => _presentation.ShowPaper(entry.PaperId, activate: true));
            }
            catch (PaperTodoPluginException ex)
            {
                // The card stayed hidden, so the UI must not claim it was restored.
                MarkArchivedAgain(entry);
                NotifyChanged();
                return ArchiveOperationResult.Failure(MapHostCode(ex.Code));
            }

            NotifyChanged();
            return ArchiveOperationResult.Success(ArchiveResultCodes.Restore);
        }

        if (!ArchiveRules.IsRestorable(entry))
        {
            return NotRestorableResult(entry);
        }

        if (Denied(PaperTodoPermissionNames.PapersCreate) is { } missingCreate)
        {
            return missingCreate;
        }

        if (ArchiveMirror.IsNoteType(entry.PaperType))
        {
            if (Denied(PaperTodoPermissionNames.NotesAppend) is { } missingNote)
            {
                return missingNote;
            }
        }
        else if (entry.Todos.Count > 0 &&
                 Denied(PaperTodoPermissionNames.TodosAppend) is { } missingTodo)
        {
            return missingTodo;
        }

        var previousPaperId = entry.PaperId;

        // Same rule as the show path: the record stops claiming Archived before the host can
        // present a visible card. The new paper id is only known once the host assigned it.
        MarkRestored(entry, restoredPaperId: null);
        ArchiveRestoreOutcome outcome;
        try
        {
            outcome = ArchiveRestore.Recreate(_workspace, entry, LivePaperIds());
        }
        catch (PaperTodoPluginException ex)
        {
            MarkArchivedAgain(entry);
            NotifyChanged();
            return ArchiveOperationResult.Failure(MapHostCode(ex.Code));
        }

        if (!outcome.Ok)
        {
            MarkArchivedAgain(entry);
            NotifyChanged();
            return ArchiveOperationResult.Failure(
                outcome.FailureCode ?? ArchiveResultCodes.CreateFailed,
                outcome.Detail);
        }

        var restoredPaperId = outcome.PaperId!;
        MarkRestored(entry, restoredPaperId);

        // Own creates are excluded from the Runtime subscription by design, so seed the mirror here
        // or a later host delete of this card would have no note body to rescue.
        _repository.SetMirror(ArchiveMirror.FromEntry(entry, restoredPaperId));
        RemapLinks(previousPaperId, restoredPaperId);

        NotifyChanged();
        return ArchiveOperationResult.Success(ArchiveResultCodes.Restore, outcome.Detail);
    }

    private ArchiveOperationResult RestoreTodoRow(ArchiveEntry entry)
    {
        var row = entry.Todos.FirstOrDefault();
        if (row == null || string.IsNullOrWhiteSpace(row.Text))
        {
            return ArchiveOperationResult.Failure(
                ArchiveResultCodes.ContentMissing,
                ArchiveText.Get("HintContentPartial"));
        }

        if (Denied(PaperTodoPermissionNames.TodosRead) is { } missingRead)
        {
            return missingRead;
        }

        if (Denied(PaperTodoPermissionNames.TodosAppend) is { } missingAppend)
        {
            return missingAppend;
        }

        var livePaperIds = LivePaperIds();
        var paper = GetPaperOrNull(entry.PaperId);
        if (paper != null &&
            ArchiveMirror.IsTodoType(paper.Type) &&
            !IsOwnBody(paper.BodyProviderId))
        {
            var item = ArchiveRestore.BuildTodoItem(
                row,
                livePaperIds,
                entry.PaperId,
                out _);

            // Appending does not change visibility, but the record must still stop claiming
            // "archived" before the row exists again so a pending lock sweep cannot race the restore.
            MarkRestored(entry, entry.PaperId);
            AppendTodosResult appended;
            try
            {
                appended = _workspace.AppendTodos(new AppendTodosRequest
                {
                    PaperId = entry.PaperId,
                    Todos = [item]
                });
            }
            catch (PaperTodoPluginException ex)
            {
                MarkArchivedAgain(entry);
                NotifyChanged();
                return ArchiveOperationResult.Failure(MapHostCode(ex.Code));
            }

            AddRestoredRowToMirror(entry.PaperId, row, appended.TodoIds.FirstOrDefault());
            NotifyChanged();
            return ArchiveOperationResult.Success(ArchiveResultCodes.Restore);
        }

        // The parent card is gone too: rebuild the smallest card that carries this row.
        if (Denied(PaperTodoPermissionNames.PapersCreate) is { } missingCreate)
        {
            return missingCreate;
        }

        MarkRestored(entry, restoredPaperId: null);
        ArchiveRestoreOutcome outcome;
        try
        {
            outcome = ArchiveRestore.RecreateTodoRow(_workspace, entry, row, livePaperIds);
        }
        catch (PaperTodoPluginException ex)
        {
            MarkArchivedAgain(entry);
            NotifyChanged();
            return ArchiveOperationResult.Failure(MapHostCode(ex.Code));
        }

        if (!outcome.Ok)
        {
            MarkArchivedAgain(entry);
            NotifyChanged();
            return ArchiveOperationResult.Failure(
                outcome.FailureCode ?? ArchiveResultCodes.CreateFailed,
                outcome.Detail);
        }

        MarkRestored(entry, outcome.PaperId!);
        _repository.SetMirror(ArchiveMirror.FromEntry(entry, outcome.PaperId!));
        NotifyChanged();
        return ArchiveOperationResult.Success(ArchiveResultCodes.Restore, outcome.Detail);
    }

    private ArchiveOperationResult PurgeEntryCore(string entryId)
    {
        var entry = _repository.Find(entryId);
        if (entry == null)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotFound);
        }

        if (entry.Kind == ArchiveEntryKind.Paper &&
            entry.State == ArchiveEntryState.Archived &&
            Denied(PaperTodoPermissionNames.PapersRead) is { } missingRead)
        {
            return missingRead;
        }

        if (entry.Kind == ArchiveEntryKind.Paper &&
            entry.State == ArchiveEntryState.Archived)
        {
            var paper = GetPaperOrNull(entry.PaperId);
            if (paper != null)
            {
                if (IsOwnBody(paper.BodyProviderId) ||
                    (!ArchiveMirror.IsTodoType(paper.Type) &&
                     !ArchiveRules.IsBuiltinMarkdown(paper.BodyProviderId)))
                {
                    // The card has since been taken over by another body provider: purging would
                    // destroy content this plugin never captured.
                    return ArchiveOperationResult.Failure(ArchiveResultCodes.PluginPaper);
                }

                if (Denied(PaperTodoPermissionNames.PapersDelete) is { } missingDelete)
                {
                    return missingDelete;
                }

                // Explicit user intent only: this is the single call path that really destroys data.
                _workspace.DeletePaper(entry.PaperId);
            }

            _repository.RemoveMirror(entry.PaperId);
        }

        _repository.Remove(entry.Id);
        NotifyChanged();
        return ArchiveOperationResult.Success(ArchiveResultCodes.Purge);
    }

    private ArchiveOperationResult RestoreAllCore()
    {
        var entries = _repository.Snapshot();
        var restorable = entries.Where(ArchiveRules.IsRestorable).ToArray();
        if (restorable.Length == 0)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NothingToDo);
        }

        var restored = 0;
        foreach (var entry in restorable)
        {
            var result = RestoreEntryCore(entry.Id);
            if (result.Ok)
            {
                restored++;
            }
            else
            {
                Trace.TraceWarning(
                    "Archive restore-all skipped one record. Code={0}",
                    result.Code);
            }
        }

        if (restored == 0)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotRestorable);
        }

        return ArchiveOperationResult.Success(
            ArchiveResultCodes.RestoreAll,
            restored.ToString());
    }

    private ArchiveOperationResult OpenArchiveCenterCore()
    {
        if (Denied(PaperTodoPermissionNames.PapersRead) is { } missingRead)
        {
            return missingRead;
        }

        var center = _workspace
            .ListPapers()
            .FirstOrDefault(paper => IsOwnBody(paper.BodyProviderId));
        if (center == null)
        {
            // The manifest startup card can be disabled or removed; nothing to reveal then.
            return ArchiveOperationResult.Failure(ArchiveResultCodes.NotFound);
        }

        var attempts = 0;
        PaperTodoPluginException? failure = null;
        var steps = new Action[]
        {
            () => _presentation.ShowPaper(center.Id, activate: false),
            () => _presentation.ExpandPaper(center.Id, activate: true),
            () => _presentation.ActivatePaper(center.Id)
        };
        foreach (var step in steps)
        {
            try
            {
                Present(step);
                attempts++;
            }
            catch (PaperTodoPluginException ex)
            {
                failure = ex;
            }
        }

        if (attempts == 0 && failure != null)
        {
            return ArchiveOperationResult.Failure(MapHostCode(failure.Code));
        }

        return ArchiveOperationResult.Success(ArchiveResultCodes.OpenCenter);
    }

    /// <summary>
    /// Moves a record into history, which also drops it from the archived-paper index in one place.
    /// This must happen before the host can present the card again, otherwise the archive lock
    /// would immediately hide the card that was just restored.
    /// <paramref name="restoredPaperId"/> is null while a card is still being re-created and the
    /// host has not assigned the new id yet.
    /// </summary>
    private void MarkRestored(ArchiveEntry entry, string? restoredPaperId)
    {
        entry.State = ArchiveEntryState.Restored;
        entry.RestoredAt = DateTimeOffset.Now;
        entry.RestoredPaperId = restoredPaperId;
        _repository.Upsert(entry);
    }

    /// <summary>
    /// Puts the archive claim back after the host refused to show or re-create the card, so the
    /// record never reports "restored" for something that never became visible.
    /// </summary>
    private void MarkArchivedAgain(ArchiveEntry entry)
    {
        entry.State = ArchiveEntryState.Archived;
        entry.RestoredAt = null;
        entry.RestoredPaperId = null;
        _repository.Upsert(entry);
    }

    /// <summary>
    /// A re-created card gets a new paper id, so old links pointing at the previous id would dangle.
    /// Rewriting our own frozen records keeps later restores intact.
    /// </summary>
    private void RemapLinks(string previousPaperId, string restoredPaperId)
    {
        if (string.IsNullOrEmpty(previousPaperId) ||
            string.Equals(previousPaperId, restoredPaperId, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var entry in _repository.Snapshot())
        {
            var changed = false;
            foreach (var row in entry.Todos)
            {
                if (!string.Equals(row.LinkedPaperId, previousPaperId, StringComparison.Ordinal))
                {
                    continue;
                }

                row.LinkedPaperId = restoredPaperId;
                changed = true;
            }

            if (changed)
            {
                _repository.Upsert(entry);
            }
        }

        foreach (var mirror in _repository.SnapshotMirrors())
        {
            var changed = false;
            foreach (var row in mirror.Todos)
            {
                if (!string.Equals(row.LinkedPaperId, previousPaperId, StringComparison.Ordinal))
                {
                    continue;
                }

                row.LinkedPaperId = restoredPaperId;
                changed = true;
            }

            if (changed)
            {
                _repository.SetMirror(mirror);
            }
        }
    }

    private void AddRestoredRowToMirror(string paperId, ArchiveTodoRecord row, string? newTodoId)
    {
        var mirror = _repository.FindMirror(paperId);
        if (mirror == null)
        {
            return;
        }

        var record = row.Clone();
        if (!string.IsNullOrEmpty(newTodoId))
        {
            record.Id = newTodoId;
        }

        record.Order = mirror.Todos.Count == 0 ? 0 : mirror.Todos.Max(item => item.Order) + 1;
        mirror.Todos.Add(record);
        mirror.CapturedAt = DateTimeOffset.Now;
        _repository.SetMirror(mirror);
    }

    private ArchiveOperationResult NotRestorableResult(ArchiveEntry entry)
    {
        var foreignBody = ArchiveMirror.IsNoteType(entry.PaperType) &&
            !ArchiveRules.IsBuiltinMarkdown(entry.BodyProviderId);
        if (foreignBody)
        {
            return ArchiveOperationResult.Failure(
                ArchiveResultCodes.NotRestorable,
                ArchiveText.Get("HintNotRestorable"));
        }

        return ArchiveOperationResult.Failure(
            ArchiveResultCodes.ContentMissing,
            ArchiveText.Get("HintContentPartial"));
    }

    private ArchiveOperationResult? Denied(string permission) =>
        _workspace.GrantedPermissions.Contains(permission)
            ? null
            : ArchiveOperationResult.Failure(ArchiveResultCodes.PermissionDenied);

    private PaperSnapshot? GetPaperOrNull(string paperId)
    {
        try
        {
            return _workspace.GetPaper(paperId);
        }
        catch (PaperTodoPluginException ex)
        {
            Trace.TraceWarning("Archive could not read a paper. Code={0}", ex.Code);
            return null;
        }
    }

    private TodoSnapshot? FindTodo(string paperId, string todoId)
    {
        var todos = _workspace.ListTodos(paperId, includeBlank: false);
        return todos.FirstOrDefault(todo =>
            string.Equals(todo.Id, todoId, StringComparison.Ordinal));
    }

    private HashSet<string> LivePaperIds()
    {
        try
        {
            return _workspace
                .ListPapers()
                .Select(paper => paper.Id)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (PaperTodoPluginException ex)
        {
            // Failing closed clears links instead of letting the host reject the whole restore.
            Trace.TraceWarning("Archive could not list papers. Code={0}", ex.Code);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private bool IsOwnBody(string? bodyProviderId) =>
        !string.IsNullOrEmpty(bodyProviderId) &&
        string.Equals(bodyProviderId, _providerId, StringComparison.Ordinal);

    private void NotifyChanged()
    {
        try
        {
            _context.StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // A surface refreshing itself must never break data protection.
            Trace.TraceWarning("Archive state notification failed. Error={0}", ex.GetBaseException());
        }
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>
    /// Host presentation must run on the WPF dispatcher. PaperTodo's own facades marshal too, so
    /// this hop is only about never touching a window or menu from a worker thread.
    /// </summary>
    private static void Present(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Maps a host error code onto a stable result code. Raw host text is never surfaced: the
    /// management surface localizes <see cref="ArchiveResultCodes"/> through
    /// <see cref="ArchiveText.ForResult"/>.
    /// </summary>
    private static string MapHostCode(string? hostCode) => hostCode switch
    {
        "permission_denied" => ArchiveResultCodes.PermissionDenied,
        "paper_not_found" or "todo_not_found" or "note_not_found" or "asset_not_found" =>
            ArchiveResultCodes.NotFound,
        "cannot_delete_host_paper" => ArchiveResultCodes.SelfPaper,
        "session_closed" or "runtime_closed" or "host_unavailable" or "presentation_unavailable" =>
            ArchiveResultCodes.PresentationUnavailable,
        "paper_limit" or "save_failed" or "invalid_params" or "content_too_long" or
            "wrong_paper_type" or "note_not_blank" or "paper_links_disabled" or
            "reminders_disabled" or "self_link_not_allowed" =>
            ArchiveResultCodes.CreateFailed,
        _ => ArchiveResultCodes.Unknown
    };

    private static ArchiveOperationResult Run(Func<ArchiveOperationResult> operation)
    {
        try
        {
            return operation();
        }
        catch (PaperTodoPluginException ex)
        {
            Trace.TraceWarning("Archive operation failed. Code={0}; Error={1}", ex.Code, ex);
            return ArchiveOperationResult.Failure(MapHostCode(ex.Code));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Archive operation failed. Error={0}", ex.GetBaseException());
            return ArchiveOperationResult.Failure(ArchiveResultCodes.Unknown);
        }
    }
}
