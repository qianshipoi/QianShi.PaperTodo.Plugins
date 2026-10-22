namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>
/// Archive/restore behavior checks. <see cref="ArchiveService"/> is built exactly the way the
/// provider Runtime and the management surface build it: one ArchiveHostContext over the fake host,
/// a real isolated ArchiveStore in a temp directory and a settings accessor.
/// </summary>
internal static class ArchiveChecks
{
    public static void ArchiveHidesAndFreezesContent()
    {
        using var fixture = new ArchiveFixture();
        var paper = fixture.Host.AddTodoPaper("项目计划", ("写文档", true), ("跑测试", false), ("发布", false));
        fixture.Host.ResetCalls();

        var result = fixture.Service.ArchivePaper(paper.Id);

        Require.True(result.Ok, $"ArchivePaper failed: {result.Code} {result.Detail}");
        Require.Equal(ArchiveResultCodes.ArchivePaper, result.Code, "ArchivePaper result code");
        Require.Equal(0, fixture.Host.Count("DeletePaper"), "Archiving must never call DeletePaper");
        Require.Equal(0, fixture.Host.DeletedPaperIds.Count, "Archiving must never delete the paper");
        Require.Equal(0, fixture.Host.Count("DeleteTodo"), "Archiving a card must not delete todo rows");
        Require.Equal(1, fixture.Host.Count("HidePaper:" + paper.Id), "Archiving must hide exactly that card");
        Require.True(fixture.Host.HiddenPaperIds.Contains(paper.Id), "The archived card must be hidden");
        Require.True(fixture.Service.IsArchived(paper.Id), "IsArchived must report the archived card");

        var entries = fixture.Store.Snapshot();
        Require.Equal(1, entries.Count, "Archiving must create exactly one record");
        var entry = entries[0];
        Require.Equal(ArchiveEntryKind.Paper, entry.Kind, "Record kind");
        Require.Equal(ArchiveEntryState.Archived, entry.State, "An archived card keeps its data, so it is Archived, not Trashed");
        Require.Equal(paper.Id, entry.PaperId, "Record paper id");
        Require.Equal(ArchiveRules.TodoPaperType, entry.PaperType, "Record paper type");
        Require.Equal(ArchiveCaptureOrigin.User, entry.Origin, "Archiving by the user must be attributed to the user");
        Require.Equal(ArchiveText.Get("ReasonUserArchive"), entry.Reason, "Record reason must be the localized user-archive text");
        Require.Equal("项目计划", entry.Title, "Record title");

        Require.Equal(3, entry.Todos.Count, "The full todo content must be frozen in the record");
        Require.SequenceEqual(
            new[] { "写文档", "跑测试", "发布" },
            entry.Todos.OrderBy(todo => todo.Order).Select(todo => todo.Text),
            "Frozen todo texts");
        Require.True(entry.Todos.First(todo => todo.Text == "写文档").Done, "Frozen todo completion flags");
        Require.Equal(entry.Id, fixture.Store.FindEntryIdByArchivedPaper(paper.Id),
            "The archived paper must be indexed so a second archive is detectable");
    }

    public static void SecondArchiveReturnsAlreadyArchived()
    {
        using var fixture = new ArchiveFixture();
        var paper = fixture.Host.AddTodoPaper("重复归档", ("一项", false));
        var first = fixture.Service.ArchivePaper(paper.Id);
        Require.True(first.Ok, $"First ArchivePaper failed: {first.Code} {first.Detail}");
        fixture.Host.ResetCalls();

        var second = fixture.Service.ArchivePaper(paper.Id);

        Require.Equal(ArchiveResultCodes.AlreadyArchived, second.Code, "Archiving the same card twice must report AlreadyArchived");
        Require.False(second.Ok, "AlreadyArchived is an error code, not a success");
        Require.Equal(1, fixture.Store.Snapshot().Count, "A repeated archive must not create a second record");
        Require.Equal(0, fixture.Host.Count("DeletePaper"), "A repeated archive must not delete anything");
        Require.Equal(0, fixture.Host.Count("CreatePaper"), "A repeated archive must not create anything");
    }

    public static void PluginOwnedCardReturnsPluginPaper()
    {
        using var fixture = new ArchiveFixture();
        var own = fixture.Host.AddNotePaper("归档中心", "# 归档中心正文", bodyProviderId: ArchiveFixture.ProviderId);
        fixture.Host.ResetCalls();

        var result = fixture.Service.ArchivePaper(own.Id);

        Require.Equal(ArchiveResultCodes.PluginPaper, result.Code,
            $"Archiving the plugin's own card must report PluginPaper, actual {result.Code}");
        Require.False(result.Ok, "PluginPaper is an error code, not a success");
        Require.Equal(0, fixture.Store.Snapshot().Count, "The plugin's own card must not become a record");
        Require.Equal(0, fixture.Host.Count("HidePaper"), "The plugin's own card must not be hidden by archiving it");
        Require.Equal(0, fixture.Host.Count("DeletePaper"), "The plugin's own card must never be deleted");
        Require.False(fixture.Service.IsArchived(own.Id), "The plugin's own card must not be reported as archived");
    }

    public static void HostPaperIsRefusedWithoutMutation()
    {
        var host = new FakeHost();
        var center = host.AddNotePaper("归档中心卡片", "# body");
        using var fixture = new ArchiveFixture(host: host, hostPaperId: center.Id);
        host.ResetCalls();

        var result = fixture.Service.ArchivePaper(center.Id);

        Require.False(result.Ok, "A body session must not archive the card that hosts it");
        Require.In(
            result.Code,
            new[] { ArchiveResultCodes.SelfPaper, ArchiveResultCodes.PluginPaper },
            "Self-archive result code");
        Require.Equal(0, fixture.Store.Snapshot().Count, "A refused self-archive must not create a record");
        Require.Equal(0, host.Count("HidePaper"), "A refused self-archive must not hide the card");
        Require.Equal(0, host.Count("DeletePaper"), "A refused self-archive must not delete anything");
    }

    public static void RestoreArchivedRecordShowsSurvivingPaper()
    {
        using var fixture = new ArchiveFixture();
        var paper = fixture.Host.AddTodoPaper("恢复到原卡片", ("任务一", false), ("任务二", true));
        var archive = fixture.Service.ArchivePaper(paper.Id);
        Require.True(archive.Ok, $"ArchivePaper failed: {archive.Code} {archive.Detail}");
        var entryId = fixture.Store.FindEntryIdByArchivedPaper(paper.Id);
        Require.NotNull(entryId, "The archived paper must be indexed by its entry id");
        fixture.Host.ResetCalls();

        var result = fixture.Service.RestoreEntry(entryId!);

        Require.True(result.Ok, $"RestoreEntry failed: {result.Code} {result.Detail}");
        Require.Equal(ArchiveResultCodes.Restore, result.Code, "RestoreEntry result code");
        Require.Equal(1, fixture.Host.Count("ShowPaper:" + paper.Id), "Restoring a surviving card must show it");
        Require.True(paper.Visible, "The restored card must be visible again");
        Require.Equal(0, fixture.Host.Count("CreatePaper"),
            "Restoring a surviving card must not create a duplicate card");
        Require.Equal(0, fixture.Host.Count("DeletePaper"), "Restoring must never delete anything");

        var entry = fixture.Store.Find(entryId!);
        Require.NotNull(entry, "The record must be kept for history after restoring");
        Require.Equal(ArchiveEntryState.Restored, entry!.State, "The restored record must become Restored");
        Require.Null(fixture.Store.FindEntryIdByArchivedPaper(paper.Id),
            "A restored record must be dropped from the archived-paper index");
        Require.False(fixture.Service.IsArchived(paper.Id), "A restored card is no longer archived");
    }

    public static void RestoreVanishedPaperRecreatesFromSnapshot()
    {
        using var fixture = new ArchiveFixture();
        var paper = fixture.Host.AddTodoPaper("消失的卡片", ("甲", true), ("乙", false));
        var archive = fixture.Service.ArchivePaper(paper.Id);
        Require.True(archive.Ok, $"ArchivePaper failed: {archive.Code} {archive.Detail}");
        var entryId = fixture.Store.FindEntryIdByArchivedPaper(paper.Id);
        Require.NotNull(entryId, "The archived paper must be indexed by its entry id");

        // The host lost the card without telling the plugin; the frozen snapshot is the only source.
        fixture.Host.RemovePaperSilently(paper.Id);
        fixture.Host.ResetCalls();

        var result = fixture.Service.RestoreEntry(entryId!);

        Require.True(result.Ok, $"RestoreEntry of a vanished card failed: {result.Code} {result.Detail}");
        Require.Equal(ArchiveResultCodes.Restore, result.Code, "RestoreEntry result code");
        Require.Equal(1, fixture.Host.CreateRequests.Count, "A vanished card must be recreated exactly once");
        var request = fixture.Host.CreateRequests[0];
        Require.Equal(ArchiveRules.TodoPaperType, request.Type, "Recreated card type");
        Require.Equal("消失的卡片", request.Title, "Recreated card title");
        Require.True(request.Show, "A recreated card should be shown");
        Require.NotNull(request.Todos, "A recreated todo card must carry its frozen rows");
        Require.SequenceEqual(new[] { "甲", "乙" }, request.Todos!.Select(todo => todo.Text), "Recreated todo texts");
        Require.True(request.Todos!.First(todo => todo.Text == "甲").Done, "Recreated todo completion flags");

        var created = fixture.Host.Papers.Last();
        var entry = fixture.Store.Find(entryId!);
        Require.NotNull(entry, "The record must survive the restore");
        Require.Equal(ArchiveEntryState.Restored, entry!.State, "The restored record must become Restored");
        Require.Equal(created.Id, entry.RestoredPaperId, "RestoredPaperId must point at the recreated card");
    }

    public static void RestoreTrashedNoteWritesCapturedContent()
    {
        using var fixture = new ArchiveFixture();
        const string content = "# 标题\n正文内容 **粗体**";
        var entry = TrashedNote(
            id: "e-trashed-note",
            paperId: "p-gone-note",
            title: "会议纪要",
            content: content,
            contentAvailable: true,
            bodyProviderId: ArchiveRules.BuiltinMarkdownProviderId);
        fixture.SeedEntry(entry);
        fixture.Host.ResetCalls();

        var result = fixture.Service.RestoreEntry(entry.Id);

        Require.True(result.Ok, $"RestoreEntry of a trashed note failed: {result.Code} {result.Detail}");
        Require.Equal(ArchiveResultCodes.Restore, result.Code, "RestoreEntry result code");
        Require.Equal(1, fixture.Host.CreateRequests.Count, "Restoring a trashed note must create exactly one card");
        var request = fixture.Host.CreateRequests[0];
        Require.Equal(ArchiveRules.NotePaperType, request.Type, "Recreated note card type");
        Require.Equal("会议纪要", request.Title, "Recreated note card title");
        Require.True(request.Show, "A recreated note should be shown");
        Require.Equal(content, request.Content, "The captured note content must be written back unchanged");

        var stored = fixture.Store.Find(entry.Id);
        Require.NotNull(stored, "The record must survive the restore");
        Require.Equal(ArchiveEntryState.Restored, stored!.State, "The restored record must become Restored");
        Require.Equal(fixture.Host.Papers.Single().Id, stored.RestoredPaperId,
            "RestoredPaperId must point at the recreated note");
    }

    public static void RestoreForeignBodyNoteIsNotRestorable()
    {
        using var fixture = new ArchiveFixture();
        var entry = TrashedNote(
            id: "e-foreign-note",
            paperId: "p-foreign-note",
            title: "第三方笔记",
            content: null,
            contentAvailable: false,
            bodyProviderId: "acme.other.body");
        fixture.SeedEntry(entry);
        fixture.Host.ResetCalls();

        var result = fixture.Service.RestoreEntry(entry.Id);

        Require.False(result.Ok, "A note body owned by another provider must not be reported as restored");
        Require.Equal(ArchiveResultCodes.NotRestorable, result.Code,
            $"A non-builtin note body must report NotRestorable, actual {result.Code}");
        Require.Equal(0, fixture.Host.CreateRequests.Count, "Nothing may be created for an unreadable note body");
        Require.Equal(0, fixture.Host.Mutations().Count,
            $"No host mutation may happen for an unreadable note body, saw: {string.Join(", ", fixture.Host.Mutations())}");

        var stored = fixture.Store.Find(entry.Id);
        Require.NotNull(stored, "An unrestorable record must be kept, not silently dropped");
        Require.Equal(ArchiveEntryState.Trashed, stored!.State, "An unrestorable record must stay Trashed");
    }

    public static void PurgeArchivedRecordDeletesPaper()
    {
        using var fixture = new ArchiveFixture();
        var paper = fixture.Host.AddTodoPaper("彻底删除", ("一条", false), ("两条", true));
        var archive = fixture.Service.ArchivePaper(paper.Id);
        Require.True(archive.Ok, $"ArchivePaper failed: {archive.Code} {archive.Detail}");
        var entryId = fixture.Store.FindEntryIdByArchivedPaper(paper.Id);
        Require.NotNull(entryId, "The archived paper must be indexed by its entry id");
        fixture.Host.ResetCalls();

        var result = fixture.Service.PurgeEntry(entryId!);

        Require.True(result.Ok, $"PurgeEntry of an archived record failed: {result.Code} {result.Detail}");
        Require.Equal(ArchiveResultCodes.Purge, result.Code, "PurgeEntry result code");
        Require.Equal(1, fixture.Host.DeletedPaperIds.Count, "Purging an archived record must delete the hidden paper");
        Require.Equal(paper.Id, fixture.Host.DeletedPaperIds[0], "The deleted paper must be the archived one");
        Require.Null(fixture.Store.Find(entryId!), "Purging an archived record must drop the record");
        Require.Null(fixture.Store.FindEntryIdByArchivedPaper(paper.Id),
            "Purging an archived record must drop its archived-paper index entry");
    }

    public static void PurgeTrashedRecordTouchesNoHostData()
    {
        using var fixture = new ArchiveFixture();
        var entry = new ArchiveEntry
        {
            Id = "e-trashed-purge",
            Kind = ArchiveEntryKind.Paper,
            State = ArchiveEntryState.Trashed,
            Origin = ArchiveCaptureOrigin.System,
            PaperId = "p-already-gone",
            PaperType = ArchiveRules.TodoPaperType,
            BodyProviderId = ArchiveRules.BuiltinMarkdownProviderId,
            Title = "回收站卡片",
            CapturedAt = DateTimeOffset.Now.AddMinutes(-10),
            Todos = [new ArchiveTodoRecord { Id = "t-1", Text = "仅存在于快照里的待办", Order = 0 }]
        };
        fixture.SeedEntry(entry);
        fixture.Host.ResetCalls();

        var result = fixture.Service.PurgeEntry(entry.Id);

        Require.True(result.Ok, $"PurgeEntry of a trashed record failed: {result.Code} {result.Detail}");
        Require.Equal(ArchiveResultCodes.Purge, result.Code, "PurgeEntry result code");
        Require.Equal(0, fixture.Host.DeletedPaperIds.Count, "Purging a trashed record must not delete a host paper");
        Require.Equal(0, fixture.Host.DeletedTodos.Count, "Purging a trashed record must not delete host todos");
        Require.Equal(0, fixture.Host.Count("DeletePaper") + fixture.Host.Count("DeleteTodo"),
            "Purging a trashed record must not even attempt a host deletion");
        Require.Null(fixture.Store.Find(entry.Id), "Purging a trashed record must drop the record");
    }

    public static void RestoreAllOnEmptyArchiveDoesNothing()
    {
        using var fixture = new ArchiveFixture();
        fixture.Host.ResetCalls();

        var result = fixture.Service.RestoreAll();

        Require.Equal(ArchiveResultCodes.NothingToDo, result.Code,
            $"RestoreAll on an empty archive must report NothingToDo, actual {result.Code}");
        Require.False(result.Ok, "NothingToDo is an error code, not a success");
        Require.Equal(0, fixture.Host.Mutations().Count,
            $"RestoreAll on an empty archive must not touch the host, saw: {string.Join(", ", fixture.Host.Mutations())}");
        Require.Equal(0, fixture.Store.Snapshot().Count, "An empty archive stays empty");
    }

    public static void ArchiveUnknownPaperCreatesNothing()
    {
        using var fixture = new ArchiveFixture();
        fixture.Host.ResetCalls();

        var result = fixture.Service.ArchivePaper("p-does-not-exist");

        Require.False(result.Ok, "Archiving an unknown paper must fail");
        Require.Equal(ArchiveResultCodes.NotFound, result.Code,
            $"Archiving an unknown paper must report NotFound, actual {result.Code}");
        Require.Equal(0, fixture.Store.Snapshot().Count, "An unknown paper must not create a record");
        Require.Equal(0, fixture.Host.Count("HidePaper") + fixture.Host.Count("DeletePaper"),
            "An unknown paper must not cause a host mutation");
    }

    public static void RestoreTrashedTodoCardRecreatesRows()
    {
        using var fixture = new ArchiveFixture();
        var entry = new ArchiveEntry
        {
            Id = "e-trashed-todo",
            Kind = ArchiveEntryKind.Paper,
            State = ArchiveEntryState.Trashed,
            Origin = ArchiveCaptureOrigin.System,
            PaperId = "p-gone-todo",
            PaperType = ArchiveRules.TodoPaperType,
            BodyProviderId = ArchiveRules.BuiltinMarkdownProviderId,
            PaperTitle = "回收站待办",
            Title = "回收站待办",
            CapturedAt = DateTimeOffset.Now.AddMinutes(-5),
            Todos =
            [
                new ArchiveTodoRecord { Id = "r1", Text = "还要做的事", Done = false, Order = 0 },
                new ArchiveTodoRecord { Id = "r2", Text = "已经做完的", Done = true, Order = 1 }
            ]
        };
        fixture.SeedEntry(entry);
        fixture.Host.ResetCalls();

        var result = fixture.Service.RestoreEntry(entry.Id);

        Require.True(result.Ok, $"RestoreEntry of a trashed todo card failed: {result.Code} {result.Detail}");
        Require.Equal(ArchiveResultCodes.Restore, result.Code, "RestoreEntry result code");
        Require.Equal(1, fixture.Host.CreateRequests.Count, "Restoring a trashed todo card must create exactly one card");
        var request = fixture.Host.CreateRequests[0];
        Require.Equal(ArchiveRules.TodoPaperType, request.Type, "Recreated card type");
        Require.Equal("回收站待办", request.Title, "Recreated card title");
        Require.NotNull(request.Todos, "A recreated todo card must carry its frozen rows");
        Require.SequenceEqual(new[] { "还要做的事", "已经做完的" }, request.Todos!.Select(todo => todo.Text),
            "Recreated todo texts");
        Require.True(request.Todos!.First(todo => todo.Text == "已经做完的").Done, "Recreated completion flags");

        var stored = fixture.Store.Find(entry.Id);
        Require.NotNull(stored, "The record must survive the restore");
        Require.Equal(ArchiveEntryState.Restored, stored!.State, "The restored record must become Restored");
        Require.Equal(fixture.Host.Papers.Last().Id, stored.RestoredPaperId,
            "RestoredPaperId must point at the recreated card");
    }

    public static void ArchiveTodoRowIsFrozenBeforeDeleting()
    {
        using var fixture = new ArchiveFixture();
        var paper = fixture.Host.AddTodoPaper("单行归档", ("保留的行", false), ("要归档的行", false), ("另一行", true));
        var row = paper.Todos.Single(todo => todo.Text == "要归档的行");
        fixture.Host.ResetCalls();

        var archived = fixture.Service.ArchiveTodoItem(paper.Id, row.Id);

        Require.True(archived.Ok, $"ArchiveTodoItem failed: {archived.Code} {archived.Detail}");
        Require.Equal(ArchiveResultCodes.ArchiveTodoItem, archived.Code, "ArchiveTodoItem result code");
        Require.Equal(1, fixture.Host.DeletedTodos.Count, "Archiving one row must delete exactly that row");
        Require.Equal(row.Id, fixture.Host.DeletedTodos[0].TodoId, "The archived row is the deleted one");
        Require.Equal(2, paper.Todos.Count, "Only the archived row leaves the card");
        Require.Equal(0, fixture.Host.Count("DeletePaper"), "Archiving a row must never delete the card");

        var entries = fixture.Store.Snapshot();
        Require.Equal(1, entries.Count, "Archiving a row must create exactly one record");
        Require.Equal(ArchiveEntryKind.TodoItem, entries[0].Kind, "Row records use the TodoItem kind");
        Require.Equal(ArchiveEntryState.Trashed, entries[0].State,
            "The row really left the host, so its record is Trashed");
        Require.Equal(1, entries[0].Todos.Count, "The record freezes the row content");
        Require.Equal("要归档的行", entries[0].Todos[0].Text, "Frozen row text");

        fixture.Host.ResetCalls();
        var restored = fixture.Service.RestoreEntry(entries[0].Id);

        Require.True(restored.Ok, $"Restoring an archived row failed: {restored.Code} {restored.Detail}");
        Require.Equal(ArchiveResultCodes.Restore, restored.Code, "Row restore result code");
        Require.Equal(1, fixture.Host.AppendRequests.Count, "Restoring a row must append it to the surviving card");
        Require.Equal(paper.Id, fixture.Host.AppendRequests[0].PaperId, "The row goes back to its parent card");
        Require.SequenceEqual(new[] { "要归档的行" }, fixture.Host.AppendRequests[0].Todos.Select(todo => todo.Text),
            "Restored row text");
        Require.Equal(3, paper.Todos.Count, "The row is back on the card");
        Require.Equal(ArchiveEntryState.Restored, fixture.Store.Find(entries[0].Id)!.State,
            "The row record becomes Restored");
    }

    /// <summary>A Trashed note record as the runtime writes it after a host-side delete.</summary>
    private static ArchiveEntry TrashedNote(
        string id,
        string paperId,
        string title,
        string? content,
        bool contentAvailable,
        string bodyProviderId) => new()
    {
        Id = id,
        Kind = ArchiveEntryKind.Paper,
        State = ArchiveEntryState.Trashed,
        Origin = ArchiveCaptureOrigin.System,
        PaperId = paperId,
        PaperType = ArchiveRules.NotePaperType,
        BodyProviderId = bodyProviderId,
        PaperTitle = title,
        Title = title,
        CapturedAt = DateTimeOffset.Now.AddMinutes(-5),
        NoteContent = content,
        NoteContentAvailable = contentAvailable,
        Reason = ArchiveText.Get("ReasonHostDelete")
    };
}
