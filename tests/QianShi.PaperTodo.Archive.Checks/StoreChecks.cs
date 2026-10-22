using System.IO;

namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>
/// Store checks. Every case uses the internal <c>ArchiveStore(string path)</c> constructor with a
/// unique temp file, so a run can never touch the user's real archive.
/// </summary>
internal static class StoreChecks
{
    public static void UpsertFindSnapshotAndIndex()
    {
        using var scope = new TempScope(nameof(UpsertFindSnapshotAndIndex));
        using var store = new ArchiveStore(scope.StorePath);
        store.Load();

        var start = new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var archived = Entry("e-archived", ArchiveEntryKind.Paper, ArchiveEntryState.Archived, "p-archived", start);
        var trashed = Entry("e-trashed", ArchiveEntryKind.Paper, ArchiveEntryState.Trashed, "p-trashed", start.AddHours(3));
        var todoItem = Entry("e-todo-item", ArchiveEntryKind.TodoItem, ArchiveEntryState.Archived, "p-todo-item", start.AddHours(2));
        var restored = Entry("e-restored", ArchiveEntryKind.Paper, ArchiveEntryState.Restored, "p-restored", start.AddHours(1));
        store.Upsert(archived, ArchiveChangeKind.EntryAdded);
        store.Upsert(trashed, ArchiveChangeKind.EntryAdded);
        store.Upsert(todoItem, ArchiveChangeKind.EntryAdded);
        store.Upsert(restored, ArchiveChangeKind.EntryAdded);

        Require.SequenceEqual(
            new[] { "e-trashed", "e-todo-item", "e-restored", "e-archived" },
            store.Snapshot().Select(entry => entry.Id),
            "Snapshot must be newest first");

        Require.Equal("e-archived", store.Find("e-archived")?.Id, "Find by id");
        Require.Null(store.Find("e-missing"), "Find of an unknown id");
        Require.Null(store.Find(string.Empty), "Find of an empty id");

        // Only an Archived Paper record may claim a paper: a Trashed/Restored Paper record and an
        // Archived TodoItem record never do.
        Require.Equal("e-archived", store.FindEntryIdByArchivedPaper("p-archived"), "Archived paper record is indexed");
        Require.Null(store.FindEntryIdByArchivedPaper("p-trashed"), "Trashed paper record must not be indexed");
        Require.Null(store.FindEntryIdByArchivedPaper("p-restored"), "Restored paper record must not be indexed");
        Require.Null(store.FindEntryIdByArchivedPaper("p-todo-item"), "TodoItem record must not be indexed as a paper");
        Require.Null(store.FindEntryIdByArchivedPaper("p-unknown"), "Unknown paper id must not be indexed");

        // The index follows state transitions instead of only insertions.
        var updated = store.Find("e-archived")!;
        updated.State = ArchiveEntryState.Restored;
        store.Upsert(updated);
        Require.Null(store.FindEntryIdByArchivedPaper("p-archived"), "Restoring a record must drop its paper index entry");
        Require.Equal(1, store.Snapshot().Count(entry => entry.Id == "e-archived"), "Upsert must update, not duplicate");

        var rearchived = store.Find("e-archived")!;
        rearchived.State = ArchiveEntryState.Archived;
        store.Upsert(rearchived);
        Require.Equal("e-archived", store.FindEntryIdByArchivedPaper("p-archived"), "Re-archiving must index the paper again");

        // Snapshots and Find results are clones: a caller cannot corrupt the store by mutating them.
        store.Find("e-trashed")!.Title = "mutated by caller";
        Require.Equal("Title e-trashed", store.Find("e-trashed")!.Title, "Find must return a clone");
        store.Snapshot().First(entry => entry.Id == "e-trashed").Title = "mutated again";
        Require.Equal("Title e-trashed", store.Find("e-trashed")!.Title, "Snapshot must return clones");

        // Statistics counts record states, independent of record kind.
        var statistics = store.Statistics();
        Require.Equal(2, statistics.Archived, "Statistics.Archived");
        Require.Equal(1, statistics.Trashed, "Statistics.Trashed");
        Require.Equal(1, statistics.Restored, "Statistics.Restored");
        Require.Equal(0, statistics.MirroredPapers, "Statistics.MirroredPapers");
    }

    public static void SaveLoadRoundTrip()
    {
        using var scope = new TempScope(nameof(SaveLoadRoundTrip));
        var capturedAt = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(8));
        var reminder = new DateTimeOffset(2025, 5, 6, 7, 8, 9, TimeSpan.Zero);

        using (var first = new ArchiveStore(scope.StorePath))
        {
            first.Load();
            first.Upsert(new ArchiveEntry
            {
                Id = "e-roundtrip",
                Kind = ArchiveEntryKind.Paper,
                State = ArchiveEntryState.Archived,
                Origin = ArchiveCaptureOrigin.User,
                PaperId = "p-roundtrip",
                PaperType = ArchiveRules.TodoPaperType,
                BodyProviderId = ArchiveRules.BuiltinMarkdownProviderId,
                PaperTitle = "Round trip paper",
                Title = "Round trip paper",
                CapturedAt = capturedAt,
                Reason = "ReasonUserArchive",
                Todos =
                [
                    new ArchiveTodoRecord
                    {
                        Id = "t1",
                        Text = "第一条中文待办",
                        Done = true,
                        Order = 0,
                        LinkedPaperId = "p-link",
                        LinkedPath = @"C:\temp",
                        LinkedPathIsDirectory = false,
                        ReminderAt = reminder
                    },
                    new ArchiveTodoRecord
                    {
                        Id = "t2",
                        Text = "second row",
                        Done = false,
                        Order = 1
                    }
                ]
            }, ArchiveChangeKind.EntryAdded);

            first.Upsert(new ArchiveEntry
            {
                Id = "e-note",
                Kind = ArchiveEntryKind.Paper,
                State = ArchiveEntryState.Trashed,
                Origin = ArchiveCaptureOrigin.System,
                PaperId = "p-note",
                PaperType = ArchiveRules.NotePaperType,
                BodyProviderId = ArchiveRules.BuiltinMarkdownProviderId,
                PaperTitle = "Note paper",
                Title = "Note paper",
                CapturedAt = capturedAt.AddMinutes(-5),
                NoteContent = "# 标题\n第二行 **正文**",
                NoteContentAvailable = true
            }, ArchiveChangeKind.EntryAdded);

            first.SetMirror(new PaperMirror
            {
                PaperId = "p-mirror",
                PaperType = ArchiveRules.NotePaperType,
                Title = "Mirrored note",
                BodyProviderId = ArchiveRules.BuiltinMarkdownProviderId,
                IsVisible = false,
                CapturedAt = capturedAt,
                NoteContent = "mirror body",
                NoteContentAvailable = true,
                Todos =
                [
                    new ArchiveTodoRecord { Id = "m1", Text = "mirror row", Done = false, Order = 0 }
                ]
            });

            first.Save(immediate: true);
            Require.True(File.Exists(scope.StorePath), "Save(immediate: true) must write the store file");
        }

        using var second = new ArchiveStore(scope.StorePath);
        second.Load();

        var entries = second.Snapshot();
        Require.Equal(2, entries.Count, "Every record must round-trip");

        var roundtrip = second.Find("e-roundtrip");
        Require.NotNull(roundtrip, "Find after reload");
        Require.Equal(ArchiveEntryState.Archived, roundtrip!.State, "Record state round-trips");
        Require.Equal(ArchiveCaptureOrigin.User, roundtrip.Origin, "Capture origin round-trips");
        Require.Equal(capturedAt, roundtrip.CapturedAt, "CapturedAt round-trips");
        Require.Equal("Round trip paper", roundtrip.PaperTitle, "Paper title round-trips");
        Require.Equal(2, roundtrip.Todos.Count, "Todo rows round-trip");
        Require.Equal("第一条中文待办", roundtrip.Todos[0].Text, "Todo text round-trips");
        Require.True(roundtrip.Todos[0].Done, "Todo completion round-trips");
        Require.Equal(1, roundtrip.Todos[1].Order, "Todo order round-trips");
        Require.Equal("p-link", roundtrip.Todos[0].LinkedPaperId, "Todo linked paper round-trips");
        Require.Equal(@"C:\temp", roundtrip.Todos[0].LinkedPath, "Todo linked path round-trips");
        Require.Equal<bool?>(false, roundtrip.Todos[0].LinkedPathIsDirectory, "Todo linked path kind round-trips");
        Require.Equal(reminder, roundtrip.Todos[0].ReminderAt, "Todo reminder round-trips");

        var note = second.Find("e-note");
        Require.NotNull(note, "Note record must round-trip");
        Require.Equal("# 标题\n第二行 **正文**", note!.NoteContent, "Note content round-trips");
        Require.True(note.NoteContentAvailable, "Note content availability round-trips");

        var mirror = second.FindMirror("p-mirror");
        Require.NotNull(mirror, "Mirror must round-trip");
        Require.Equal("mirror body", mirror!.NoteContent, "Mirror note content round-trips");
        Require.Equal("Mirrored note", mirror.Title, "Mirror title round-trips");
        Require.Equal(false, mirror.IsVisible, "Mirror visibility round-trips");
        Require.Equal(1, mirror.Todos.Count, "Mirror todo rows round-trip");
        Require.Equal(1, second.SnapshotMirrors().Count, "SnapshotMirrors round-trips");

        Require.Equal("e-roundtrip", second.FindEntryIdByArchivedPaper("p-roundtrip"),
            "The archived-paper index must be rebuilt on load");
        Require.Null(second.FindEntryIdByArchivedPaper("p-note"),
            "A Trashed record must not be indexed after reload");
        Require.Equal(1, second.Statistics().Archived, "Statistics after reload");
        Require.Equal(1, second.Statistics().MirroredPapers, "Statistics mirrors after reload");
    }

    public static void RemoveDropsRecordAndIndex()
    {
        using var scope = new TempScope(nameof(RemoveDropsRecordAndIndex));
        const string entryId = "e-remove";
        using (var store = new ArchiveStore(scope.StorePath))
        {
            store.Load();
            store.Upsert(
                Entry(entryId, ArchiveEntryKind.Paper, ArchiveEntryState.Archived, "p-remove", DateTimeOffset.Now),
                ArchiveChangeKind.EntryAdded);
            Require.Equal(entryId, store.FindEntryIdByArchivedPaper("p-remove"), "Index before removal");

            store.Remove(entryId);
            Require.Null(store.Find(entryId), "Remove must drop the record");
            Require.Null(store.FindEntryIdByArchivedPaper("p-remove"), "Remove must drop the paper index entry");
            Require.Equal(0, store.Statistics().Archived, "Statistics must drop the removed record");

            // Idempotent and safe on unknown input.
            store.Remove(entryId);
            store.Remove("e-does-not-exist");
            store.Remove(string.Empty);
            store.Save(immediate: true);
        }

        using var reloaded = new ArchiveStore(scope.StorePath);
        reloaded.Load();
        Require.Equal(0, reloaded.Snapshot().Count, "A removed record must not come back after reload");
        Require.Null(reloaded.FindEntryIdByArchivedPaper("p-remove"),
            "A removed paper index entry must not come back after reload");
    }

    public static void RetentionRules()
    {
        var now = DateTimeOffset.Now;

        // A: RetentionDays expires old Trashed records; Archived records are never expired.
        using (var scope = new TempScope("retention-days"))
        using (var store = new ArchiveStore(scope.StorePath))
        {
            store.Load();
            store.Upsert(Entry("r-old-archived", ArchiveEntryKind.Paper, ArchiveEntryState.Archived, "p-keep", now.AddDays(-400)), ArchiveChangeKind.EntryAdded);
            store.Upsert(Entry("r-old-trashed", ArchiveEntryKind.Paper, ArchiveEntryState.Trashed, "p-old-trash", now.AddDays(-400)), ArchiveChangeKind.EntryAdded);
            store.Upsert(Entry("r-new-trashed", ArchiveEntryKind.Paper, ArchiveEntryState.Trashed, "p-new-trash", now.AddDays(-1)), ArchiveChangeKind.EntryAdded);
            store.Upsert(Entry("r-new-restored", ArchiveEntryKind.Paper, ArchiveEntryState.Restored, "p-new-restored", now.AddDays(-1)), ArchiveChangeKind.EntryAdded);

            store.ApplyRetention(new ArchiveRetention(RetentionDays: 30, MaxEntries: 5000, KeepRestored: true));

            var ids = store.Snapshot().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            Require.False(ids.Contains("r-old-trashed"), "RetentionDays must drop Trashed records older than the window");
            Require.True(ids.Contains("r-old-archived"), "Archived records must never be pruned, however old they are");
            Require.True(ids.Contains("r-new-trashed"), "Recent Trashed records must survive retention");
            Require.True(ids.Contains("r-new-restored"), "Recent Restored records must survive when KeepRestored is on");
            Require.Equal("r-old-archived", store.FindEntryIdByArchivedPaper("p-keep"),
                "The archived-paper index must survive retention");
            Require.Null(store.FindEntryIdByArchivedPaper("p-old-trash"),
                "A pruned Trashed record must not keep a paper index entry");
        }

        // B: KeepRestored=false drops Restored records only.
        using (var scope = new TempScope("retention-keep-restored"))
        using (var store = new ArchiveStore(scope.StorePath))
        {
            store.Load();
            store.Upsert(Entry("k-restored-old", ArchiveEntryKind.Paper, ArchiveEntryState.Restored, "p-r-old", now.AddDays(-60)), ArchiveChangeKind.EntryAdded);
            store.Upsert(Entry("k-restored-new", ArchiveEntryKind.Paper, ArchiveEntryState.Restored, "p-r-new", now.AddDays(-1)), ArchiveChangeKind.EntryAdded);
            store.Upsert(Entry("k-trashed", ArchiveEntryKind.Paper, ArchiveEntryState.Trashed, "p-t", now.AddDays(-1)), ArchiveChangeKind.EntryAdded);
            store.Upsert(Entry("k-archived", ArchiveEntryKind.Paper, ArchiveEntryState.Archived, "p-a", now.AddDays(-500)), ArchiveChangeKind.EntryAdded);

            store.ApplyRetention(new ArchiveRetention(RetentionDays: 0, MaxEntries: 5000, KeepRestored: false));

            var ids = store.Snapshot().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            Require.False(ids.Contains("k-restored-old"), "KeepRestored=false must drop old Restored records");
            Require.False(ids.Contains("k-restored-new"), "KeepRestored=false must drop recent Restored records too");
            Require.True(ids.Contains("k-trashed"), "KeepRestored=false must not drop Trashed records");
            Require.True(ids.Contains("k-archived"), "KeepRestored=false must not drop Archived records");
        }

        // C: MaxEntries caps only the history tail.
        using (var scope = new TempScope("retention-max-entries"))
        using (var store = new ArchiveStore(scope.StorePath))
        {
            store.Load();
            for (var index = 1; index <= 5; index++)
            {
                store.Upsert(
                    Entry($"c-trash-{index}", ArchiveEntryKind.Paper, ArchiveEntryState.Trashed, $"p-c-{index}", now.AddDays(-index)),
                    ArchiveChangeKind.EntryAdded);
            }

            store.Upsert(Entry("c-archived", ArchiveEntryKind.Paper, ArchiveEntryState.Archived, "p-c-archived", now.AddDays(-6)), ArchiveChangeKind.EntryAdded);

            store.ApplyRetention(new ArchiveRetention(RetentionDays: 0, MaxEntries: 2, KeepRestored: true));

            var ids = store.Snapshot().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            Require.Equal(3, ids.Count, "MaxEntries=2 keeps two history records plus the archived record");
            Require.True(ids.Contains("c-trash-1") && ids.Contains("c-trash-2"), "MaxEntries keeps the newest history records");
            Require.False(ids.Contains("c-trash-3") || ids.Contains("c-trash-4") || ids.Contains("c-trash-5"),
                "MaxEntries drops the oldest history records");
            Require.True(ids.Contains("c-archived"), "Archived records sit outside the history cap");
        }

        // D: Archived records are never capped, even above MaxEntries.
        using (var scope = new TempScope("retention-archived-never-capped"))
        using (var store = new ArchiveStore(scope.StorePath))
        {
            store.Load();
            for (var index = 1; index <= 3; index++)
            {
                store.Upsert(
                    Entry($"d-archived-{index}", ArchiveEntryKind.Paper, ArchiveEntryState.Archived, $"p-d-a-{index}", now.AddDays(-index)),
                    ArchiveChangeKind.EntryAdded);
                store.Upsert(
                    Entry($"d-trashed-{index}", ArchiveEntryKind.Paper, ArchiveEntryState.Trashed, $"p-d-t-{index}", now.AddDays(-index)),
                    ArchiveChangeKind.EntryAdded);
            }

            store.ApplyRetention(new ArchiveRetention(RetentionDays: 0, MaxEntries: 1, KeepRestored: true));

            var ids = store.Snapshot().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            Require.Equal(4, ids.Count, "MaxEntries=1 keeps every Archived record plus one history record");
            Require.Equal(3, ids.Count(id => id.StartsWith("d-archived-", StringComparison.Ordinal)),
                "Every Archived record must survive MaxEntries");
            Require.True(ids.Contains("d-trashed-1"), "The newest history record survives MaxEntries");
            Require.Equal("d-archived-1", store.FindEntryIdByArchivedPaper("p-d-a-1"),
                "The archived-paper index must still map surviving Archived records");
        }
    }

    public static void CorruptJsonIsPreservedAndNeverThrows()
    {
        using var scope = new TempScope(nameof(CorruptJsonIsPreservedAndNeverThrows));

        const string garbage = "{ this is not valid json ";
        File.WriteAllText(scope.StorePath, garbage);
        using (var store = new ArchiveStore(scope.StorePath))
        {
            var threw = false;
            try
            {
                store.Load();
            }
            catch (Exception ex)
            {
                threw = true;
                throw new CheckFailedException($"Load must not throw on corrupt JSON, but threw {ex.GetType().Name}: {ex.Message}");
            }

            Require.False(threw, "Load must not throw on corrupt JSON");
            Require.Equal(0, store.Snapshot().Count, "Corrupt JSON must yield an empty document");
            var lastError = store.LastError ?? string.Empty;
            Require.True(lastError.Length > 0, "LastError must report the corrupt read");
            Require.True(
                lastError.Contains("corrupt", StringComparison.OrdinalIgnoreCase),
                $"LastError must mention the preserved corrupt copy, actual <{lastError}>");

            var copies = Directory.GetFiles(scope.Root, "archive-store.json.corrupt-*");
            Require.Equal(1, copies.Length, "Exactly one .corrupt-* copy must be kept");
            Require.Equal(garbage, File.ReadAllText(copies[0]), "The .corrupt copy must hold the original bytes");
        }

        // A JSON document that deserializes to null is preserved the same way.
        var nullPath = Path.Combine(scope.Root, "null-document.json");
        File.WriteAllText(nullPath, "null");
        using (var store = new ArchiveStore(nullPath))
        {
            store.Load();
            Require.Equal(0, store.Snapshot().Count, "A null JSON document must yield an empty store");
            Require.NotNull(store.LastError, "A null JSON document must set LastError");
            Require.Equal(1, Directory.GetFiles(scope.Root, "null-document.json.corrupt-*").Length,
                "A null JSON document must be preserved as .corrupt-*");
        }
    }

    public static void NewerVersionIsLeftUntouched()
    {
        using var scope = new TempScope(nameof(NewerVersionIsLeftUntouched));
        const string newerDocument =
            "{\"version\":999,\"entries\":[{\"id\":\"e-future\",\"kind\":0,\"state\":0,\"paperId\":\"p-future\",\"paperType\":\"todo\",\"title\":\"future\"}],\"mirrors\":{},\"archivedPaperIndex\":{\"p-future\":\"e-future\"}}";
        File.WriteAllText(scope.StorePath, newerDocument);

        using (var store = new ArchiveStore(scope.StorePath))
        {
            store.Load();
            Require.Equal(0, store.Snapshot().Count, "A future document version must yield no records");
            Require.Null(store.FindEntryIdByArchivedPaper("p-future"), "A future document must not populate the paper index");
            Require.Equal(0, store.SnapshotMirrors().Count, "A future document must yield no mirrors");
            var futureError = store.LastError ?? string.Empty;
            Require.True(futureError.Length > 0, "Reading a future document must set LastError");
            Require.True(
                futureError.Contains("newer", StringComparison.OrdinalIgnoreCase),
                $"LastError must mention the newer version, actual <{futureError}>");
            Require.Equal(newerDocument, File.ReadAllText(scope.StorePath),
                "Loading a future document must leave the file byte-identical on disk");
        }

        Require.Equal(newerDocument, File.ReadAllText(scope.StorePath),
            "Disposing without a mutation must not rewrite a future document");

        // A future-version document latches the store read-only. This is the exact sequence the
        // provider Runtime performs at startup (Load, then mirror seeding), so it must be asserted,
        // not just observed: a downgrade must never rewrite newer records with an older shape.
        using (var store = new ArchiveStore(scope.StorePath))
        {
            store.Load();
            store.SetMirror(new PaperMirror { PaperId = "p-probe", Title = "probe" });
            store.Save(immediate: true);
            Require.Equal(newerDocument, File.ReadAllText(scope.StorePath),
                "A mutation after a future-version read must not rewrite the document");
            Require.True(
                (store.LastError ?? string.Empty).Contains("newer", StringComparison.OrdinalIgnoreCase),
                "The store must still report the newer-version refusal after a blocked write");
        }
    }

    private static ArchiveEntry Entry(
        string id,
        ArchiveEntryKind kind,
        ArchiveEntryState state,
        string paperId,
        DateTimeOffset capturedAt) => new()
    {
        Id = id,
        Kind = kind,
        State = state,
        Origin = ArchiveCaptureOrigin.User,
        PaperId = paperId,
        PaperType = ArchiveRules.TodoPaperType,
        BodyProviderId = ArchiveRules.BuiltinMarkdownProviderId,
        PaperTitle = "Title " + id,
        Title = "Title " + id,
        CapturedAt = capturedAt
    };
}
