using System.IO;
using System.Windows;

namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>
/// Executable verification harness for the card-archive plugin. Run with:
/// <c>dotnet run --project QianShi.PaperTodo.Plugins/QianShi.PaperTodo.Archive.Checks</c>
///
/// One PASS/FAIL line per check plus a summary; a failing check never aborts the run and the exit
/// code is 1 when anything failed.
/// </summary>
internal static class Program
{
    private static readonly (string Name, Action Body)[] Checks =
    [
        ("store 1: upsert/find/snapshot order + archived-paper index", StoreChecks.UpsertFindSnapshotAndIndex),
        ("store 2: save/load round-trip (entries, todos, note content, mirrors)", StoreChecks.SaveLoadRoundTrip),
        ("store 3: remove drops record and index entry", StoreChecks.RemoveDropsRecordAndIndex),
        ("store 4: retention keeps archived, prunes history, caps MaxEntries", StoreChecks.RetentionRules),
        ("store 5: corrupt JSON is preserved, reported, never throws", StoreChecks.CorruptJsonIsPreservedAndNeverThrows),
        ("store 6: newer document version is left untouched", StoreChecks.NewerVersionIsLeftUntouched),
        ("archive 7: hides card, freezes full todos, never deletes", ArchiveChecks.ArchiveHidesAndFreezesContent),
        ("archive 8: second archive returns AlreadyArchived", ArchiveChecks.SecondArchiveReturnsAlreadyArchived),
        ("archive 9: plugin-owned card returns PluginPaper", ArchiveChecks.PluginOwnedCardReturnsPluginPaper),
        ("archive 9b: hosting card is refused without mutation", ArchiveChecks.HostPaperIsRefusedWithoutMutation),
        ("restore 10: archived record with live paper is shown again", ArchiveChecks.RestoreArchivedRecordShowsSurvivingPaper),
        ("restore 11: vanished paper is recreated from the frozen snapshot", ArchiveChecks.RestoreVanishedPaperRecreatesFromSnapshot),
        ("restore 12: trashed note is recreated with captured content", ArchiveChecks.RestoreTrashedNoteWritesCapturedContent),
        ("restore 12b: trashed todo card is recreated with its rows", ArchiveChecks.RestoreTrashedTodoCardRecreatesRows),
        ("archive 13: trashed note from another body provider is NotRestorable", ArchiveChecks.RestoreForeignBodyNoteIsNotRestorable),
        ("archive 13b: one todo row is frozen, deleted and restored", ArchiveChecks.ArchiveTodoRowIsFrozenBeforeDeleting),
        ("purge 14a: archived record deletes the paper, then drops the record", ArchiveChecks.PurgeArchivedRecordDeletesPaper),
        ("purge 14b: trashed record drops the record without host deletion", ArchiveChecks.PurgeTrashedRecordTouchesNoHostData),
        ("restore 15: RestoreAll on an empty archive returns NothingToDo", ArchiveChecks.RestoreAllOnEmptyArchiveDoesNothing),
        ("archive 16: unknown paper id returns NotFound without a record", ArchiveChecks.ArchiveUnknownPaperCreatesNothing),
        ("runtime 17: contributions follow every live card, never the plugin's own", RuntimeChecks.RegistrationFollowsLivePapers),
        ("runtime 18: disabled settings contribute no action at all", RuntimeChecks.SettingsOffContributesNothing),
        ("runtime 19: the top-bar archive action hides but never deletes", RuntimeChecks.TopBarArchiveActionHidesTheCard),
        ("runtime 19b: the Todo row action deletes only the frozen row", RuntimeChecks.TodoRowActionArchivesOnlyThatRow),
        ("runtime 20: the context-menu unarchive action shows the card again", RuntimeChecks.ContextMenuUnarchiveRestores),
        ("runtime 21: a host-side delete captures the mirrored note body", RuntimeChecks.HostDeleteCapturesMirroredNoteBody),
        ("runtime 21b: with capture off the mirror is kept, then promoted", RuntimeChecks.HostDeleteKeepsMirrorWhenCaptureIsOff),
        ("runtime 22: Dispose clears every contribution and is idempotent", RuntimeChecks.DisposeClearsEveryContribution),
        ("runtime 23: a failed host read at startup does not kill the Runtime", RuntimeChecks.StartupReadFailureDoesNotKillTheRuntime),
        ("lock 24: a show-all burst is re-hidden by one coalesced sweep", LockChecks.ShowAllBurstIsCoalesced),
        ("lock 25: with the lock off a host reveal restores the record", LockChecks.LockOffAcceptsTheReveal),
        ("lock 26: a restore from the center is never re-hidden", LockChecks.RestoreFromTheCenterWinsOverTheLock),
        ("lock 27: a failed restore rolls the record back to Archived", LockChecks.FailedRestoreRollsBackToArchived),
        ("lock 28: Dispose cancels the pending sweep", LockChecks.DisposeCancelsPendingSweep),
        ("lock 29: enabling the lock at runtime sweeps visible archived cards", LockChecks.LockEnabledAtRuntimeSweepsImmediately)
    ];

    [STAThread]
    private static int Main()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // Encoding is a convenience for the Chinese text in check messages, never a failure.
        }

        // The plugin may marshal host calls through Application.Current.Dispatcher. Creating the
        // application object (without Run) makes the checks behave like the real UI thread caller.
        Application? application = null;
        try
        {
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        catch (Exception ex)
        {
            Console.WriteLine("NOTE  WPF Application could not be created: " + ex.Message);
        }

        var failed = 0;
        foreach (var (name, body) in Checks)
        {
            try
            {
                body();
                Console.WriteLine("PASS  " + name);
            }
            catch (CheckFailedException ex)
            {
                failed++;
                Console.WriteLine("FAIL  " + name);
                Console.WriteLine("        " + ex.Message);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL  " + name);
                Console.WriteLine($"        unexpected {ex.GetType().Name}: {ex.Message}");
                var stack = ex.StackTrace?.Split(Environment.NewLine)[0];
                if (!string.IsNullOrEmpty(stack))
                {
                    Console.WriteLine("        at " + stack.Trim());
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"ARCHIVE_CHECKS_RESULT {Checks.Length - failed}/{Checks.Length} passed; {failed} failed");
        _ = application;
        return failed == 0 ? 0 : 1;
    }
}
