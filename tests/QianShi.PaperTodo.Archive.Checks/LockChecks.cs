using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>
/// Bounded dispatcher pumping. The archive lock coalesces its re-hide sweep on a 200 ms
/// <see cref="DispatcherTimer"/>, and a timer only ticks while the dispatcher is running a message
/// loop — which a console check never does by itself. Every wait here is bounded, so a regression
/// (a sweep that never runs, or one that loops) fails the check instead of hanging the harness.
/// </summary>
internal static class DispatcherPump
{
    public static void Pump(TimeSpan duration)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            Thread.Sleep(duration);
            return;
        }

        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = duration };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            frame.Continue = false;
        };
        stop.Start();

        // Hard cap in case the dispatcher never gets to the timer (blocked or shutting down).
        var watchdog = new System.Threading.Timer(
            _ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)),
            null,
            duration + TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);

        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            watchdog.Dispose();
            stop.Stop();
        }
    }

    /// <summary>Pumps in short slices until the condition holds; bounded by <paramref name="timeout"/>.</summary>
    public static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        if (condition())
        {
            return true;
        }

        while (DateTime.UtcNow < deadline)
        {
            Pump(TimeSpan.FromMilliseconds(50));
            if (condition())
            {
                return true;
            }
        }

        return condition();
    }
}

/// <summary>
/// Archive-lock checks. PaperTodo's show-all paths (tray double-click, tray "show all", the
/// Show/Hide/Toggle shortcut) reveal every card without consulting the plugin, so the Runtime
/// re-hides archived cards from one coalesced sweep.
/// </summary>
internal static class LockChecks
{
    private const string LockOffJson = "{\"lockArchivedHidden\":false}";

    private const string LockOnJson =
        "{\"autoBackup\":true,\"captureHostDeletes\":true,\"lockArchivedHidden\":true}";

    public static void ShowAllBurstIsCoalesced()
    {
        var host = new FakeHost();
        var own = host.AddNotePaper("归档中心", "# 归档中心正文", bodyProviderId: RuntimeFixture.ProviderId);
        own.Visible = true; // visible on purpose: the sweep must skip the plugin's own card

        // Witness: archived and already on screen when the Runtime starts, so the startup sweep must
        // hide it. That is a behavioural gate proving the startup sweep finished before the burst is
        // measured, instead of guessing from call counts.
        var witness = host.AddNotePaper("启动见证", "# 见证正文", visible: true);

        var archived = new List<FakePaper>();
        for (var index = 0; index < 3; index++)
        {
            archived.Add(host.AddNotePaper($"归档卡片{index}", $"# 正文{index}", visible: false));
        }

        var live = host.AddNotePaper("普通卡片", "# 普通正文", visible: false);
        var entries = archived
            .Select((card, index) => ArchivedEntry($"e-burst-{index}", card))
            .ToArray();
        var witnessEntry = ArchivedEntry("e-burst-witness", witness);

        using var fixture = new RuntimeFixture(host, seed: store =>
        {
            foreach (var entry in entries)
            {
                store.Upsert(entry, ArchiveChangeKind.EntryAdded);
            }

            store.Upsert(witnessEntry, ArchiveChangeKind.EntryAdded);
        });

        var startupSwept = DispatcherPump.PumpUntil(
            () => host.Count("HidePaper:" + witness.Id) > 0,
            TimeSpan.FromSeconds(3));
        Require.True(startupSwept,
            $"the startup sweep must hide an archived card that is already on screen, " +
            $"saw [{string.Join(", ", host.Calls)}]");
        host.ResetCalls();

        // A show-all burst: one Visibility event per card, all in the same instant.
        foreach (var card in archived)
        {
            Reveal(host, card);
        }

        Reveal(host, live);

        var settled = DispatcherPump.PumpUntil(
            () => archived.All(card => host.Count("HidePaper:" + card.Id) > 0),
            TimeSpan.FromSeconds(3));
        Require.True(settled,
            $"the lock sweep must hide every revealed archived card, saw [{string.Join(", ", host.Calls)}]");

        // Give a non-coalesced implementation the chance to fire a second sweep.
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(300));

        foreach (var card in archived)
        {
            Require.Equal(1, host.Count("HidePaper:" + card.Id),
                $"archived card {card.Id} must be hidden exactly once by one coalesced sweep");
            Require.False(card.Visible, $"archived card {card.Id} must be hidden again");
        }

        Require.Equal(0, host.Count("HidePaper:" + live.Id),
            "a revealed card without an archive record must stay visible");
        Require.True(live.Visible, "the non-archived card must stay visible");
        Require.Equal(0, host.Count("HidePaper:" + own.Id),
            "the plugin's own management card must never be swept");
        Require.True(own.Visible, "the plugin's own management card must stay visible");
        Require.Equal(0, host.Count("HidePaper:" + witness.Id),
            "an archived card that is already hidden must not be hidden again by the burst sweep");

        var listPaperReads = host.Count("ListPapers");
        Require.Equal(1, listPaperReads,
            $"one coalesced sweep must read the workspace once for the whole burst, saw {listPaperReads} reads " +
            $"for {archived.Count + 1} reveal events");

        // The lock only hides: every record must still be archived and restorable.
        foreach (var entry in entries)
        {
            var stored = fixture.Store.Find(entry.Id);
            Require.NotNull(stored, "the archive record must survive the sweep");
            Require.Equal(ArchiveEntryState.Archived, stored!.State, "the lock must not turn a record into Restored");
            Require.Equal(entry.Id, fixture.Store.FindEntryIdByArchivedPaper(stored.PaperId),
                "the swept card must still be restorable from the Archive Center");
        }
    }

    public static void LockOffAcceptsTheReveal()
    {
        var host = new FakeHost();
        var card = host.AddNotePaper("锁关闭卡片", "# 正文", visible: false);
        var entry = ArchivedEntry("e-lock-off", card);
        using var fixture = new RuntimeFixture(host, LockOffJson,
            seed: store => store.Upsert(entry, ArchiveChangeKind.EntryAdded));

        DispatcherPump.Pump(TimeSpan.FromMilliseconds(300));
        host.ResetCalls();
        Reveal(host, card);

        var accepted = DispatcherPump.PumpUntil(
            () => fixture.Store.Find(entry.Id)?.State == ArchiveEntryState.Restored,
            TimeSpan.FromSeconds(2));
        Require.True(accepted,
            $"with lockArchivedHidden off a host reveal must be accepted as a restore, actual state " +
            $"<{fixture.Store.Find(entry.Id)?.State}>");
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(450)); // well past the 200 ms sweep delay

        Require.Equal(0, host.Count("HidePaper:" + card.Id),
            "with the lock off nothing may be hidden again");
        Require.True(card.Visible, "with the lock off the revealed card stays visible");
        Require.Null(fixture.Store.FindEntryIdByArchivedPaper(card.Id),
            "the accepted reveal must drop the archived-paper index entry");
    }

    public static void RestoreFromTheCenterWinsOverTheLock()
    {
        var host = new FakeHost();
        var card = host.AddNotePaper("中心恢复", "# 恢复正文", visible: false);
        var entry = ArchivedEntry("e-center-restore", card);
        using var fixture = new RuntimeFixture(host, seed: store => store.Upsert(entry, ArchiveChangeKind.EntryAdded));
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(400));
        host.ResetCalls();

        var service = CreateService(host, fixture);
        var result = service.RestoreEntry(entry.Id);

        Require.True(result.Ok, $"RestoreEntry failed: {result.Code} {result.Detail}");
        Require.Equal(ArchiveResultCodes.Restore, result.Code, "RestoreEntry result code");
        Require.Equal(1, host.Count("ShowPaper:" + card.Id), "restoring from the center must show the card");
        Require.True(card.Visible, "the restored card must be visible");

        // The host reports its own reveal of the restored card; the lock must not undo it.
        Reveal(host, card);
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(600));

        Require.Equal(0, host.Count("HidePaper:" + card.Id),
            "the lock must never re-hide a card restored from the Archive Center");
        Require.True(card.Visible, "the restored card must stay on screen after the sweep window");
        Require.Equal(ArchiveEntryState.Restored, fixture.Store.Find(entry.Id)!.State,
            "the restored record must stay Restored");
        Require.Null(fixture.Store.FindEntryIdByArchivedPaper(card.Id),
            "the archived-paper index entry must stay gone after the sweep");
    }

    public static void FailedRestoreRollsBackToArchived()
    {
        var host = new FakeHost();
        var card = host.AddNotePaper("恢复失败", "# 正文", visible: false);
        var entry = ArchivedEntry("e-failed-restore", card);
        using var fixture = new RuntimeFixture(host, seed: store => store.Upsert(entry, ArchiveChangeKind.EntryAdded));
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(300));
        host.ResetCalls();

        var service = CreateService(host, fixture);
        host.FailShowPaper = true;
        var result = service.RestoreEntry(entry.Id);
        host.FailShowPaper = false;

        Require.False(result.Ok, "a refused ShowPaper must surface as a failed restore");
        Require.Equal(1, host.Count("ShowPaper:" + card.Id), "the restore must have attempted to show the card");
        Require.False(card.Visible, "the card must still be hidden after the failed restore");

        var stored = fixture.Store.Find(entry.Id);
        Require.NotNull(stored, "the record must survive the failed restore");
        Require.Equal(ArchiveEntryState.Archived, stored!.State,
            $"a failed restore must roll the record back to Archived, actual <{stored.State}>");
        Require.Null(stored.RestoredAt, "the rolled-back record must not claim a restore time");
        Require.Null(stored.RestoredPaperId, "the rolled-back record must not claim a restored card");
        Require.Equal(entry.Id, fixture.Store.FindEntryIdByArchivedPaper(card.Id),
            "the archived-paper index entry must be back after the rollback");

        // The lock still governs the card afterwards.
        Reveal(host, card);
        var rehidden = DispatcherPump.PumpUntil(() => host.Count("HidePaper:" + card.Id) > 0, TimeSpan.FromSeconds(3));
        Require.True(rehidden, "a rolled-back card must stay under the lock");
        Require.Equal(ArchiveEntryState.Archived, fixture.Store.Find(entry.Id)!.State,
            "the lock sweep must not change the rolled-back record");
    }

    public static void DisposeCancelsPendingSweep()
    {
        var host = new FakeHost();
        var card = host.AddNotePaper("释放取消", "# 正文", visible: false);
        var entry = ArchivedEntry("e-dispose-cancel", card);
        using var fixture = new RuntimeFixture(host, seed: store => store.Upsert(entry, ArchiveChangeKind.EntryAdded));
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(400));
        host.ResetCalls();

        // Schedule the sweep, then tear the Runtime down before the timer can tick.
        Reveal(host, card);
        fixture.Runtime.Dispose();

        // Pump well past the 200 ms sweep delay; nothing may be hidden and nothing may throw.
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(700));
        Require.Equal(0, host.Count("HidePaper:" + card.Id),
            "Dispose must cancel the pending lock sweep");
        Require.True(card.Visible, "nothing may be hidden after Dispose");
        Require.Equal(0, host.SubscriptionCount, "Dispose must unsubscribe from the workspace");

        var timerField = typeof(ArchiveRuntime).GetField(
            "_lockSweepTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        var pendingTimer = timerField?.GetValue(fixture.Runtime);
        Console.WriteLine(pendingTimer is null
            ? "NOTE  Dispose cleared the pending lock-sweep timer before it could tick"
            : "NOTE  Dispose left a lock-sweep timer instance reachable; the sweep is still gated by _disposed");

        // A second Dispose must be safe, and a later reveal must not resurrect the sweep.
        fixture.Runtime.Dispose();
        Reveal(host, card);
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(500));
        Require.Equal(0, host.Count("HidePaper:" + card.Id),
            "a second Dispose and a late reveal must never hide anything");
    }

    public static void LockEnabledAtRuntimeSweepsImmediately()
    {
        var host = new FakeHost();
        var card = host.AddNotePaper("运行中开启锁", "# 正文", visible: false);
        var entry = ArchivedEntry("e-lock-enable", card);
        using var fixture = new RuntimeFixture(host, LockOffJson,
            seed: store => store.Upsert(entry, ArchiveChangeKind.EntryAdded));

        // The host revealed this card while the backend was not listening (a show-all run before the
        // Runtime started), so it is on screen and still archived, with no reveal event delivered.
        card.Visible = true;
        DispatcherPump.Pump(TimeSpan.FromMilliseconds(300));
        Require.True(card.Visible, "with the lock off nothing may hide an already-visible card");
        Require.Equal(0, host.Count("HidePaper:" + card.Id),
            "with the lock off nothing may be hidden, even at startup");
        Require.Equal(ArchiveEntryState.Archived, fixture.Store.Find(entry.Id)!.State,
            "the precondition needs a still-archived card that is already on screen");
        host.ResetCalls();

        // Turning the lock on must re-hide it with no new reveal event.
        fixture.Settings.Raise(LockOnJson);
        var swept = DispatcherPump.PumpUntil(
            () => host.Count("HidePaper:" + card.Id) > 0,
            TimeSpan.FromSeconds(2));

        Require.True(swept,
            $"enabling lockArchivedHidden must hide already-visible archived cards immediately, " +
            $"saw [{string.Join(", ", host.Calls)}]");
        Require.False(card.Visible, "the visible archived card must be hidden again");
        Require.Equal(ArchiveEntryState.Archived, fixture.Store.Find(entry.Id)!.State,
            "the lock must not change the record when it sweeps");
    }

    // ---------------------------------------------------------------- helpers

    private static ArchiveService CreateService(FakeHost host, RuntimeFixture fixture) => new(
        new ArchiveHostContext
        {
            ProviderId = RuntimeFixture.ProviderId,
            Workspace = host,
            Presentation = host,
            Repository = fixture.Store,
            Settings = () => ArchiveSettings.Parse(fixture.Settings.Json),
            HostPaperId = null
        });

    private static ArchiveEntry ArchivedEntry(string id, FakePaper card) => new()
    {
        Id = id,
        Kind = ArchiveEntryKind.Paper,
        State = ArchiveEntryState.Archived,
        Origin = ArchiveCaptureOrigin.User,
        PaperId = card.Id,
        PaperType = card.Type,
        BodyProviderId = card.BodyProviderId,
        PaperTitle = card.Title,
        Title = card.Title,
        CapturedAt = DateTimeOffset.Now.AddMinutes(-10),
        NoteContent = string.Equals(card.Type, ArchiveRules.NotePaperType, StringComparison.Ordinal)
            ? card.Content
            : null,
        NoteContentAvailable = string.Equals(card.Type, ArchiveRules.NotePaperType, StringComparison.Ordinal)
    };

    /// <summary>
    /// Delivers the event the host would emit when a card becomes visible again. The fake host's own
    /// API calls do not raise events, so the reveal of a PaperTodo-owned show-all path is modelled
    /// explicitly here.
    /// </summary>
    private static void Reveal(FakeHost host, FakePaper paper)
    {
        var before = new PaperSnapshot(
            paper.Id, paper.Type, paper.Title, false, paper.Collapsed, paper.AlwaysOnTop, paper.BodyProviderId);
        paper.Visible = true;
        var after = new PaperSnapshot(
            paper.Id, paper.Type, paper.Title, true, paper.Collapsed, paper.AlwaysOnTop, paper.BodyProviderId);
        host.Raise(new PaperChangedEvent(before, after, PaperChangedFields.Visibility, Metadata()));
    }

    private static PaperTodoEventMetadata Metadata() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        DateTimeOffset.Now,
        PaperTodoEventOrigin.User,
        null);
}
