namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>
/// One executable provider Runtime plus every fake it needs. The internal
/// <c>ArchiveRuntime(PaperPluginRuntimeContext, IArchiveRepository)</c> seam lets the whole backend
/// run offline against an isolated store in a temp directory — never the installed plugin folder.
/// </summary>
internal sealed class RuntimeFixture : IDisposable
{
    public const string ProviderId = ArchiveFixture.ProviderId;

    public const string DefaultSettingsJson =
        "{\"autoStart\":true,\"autoBackup\":true,\"topBarAction\":true,\"contextMenuAction\":true," +
        "\"captureHostDeletes\":true,\"retentionDays\":0,\"maxEntries\":500,\"keepRestored\":true," +
        "\"showCountInCapsule\":true}";

    private readonly bool _ownsStore;

    public RuntimeFixture(
        FakeHost host,
        string settingsJson = DefaultSettingsJson,
        Action<IArchiveRepository>? seed = null,
        ArchiveStore? store = null)
    {
        _ownsStore = store is null;
        Scope = _ownsStore ? new TempScope("runtime") : null;
        Store = store ?? new ArchiveStore(Scope!.StorePath);
        Store.Load();

        Host = host;
        Settings = new FakeRuntimeSettings(settingsJson);
        GlobalTopBar = new FakeGlobalTopBar();
        Shortcuts = new FakeGlobalShortcuts();
        Papers = new FakeRuntimePapers();
        State = new FakeRuntimeState();
        Context = new PaperPluginRuntimeContext
        {
            ProviderId = ProviderId,
            ApiVersion = "2.2",
            UiLanguage = "en-US",
            GrantedPermissions = PaperTodoPermissionNames.All,
            Workspace = host,
            Settings = Settings,
            State = State,
            Papers = Papers,
            GlobalTopBar = GlobalTopBar,
            GlobalShortcuts = Shortcuts
        };

        seed?.Invoke(Store);
        Runtime = new ArchiveRuntime(Context, Store);
    }

    public TempScope? Scope { get; }

    public ArchiveStore Store { get; }

    public FakeHost Host { get; }

    public FakeRuntimeSettings Settings { get; }

    public FakeGlobalTopBar GlobalTopBar { get; }

    public FakeGlobalShortcuts Shortcuts { get; }

    public FakeRuntimePapers Papers { get; }

    public FakeRuntimeState State { get; }

    public PaperPluginRuntimeContext Context { get; }

    public ArchiveRuntime Runtime { get; }

    public void Dispose()
    {
        // Disposing twice is harmless and is proven by check 22; this also covers failed checks.
        Runtime.Dispose();
        if (!_ownsStore)
        {
            return;
        }

        Store.Dispose();
        Scope!.Dispose();
    }
}

/// <summary>
/// Runtime checks: real contribution registration, real handler invocation and real event capture.
/// Every case builds its own isolated store, so a run can never touch the user's archive.
/// </summary>
internal static class RuntimeChecks
{
    public static void RegistrationFollowsLivePapers()
    {
        var host = new FakeHost();
        var todo = host.AddTodoPaper("注册检查待办", ("第一行", false), ("第二行", true));
        var note = host.AddNotePaper("注册检查笔记", "# 正文");
        var own = host.AddNotePaper("归档中心", "# 归档中心正文", bodyProviderId: RuntimeFixture.ProviderId);
        using var fixture = new RuntimeFixture(host);

        // Global top bar: the archive action plus the open-center entry point.
        Require.NotNull(fixture.GlobalTopBar.Handler, "the Runtime must register a global top-bar handler");
        Require.True(fixture.GlobalTopBar.SetActionsCount >= 1, "the Runtime must publish its global actions at start");
        var actions = fixture.GlobalTopBar.CurrentActions;
        Require.Equal(2, actions.Count,
            $"topBarAction is on, so exactly the archive and open-center actions are expected, saw [{Describe(actions)}]");
        var archiveAction = actions.SingleOrDefault(
            action => string.Equals(action.ToolTip, ArchiveText.Get("TopBarArchiveTooltip"), StringComparison.Ordinal));
        Require.NotNull(archiveAction,
            $"the archive action must expose its localized tooltip, saw [{Describe(actions)}]");
        Require.Equal(PaperTopBarIconKind.Character, archiveAction!.Icon.Kind,
            "the archive action must use a host-rendered Character icon");
        Require.False(string.IsNullOrWhiteSpace(archiveAction.Icon.Value), "the archive icon glyph must not be empty");
        Require.True(archiveAction.Visible && archiveAction.Enabled, "the archive action must start visible and enabled");
        var centerAction = actions.SingleOrDefault(
            action => string.Equals(action.ToolTip, ArchiveText.Get("TopBarOpenCenterTooltip"), StringComparison.Ordinal));
        Require.NotNull(centerAction,
            $"the open-center action must expose its localized tooltip, saw [{Describe(actions)}]");
        Require.Equal(PaperTopBarIconKind.Character, centerAction!.Icon.Kind, "the open-center icon must be a Character");
        Require.NotEqual(archiveAction.Id, centerAction.Id, "the two global actions must have distinct ids");

        // Paper context menus: every live card except the plugin's own management card.
        var registered = fixture.Host.PaperMenuRegistrations
            .Select(registration => registration.PaperId)
            .ToArray();
        Require.Equal(2, registered.Length,
            $"one context menu per live non-plugin card is expected, saw [{string.Join(", ", registered)}]");
        Require.True(registered.Contains(todo.Id), "the live Todo card must get a context menu");
        Require.True(registered.Contains(note.Id), "the live Note card must get a context menu");
        Require.False(registered.Contains(own.Id), "the plugin's own card must never get an archive context menu");

        foreach (var paperId in new[] { todo.Id, note.Id })
        {
            var menu = fixture.Host.PaperMenuFor(paperId);
            Require.NotNull(menu, "context menu missing for paper " + paperId);
            Require.Equal(2, menu!.Count, $"the context menu of {paperId} must offer archive plus open-center");
            Require.True(menu.Any(action => string.Equals(action.Text, ArchiveText.Get("MenuArchive"), StringComparison.Ordinal)),
                $"an unarchived card must offer the archive entry, saw [{string.Join(" | ", menu.Select(action => action.Text))}]");
            Require.True(menu.Any(action => string.Equals(action.Text, ArchiveText.Get("MenuOpenCenter"), StringComparison.Ordinal)),
                $"the context menu must offer open-center, saw [{string.Join(" | ", menu.Select(action => action.Text))}]");
        }

        // Todo row actions follow the same setting.
        Require.NotNull(fixture.Host.TodoActionHandler, "the Runtime must register a Todo row action handler");
        var rows = fixture.Host.TodoActionRegistrations.Keys.ToArray();
        Require.Equal(todo.Todos.Count, rows.Length,
            $"one Todo row action per live row is expected, saw [{string.Join(", ", rows.Select(row => row.TodoId))}]");
        Require.True(rows.All(row => string.Equals(row.PaperId, todo.Id, StringComparison.Ordinal)),
            "Todo row actions must only target Todo cards");
        foreach (var row in rows)
        {
            var rowActions = fixture.Host.TodoActionsFor(row.PaperId, row.TodoId);
            Require.NotNull(rowActions, "Todo row action missing for " + row.TodoId);
            Require.Equal(1, rowActions!.Count, "a row must contribute exactly one archive entry");
            Require.False(string.IsNullOrWhiteSpace(rowActions[0].Text), "the Todo row action must carry localized text");
        }
    }

    public static void SettingsOffContributesNothing()
    {
        var host = new FakeHost();
        host.AddTodoPaper("关闭贡献待办", ("一行", false));
        host.AddNotePaper("关闭贡献笔记", "# 正文");
        using var fixture = new RuntimeFixture(
            host,
            "{\"topBarAction\":false,\"contextMenuAction\":false}");

        Require.True(fixture.GlobalTopBar.SetActionsCount >= 1,
            "the Runtime must still publish an (empty) action set so stale buttons are removed");
        Require.Equal(0, fixture.GlobalTopBar.CurrentActions.Count,
            $"topBarAction=false must contribute no global action, saw [{Describe(fixture.GlobalTopBar.CurrentActions)}]");
        Require.NotNull(fixture.GlobalTopBar.Handler,
            "the handler must stay registered so a later setting change can re-enable the buttons");

        Require.Equal(0, fixture.Host.PaperMenuRegistrations.Count,
            "contextMenuAction=false must contribute no paper context menu");
        Require.True(fixture.Host.PaperMenuClearCount >= 1,
            "the Runtime must clear stale context menus even when the setting is off");
        Require.Equal(0, fixture.Host.TodoActionRegistrations.Count,
            "contextMenuAction=false must contribute no Todo row action");
        Require.True(fixture.Host.TodoMenuClearCount >= 1,
            "the Runtime must clear stale Todo row actions even when the setting is off");
    }

    public static void TopBarArchiveActionHidesTheCard()
    {
        var host = new FakeHost();
        var paper = host.AddTodoPaper("顶栏归档", ("待办一", false), ("待办二", true));
        using var fixture = new RuntimeFixture(host);
        var handler = fixture.GlobalTopBar.Handler;
        Require.NotNull(handler, "the global top-bar handler must be registered");
        var archiveAction = fixture.GlobalTopBar.CurrentActions.SingleOrDefault(
            action => string.Equals(action.ToolTip, ArchiveText.Get("TopBarArchiveTooltip"), StringComparison.Ordinal));
        Require.NotNull(archiveAction, "the archive action must be part of the published action set");

        host.ResetCalls();
        handler!(new PaperTopBarActionInvocation(
            archiveAction!.Id,
            PaperTopBarActionScope.Global,
            paper.Id,
            paper.Type,
            paper.BodyProviderId));

        Require.Equal(1, host.Count("HidePaper:" + paper.Id),
            $"the archive action must hide the target card, saw [{string.Join(", ", host.Calls)}]");
        Require.Equal(0, host.DeletedPaperIds.Count, "the archive action must never delete the card");
        Require.Equal(0, host.Count("DeletePaper"), "the archive action must never call DeletePaper");
        Require.False(paper.Visible, "the target card must end up hidden");

        var entries = fixture.Store.Snapshot();
        Require.Equal(1, entries.Count, "the archive action must create exactly one record");
        Require.Equal(ArchiveEntryState.Archived, entries[0].State, "the record must stay recoverable (Archived)");
        Require.Equal(paper.Id, entries[0].PaperId, "the record must belong to the invoked card");
        Require.Equal(2, entries[0].Todos.Count, "the record must freeze the full card content");
        Require.Equal(ArchiveCaptureOrigin.User, entries[0].Origin, "a user click must be attributed to the user");
    }

    public static void TodoRowActionArchivesOnlyThatRow()
    {
        var host = new FakeHost();
        var paper = host.AddTodoPaper("行归档", ("保留的行", false), ("归档这一行", false));
        using var fixture = new RuntimeFixture(host);
        var handler = host.TodoActionHandler;
        Require.NotNull(handler, "the Todo row action handler must be registered");
        var row = paper.Todos.Single(todo => todo.Text == "归档这一行");
        var contributed = host.TodoActionsFor(paper.Id, row.Id);
        Require.NotNull(contributed, "the live row must carry the contributed action");
        var current = host.ListTodos(paper.Id).Single(todo => string.Equals(todo.Id, row.Id, StringComparison.Ordinal));

        host.ResetCalls();
        handler!(new PaperTodoActionInvocation(contributed![0].Id, paper.Id, row.Id, current));

        Require.Equal(1, host.DeletedTodos.Count, "the row action must delete exactly one row");
        Require.Equal(row.Id, host.DeletedTodos[0].TodoId, "the archived row is the deleted one");
        Require.Equal(1, paper.Todos.Count, "the unaffected rows must stay on the card");
        Require.Equal(0, host.DeletedPaperIds.Count, "the row action must never delete the card");

        var entries = fixture.Store.Snapshot();
        Require.Equal(1, entries.Count, "the row action must create exactly one record");
        Require.Equal(ArchiveEntryKind.TodoItem, entries[0].Kind, "a single row is a TodoItem record");
        Require.Equal(ArchiveEntryState.Trashed, entries[0].State,
            "the row really left the host, so its record is Trashed");
        Require.Equal(1, entries[0].Todos.Count, "the record must freeze the row");
        Require.Equal("归档这一行", entries[0].Todos[0].Text, "the frozen row text must survive");
    }

    public static void ContextMenuUnarchiveRestores()
    {
        var host = new FakeHost();
        var paper = host.AddNotePaper("菜单恢复", "# 恢复正文", visible: false);
        var seeded = new ArchiveEntry
        {
            Id = "e-menu-unarchive",
            Kind = ArchiveEntryKind.Paper,
            State = ArchiveEntryState.Archived,
            Origin = ArchiveCaptureOrigin.User,
            PaperId = paper.Id,
            PaperType = ArchiveRules.NotePaperType,
            BodyProviderId = ArchiveRules.BuiltinMarkdownProviderId,
            PaperTitle = "菜单恢复",
            Title = "菜单恢复",
            CapturedAt = DateTimeOffset.Now.AddMinutes(-5),
            NoteContent = "# 恢复正文",
            NoteContentAvailable = true
        };
        using var fixture = new RuntimeFixture(
            host,
            seed: store => store.Upsert(seeded, ArchiveChangeKind.EntryAdded));

        var handler = host.PaperActionHandler;
        Require.NotNull(handler, "the paper context-menu handler must be registered");
        var menu = host.PaperMenuFor(paper.Id);
        Require.NotNull(menu, "the archived card must still get a context menu");
        var menuActions = menu!;
        var unarchive = menuActions.SingleOrDefault(
            action => string.Equals(action.Text, ArchiveText.Get("MenuUnarchive"), StringComparison.Ordinal));
        Require.NotNull(unarchive,
            $"an archived card must offer unarchive, saw [{string.Join(" | ", menuActions.Select(action => action.Text))}]");
        Require.False(menuActions.Any(action => string.Equals(action.Text, ArchiveText.Get("MenuArchive"), StringComparison.Ordinal)),
            "an archived card must not offer archive again");

        host.ResetCalls();
        handler!(new PaperActionInvocation(
            unarchive!.Id,
            host.GetPaper(paper.Id)!,
            new PaperPopupPosition(0, 0)));

        Require.Equal(1, host.Count("ShowPaper:" + paper.Id),
            $"unarchive must show the existing card, saw [{string.Join(", ", host.Calls)}]");
        Require.True(paper.Visible, "the restored card must be visible again");
        Require.Equal(0, host.Count("CreatePaper"), "unarchive must not duplicate a surviving card");
        Require.Equal(0, host.DeletedPaperIds.Count, "unarchive must never delete anything");

        var stored = fixture.Store.Find(seeded.Id);
        Require.NotNull(stored, "the record must be kept for history");
        Require.Equal(ArchiveEntryState.Restored, stored!.State, "the record must move to Restored");
        Require.Equal(paper.Id, stored.RestoredPaperId, "RestoredPaperId must point at the shown card");
        Require.Null(fixture.Store.FindEntryIdByArchivedPaper(paper.Id),
            "a restored record must leave the archived-paper index");
    }

    public static void HostDeleteCapturesMirroredNoteBody()
    {
        var host = new FakeHost();
        const string body = "# 秘密正文\n第二行";
        var note = host.AddNotePaper("会被删掉的笔记", body);
        using var fixture = new RuntimeFixture(host);

        // The Runtime mirrors live cards at startup; that mirror is the only copy of the body.
        var mirror = fixture.Store.FindMirror(note.Id);
        Require.NotNull(mirror, "the Runtime must mirror live cards at startup");
        Require.Equal(body, mirror!.NoteContent, "the mirror must hold the note body");
        Require.True(mirror.NoteContentAvailable, "a built-in Markdown body must be marked readable");

        // The host removes the card first, then reports the event; the paper is already gone.
        var snapshot = SnapshotOf(note);
        host.RemovePaperSilently(note.Id);
        host.Raise(new PaperDeletedEvent(snapshot, Metadata(PaperTodoEventOrigin.User)));

        var entries = fixture.Store.Snapshot();
        Require.Equal(1, entries.Count, "a host-side delete must create exactly one trash record");
        var entry = entries[0];
        Require.Equal(ArchiveEntryKind.Paper, entry.Kind, "a whole-card delete produces a Paper record");
        Require.Equal(ArchiveEntryState.Trashed, entry.State, "the content left the host, so the record is Trashed");
        Require.Equal(note.Id, entry.PaperId, "the trash record must belong to the deleted card");
        Require.Equal(body, entry.NoteContent, "the trash record must carry the mirrored note body");
        Require.True(entry.NoteContentAvailable, "the mirrored body must stay restorable");
        Require.Equal(ArchiveCaptureOrigin.User, entry.Origin, "the capture origin must follow the event metadata");
        Require.Equal(ArchiveText.Get("ReasonHostDelete"), entry.Reason, "the record must explain why it was captured");
        Require.Null(fixture.Store.FindMirror(note.Id), "the mirror must be dropped once a record covers it");
    }

    public static void HostDeleteKeepsMirrorWhenCaptureIsOff()
    {
        var host = new FakeHost();
        const string body = "# 不接管删除";
        var note = host.AddNotePaper("不接管删除的笔记", body);
        using var fixture = new RuntimeFixture(host, "{\"captureHostDeletes\":false}");

        var snapshot = SnapshotOf(note);
        host.RemovePaperSilently(note.Id);
        host.Raise(new PaperDeletedEvent(snapshot, Metadata(PaperTodoEventOrigin.User)));

        Require.Equal(0, fixture.Store.Snapshot().Count,
            "captureHostDeletes=false must not create a trash record");
        var mirror = fixture.Store.FindMirror(note.Id);
        Require.NotNull(mirror, "with capture off the mirror must be kept: it is the only copy left");
        Require.Equal(body, mirror!.NoteContent, "the kept mirror must still hold the note body");

        // Enabling host-delete capture on a live Runtime must promote the kept mirror immediately
        // (ArchiveRuntime.OnSettingsChanged re-synchronises when the setting turns on), and a later
        // Runtime start must keep it.
        fixture.Settings.Raise("{\"captureHostDeletes\":true}");
        var livePromoted = fixture.Store.Snapshot();
        Require.Equal(1, livePromoted.Count,
            "enabling captureHostDeletes on a live Runtime must promote the kept mirror immediately");
        Require.Equal(body, livePromoted[0].NoteContent, "the live-promoted record must carry the kept note body");
        Require.Equal(ArchiveEntryState.Trashed, livePromoted[0].State, "the live-promoted record must be Trashed");
        Require.Null(fixture.Store.FindMirror(note.Id), "the mirror must be dropped once a record covers it");
        using var restarted = new RuntimeFixture(
            host,
            "{\"captureHostDeletes\":true}",
            store: fixture.Store);
        var promoted = restarted.Store.Snapshot();
        Require.Equal(1, promoted.Count, "a later Runtime start must keep the promoted record");
        Require.Equal(body, promoted[0].NoteContent, "the promoted record must carry the kept note body");
        Require.Equal(ArchiveEntryState.Trashed, promoted[0].State, "the promoted record must be Trashed");
        Require.Null(restarted.Store.FindMirror(note.Id), "the mirror must be dropped once it is covered by a record");
    }

    public static void StartupReadFailureDoesNotKillTheRuntime()
    {
        var host = new FakeHost();
        host.AddTodoPaper("启动读失败", ("一行", false));
        host.FailListPapers = true;

        // The host can reject a read while it is still starting up; the Runtime must survive it.
        using var fixture = new RuntimeFixture(host);

        Require.True(fixture.GlobalTopBar.SetActionsCount >= 1,
            "the Runtime must still publish its global actions after a failed startup read");
        Require.Equal(0, host.PaperMenuRegistrations.Count,
            "with ListPapers failing no paper menu could be registered yet");
        Require.Equal(1, host.SubscriptionCount, "the Runtime must still subscribe to workspace events");

        // Once the host can answer, the Runtime must resume working instead of staying dead.
        host.FailListPapers = false;
        var extra = host.AddTodoPaper("恢复后的新卡片", ("新行", false));
        host.Raise(new PaperCreatedEvent(SnapshotOf(extra), Metadata(PaperTodoEventOrigin.User)));
        Require.True(
            host.PaperMenuRegistrations.Any(registration =>
                string.Equals(registration.PaperId, extra.Id, StringComparison.Ordinal)),
            "the Runtime must register contributions again once the host answers");

        fixture.Runtime.Dispose();
        fixture.Runtime.Dispose();
        Require.Equal(0, host.SubscriptionCount, "Dispose must still clean up after a failed startup read");
    }

    public static void DisposeClearsEveryContribution()
    {
        var host = new FakeHost();
        host.AddTodoPaper("释放检查", ("一行", false));
        // The fixture cleans up its temp store at the end of the method; the Runtime itself is
        // disposed twice inside the check on purpose.
        using var fixture = new RuntimeFixture(host);

        Require.NotNull(fixture.GlobalTopBar.Handler, "precondition: the global handler is registered");
        Require.NotNull(host.PaperActionHandler, "precondition: the paper handler is registered");
        Require.NotNull(host.TodoActionHandler, "precondition: the Todo handler is registered");
        Require.True(fixture.GlobalTopBar.CurrentActions.Count > 0, "precondition: global actions are published");
        Require.True(host.PaperMenuRegistrations.Count > 0, "precondition: paper menus are published");
        Require.True(host.TodoActionRegistrations.Count > 0, "precondition: Todo row actions are published");
        Require.Equal(1, host.SubscriptionCount, "precondition: exactly one workspace subscription");
        Require.Equal(1, fixture.Settings.SubscriptionCount, "precondition: exactly one settings subscription");
        var globalClears = fixture.GlobalTopBar.ClearCount;
        var paperClears = host.PaperMenuClearCount;
        var todoClears = host.TodoMenuClearCount;

        fixture.Runtime.Dispose();

        Require.Null(fixture.GlobalTopBar.Handler, "Dispose must drop the global top-bar handler");
        Require.True(fixture.GlobalTopBar.ClearCount > globalClears, "Dispose must clear the global top-bar actions");
        Require.Equal(0, fixture.GlobalTopBar.CurrentActions.Count, "no global action may survive Dispose");
        Require.Null(host.PaperActionHandler, "Dispose must drop the paper context-menu handler");
        Require.True(host.PaperMenuClearCount > paperClears, "Dispose must clear the paper context menus");
        Require.Equal(0, host.PaperMenuRegistrations.Count, "no paper menu may survive Dispose");
        Require.Null(host.TodoActionHandler, "Dispose must drop the Todo row action handler");
        Require.True(host.TodoMenuClearCount > todoClears, "Dispose must clear the Todo row actions");
        Require.Equal(0, host.TodoActionRegistrations.Count, "no Todo row action may survive Dispose");
        Require.Equal(0, host.SubscriptionCount, "Dispose must unsubscribe from the workspace");
        Require.Equal(0, fixture.Settings.SubscriptionCount, "Dispose must unsubscribe from the settings");

        // Nothing may re-register or write after the lease is gone, and nothing may throw.
        var entriesAfterDispose = fixture.Store.Snapshot().Count;
        var created = host.AddTodoPaper("释放后新增", ("迟到的一行", false));
        host.Raise(new PaperCreatedEvent(SnapshotOf(created), Metadata(PaperTodoEventOrigin.User)));
        fixture.Settings.Raise("{\"topBarAction\":true,\"contextMenuAction\":true}");
        Require.Null(fixture.GlobalTopBar.Handler, "a late event must not resurrect the global handler");
        Require.Null(host.PaperActionHandler, "a late event must not resurrect the paper handler");
        Require.Equal(entriesAfterDispose, fixture.Store.Snapshot().Count,
            "a late event must not write archive records after Dispose");

        // Disposing again is safe (the host may call Dispose twice on a crashed runtime).
        fixture.Runtime.Dispose();
        Require.Null(fixture.GlobalTopBar.Handler, "a second Dispose must leave the surface clean");
    }

    // ---------------------------------------------------------------- helpers

    private static PaperSnapshot SnapshotOf(FakePaper paper) => new(
        paper.Id,
        paper.Type,
        paper.Title,
        paper.Visible,
        paper.Collapsed,
        paper.AlwaysOnTop,
        paper.BodyProviderId);

    private static PaperTodoEventMetadata Metadata(PaperTodoEventOrigin origin) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        DateTimeOffset.Now,
        origin,
        null);

    private static string Describe(IReadOnlyList<PaperTopBarAction> actions) =>
        actions.Count == 0
            ? "empty"
            : string.Join(", ", actions.Select(action => $"{action.Id}:'{action.ToolTip}'"));
}
