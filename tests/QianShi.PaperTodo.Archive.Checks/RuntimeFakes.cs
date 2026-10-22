using System.Text.Json;

namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>Reusable one-shot subscription used by the runtime fakes.</summary>
internal sealed class ActionSubscription(Action release) : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        release();
    }
}

/// <summary>
/// Records the provider-runtime contribution surface. `CurrentActions` is the host-visible state
/// (replace semantics), the counters prove that a clear really happened on Dispose.
/// </summary>
internal sealed class FakeGlobalTopBar : IPaperGlobalTopBarApi
{
    private readonly List<IReadOnlyList<PaperTopBarAction>> _history = [];

    public Action<PaperTopBarActionInvocation>? Handler { get; private set; }

    public IReadOnlyList<PaperTopBarAction> CurrentActions { get; private set; } = [];

    public IReadOnlyList<IReadOnlyList<PaperTopBarAction>> History => _history;

    public int SetActionsCount => _history.Count;

    public int SetHandlerCount { get; private set; }

    public int ClearCount { get; private set; }

    public void SetActionHandler(Action<PaperTopBarActionInvocation>? handler)
    {
        SetHandlerCount++;
        Handler = handler;
    }

    public void SetActions(IReadOnlyList<PaperTopBarAction> actions)
    {
        _history.Add(actions);
        CurrentActions = actions;
    }

    public void Clear()
    {
        ClearCount++;
        _history.Add(Array.Empty<PaperTopBarAction>());
        CurrentActions = Array.Empty<PaperTopBarAction>();
    }
}

/// <summary>Runtime settings whose JSON the check controls directly.</summary>
internal sealed class FakeRuntimeSettings(string json) : IPaperPluginRuntimeSettings
{
    private readonly List<Action<string>> _handlers = [];

    public string Json { get; private set; } = json;

    public int SubscriptionCount => _handlers.Count;

    public IDisposable Subscribe(Action<string> handler)
    {
        _handlers.Add(handler);
        return new ActionSubscription(() => _handlers.Remove(handler));
    }

    public void Raise(string nextJson)
    {
        Json = nextJson;
        foreach (var handler in _handlers.ToArray())
        {
            handler(nextJson);
        }
    }
}

/// <summary>The plugin never writes provider-scoped state; this fake proves that stays true.</summary>
internal sealed class FakeRuntimeState : IPaperPluginRuntimeState
{
    public string Json { get; private set; } = "{}";

    public int StateVersion => 1;

    public int TargetStateVersion => 1;

    public List<string> Saves { get; } = [];

    public void Save(string json)
    {
        Json = json;
        Saves.Add(json);
    }
}

/// <summary>No paper in this plugin owns a live Body/Mini frontend, so every call is recorded only.</summary>
internal sealed class FakeRuntimePapers : IPaperPluginRuntimePapers
{
    public List<string> Calls { get; } = [];

    public IReadOnlyList<PaperPluginRuntimePaper> List()
    {
        Calls.Add("List");
        return Array.Empty<PaperPluginRuntimePaper>();
    }

    public PaperPluginRuntimePaper? Get(string paperId)
    {
        Calls.Add("Get:" + paperId);
        return null;
    }

    public void SetTitle(string paperId, string title) => Calls.Add("SetTitle:" + paperId);

    public void SetHeaderText(string paperId, string text) => Calls.Add("SetHeaderText:" + paperId);

    public void SetCapsulePresentation(string paperId, PaperCapsulePresentation? presentation) =>
        Calls.Add("SetCapsulePresentation:" + paperId);

    public bool PostToBody(string paperId, JsonElement message)
    {
        Calls.Add("PostToBody:" + paperId);
        return false;
    }

    public IDisposable Subscribe(Action<PaperPluginRuntimeEvent> handler)
    {
        Calls.Add("Subscribe");
        return new ActionSubscription(() => { });
    }
}

/// <summary>The archive plugin declares no global shortcut.</summary>
internal sealed class FakeGlobalShortcuts : IPaperGlobalShortcutApi
{
    public Action<PaperShortcutActionInvocation>? Handler { get; private set; }

    public int ClearCount { get; private set; }

    public void SetActionHandler(Action<PaperShortcutActionInvocation>? handler) => Handler = handler;

    public void Clear()
    {
        ClearCount++;
        Handler = null;
    }
}
