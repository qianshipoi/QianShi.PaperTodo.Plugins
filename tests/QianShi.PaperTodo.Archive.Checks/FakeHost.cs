namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>One in-memory card.</summary>
internal sealed class FakePaper
{
    public required string Id { get; init; }
    public string Type { get; init; } = ArchiveRules.TodoPaperType;
    public string Title { get; set; } = string.Empty;
    public string BodyProviderId { get; init; } = ArchiveRules.BuiltinMarkdownProviderId;
    public bool Visible { get; set; } = true;
    public bool Collapsed { get; set; }
    public bool AlwaysOnTop { get; set; }
    public string Content { get; set; } = string.Empty;
    public List<FakeTodo> Todos { get; } = [];
}

internal sealed class FakeTodo
{
    public required string Id { get; init; }
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
    public int Order { get; set; }
    public string? LinkedPaperId { get; set; }
    public string? LinkedPath { get; set; }
    public bool? LinkedPathIsDirectory { get; set; }
    public DateTimeOffset? ReminderAt { get; set; }
}

internal sealed record PaperMenuRegistration(string PaperId, IReadOnlyList<PaperAction> Actions);

/// <summary>
/// In-memory stand-in for the two host surfaces the archive plugin is allowed to use. It records
/// every call in order, so a check can prove that archiving never deletes, that restoring shows
/// instead of recreating, and so on.
///
/// Error behavior copies the real host: presentation calls on a missing paper throw
/// <see cref="PaperTodoPluginException"/> with code "paper_not_found", and a blank id throws
/// "invalid_params" (see PaperTodo/src/PaperBodyPluginHostApi.WorkspacePresentation.cs and
/// tests/PaperTodo.SettingsApiChecks/PresentationChecks.cs).
/// </summary>
internal sealed class FakeHost :
    IPaperTodoHostApi,
    IPaperWorkspacePresentationApi,
    IPaperPluginPaperActions,
    IPaperPluginRuntimeTodoActions
{
    private static readonly string[] ReadCallPrefixes =
        ["ListPapers", "GetPaper", "ListTodos", "GetNote", "Subscribe"];

    private readonly List<FakePaper> _papers = [];
    private readonly List<Action<PaperTodoEvent>> _eventHandlers = [];
    private int _paperSequence;
    private int _todoSequence;

    /// <summary>Every host call in the order it happened, for example "HidePaper:p3".</summary>
    public List<string> Calls { get; } = [];

    public List<CreatePaperRequest> CreateRequests { get; } = [];

    public List<AppendTodosRequest> AppendRequests { get; } = [];

    public List<WriteNoteRequest> NoteWrites { get; } = [];

    public List<DeleteTodoRequest> DeletedTodos { get; } = [];

    public List<string> DeletedPaperIds { get; } = [];

    public List<string> ShownPaperIds { get; } = [];

    public List<string> HiddenPaperIds { get; } = [];

    public List<string> ExpandedPaperIds { get; } = [];

    public List<string> ActivatedPaperIds { get; } = [];

    public IReadOnlyList<FakePaper> Papers => _papers;

    /// <summary>Live workspace event subscriptions; a disposed Runtime must leave this at zero.</summary>
    public int SubscriptionCount => _eventHandlers.Count;

    /// <summary>Makes <see cref="ListPapers"/> fail, to model a host that cannot answer yet.</summary>
    public bool FailListPapers { get; set; }

    /// <summary>Makes <see cref="ShowPaper"/> throw, to model a host that refuses to present a card.</summary>
    public bool FailShowPaper { get; set; }

    public IReadOnlySet<string> GrantedPermissions => PaperTodoPermissionNames.All;

    // ---------------------------------------------------------------- seeding

    /// <summary>Seeds a Todo card carrying the full todo content the archive must freeze.</summary>
    public FakePaper AddTodoPaper(string title, params (string Text, bool Done)[] todos)
    {
        var paper = new FakePaper { Id = NextPaperId(), Type = ArchiveRules.TodoPaperType, Title = title };
        foreach (var (text, done) in todos)
        {
            paper.Todos.Add(new FakeTodo
            {
                Id = NextTodoId(),
                Text = text,
                Done = done,
                Order = paper.Todos.Count
            });
        }

        _papers.Add(paper);
        return paper;
    }

    public FakePaper AddNotePaper(
        string title,
        string content,
        string bodyProviderId = ArchiveRules.BuiltinMarkdownProviderId,
        bool visible = true)
    {
        var paper = new FakePaper
        {
            Id = NextPaperId(),
            Type = ArchiveRules.NotePaperType,
            Title = title,
            BodyProviderId = bodyProviderId,
            Content = content,
            Visible = visible
        };
        _papers.Add(paper);
        return paper;
    }

    /// <summary>Removes a card without any host call, simulating a card the plugin never saw deleted.</summary>
    public void RemovePaperSilently(string paperId) =>
        _papers.RemoveAll(paper => string.Equals(paper.Id, paperId, StringComparison.Ordinal));

    public FakePaper? FindPaper(string paperId) =>
        _papers.FirstOrDefault(paper => string.Equals(paper.Id, paperId, StringComparison.Ordinal));

    public FakeTodo? FindTodo(string paperId, string todoId) => FindPaper(paperId)?.Todos
        .FirstOrDefault(todo => string.Equals(todo.Id, todoId, StringComparison.Ordinal));

    // ---------------------------------------------------------------- call log

    public void ResetCalls()
    {
        Calls.Clear();
        CreateRequests.Clear();
        AppendRequests.Clear();
        NoteWrites.Clear();
        DeletedTodos.Clear();
        DeletedPaperIds.Clear();
        ShownPaperIds.Clear();
        HiddenPaperIds.Clear();
        ExpandedPaperIds.Clear();
        ActivatedPaperIds.Clear();
    }

    /// <summary>Number of recorded calls that start with <paramref name="prefix"/>.</summary>
    public int Count(string prefix) =>
        Calls.Count(call => call.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Recorded calls that are not plain reads.</summary>
    public IReadOnlyList<string> Mutations() => Calls
        .Where(call => !ReadCallPrefixes.Any(prefix => call.StartsWith(prefix, StringComparison.Ordinal)))
        .ToArray();

    public void Raise(PaperTodoEvent value)
    {
        foreach (var handler in _eventHandlers.ToArray())
        {
            handler(value);
        }
    }

    private void Record(string call) => Calls.Add(call);

    private string NextPaperId() => "p" + ++_paperSequence;

    private string NextTodoId() => "t" + ++_todoSequence;

    // ---------------------------------------------------------------- IPaperTodoHostApi

    public IReadOnlyList<PaperSnapshot> ListPapers(string? type = null)
    {
        Record("ListPapers" + (type is null ? string.Empty : ":" + type));
        if (FailListPapers)
        {
            // Simulates a host that is still starting up (or has revoked the read permission).
            throw new PaperTodoPluginException("permission_denied", "ListPapers is disabled by the check.");
        }

        return _papers
            .Where(paper => type is null || string.Equals(paper.Type, type, StringComparison.Ordinal))
            .Select(ToSnapshot)
            .ToArray();
    }

    public PaperSnapshot? GetPaper(string paperId)
    {
        Record("GetPaper:" + paperId);
        var paper = FindPaper(paperId);
        return paper is null ? null : ToSnapshot(paper);
    }

    public IReadOnlyList<TodoSnapshot> ListTodos(string? paperId = null, bool includeBlank = false)
    {
        Record("ListTodos:" + (paperId ?? "*"));
        var papers = paperId is null
            ? _papers
            : _papers.Where(paper => string.Equals(paper.Id, paperId, StringComparison.Ordinal));
        return papers
            .SelectMany(paper => paper.Todos
                .Where(todo => includeBlank || !string.IsNullOrWhiteSpace(todo.Text))
                .Select(todo => new TodoSnapshot(
                    paper.Id,
                    paper.Title,
                    todo.Id,
                    todo.Text,
                    todo.Done,
                    todo.Order,
                    todo.LinkedPaperId,
                    todo.LinkedPath,
                    todo.ReminderAt)
                {
                    LinkedPathIsDirectory = todo.LinkedPathIsDirectory
                }))
            .ToArray();
    }

    public NoteSnapshot? GetNote(string paperId)
    {
        Record("GetNote:" + paperId);
        var paper = FindPaper(paperId);
        return paper is null
            ? null
            : new NoteSnapshot(
                paper.Id,
                paper.Title,
                paper.BodyProviderId,
                ArchiveRules.IsBuiltinMarkdown(paper.BodyProviderId),
                paper.Content);
    }

    public PaperMutationResult CreatePaper(CreatePaperRequest request)
    {
        Record($"CreatePaper:{request.Type}|todos={request.Todos?.Count ?? 0}|content={(request.Content?.Length ?? 0)}");
        CreateRequests.Add(request);
        if (request.Type is not (ArchiveRules.TodoPaperType or ArchiveRules.NotePaperType))
        {
            throw new PaperTodoPluginException("invalid_params", "Unknown paper type: " + request.Type);
        }

        var paper = new FakePaper
        {
            Id = NextPaperId(),
            Type = request.Type,
            Title = request.Title ?? string.Empty,
            Visible = request.Show,
            Content = request.Content ?? string.Empty
        };
        if (request.Todos is not null)
        {
            foreach (var item in request.Todos)
            {
                paper.Todos.Add(new FakeTodo
                {
                    Id = NextTodoId(),
                    Text = item.Text,
                    Done = item.Done,
                    Order = paper.Todos.Count,
                    LinkedPaperId = item.LinkedPaperId,
                    ReminderAt = item.ReminderAt
                });
            }
        }

        _papers.Add(paper);
        return new PaperMutationResult(paper.Id, paper.Type, Created: true);
    }

    public AppendTodosResult AppendTodos(AppendTodosRequest request)
    {
        Record($"AppendTodos:{request.PaperId}|n={request.Todos.Count}");
        AppendRequests.Add(request);
        var paper = RequirePaper(request.PaperId);
        var added = new List<string>();
        foreach (var item in request.Todos)
        {
            var todo = new FakeTodo
            {
                Id = NextTodoId(),
                Text = item.Text,
                Done = item.Done,
                Order = paper.Todos.Count,
                LinkedPaperId = item.LinkedPaperId,
                ReminderAt = item.ReminderAt
            };
            paper.Todos.Add(todo);
            added.Add(todo.Id);
        }

        return new AppendTodosResult(paper.Id, added);
    }

    public TodoMutationResult UpdateTodo(UpdateTodoRequest request)
    {
        Record($"UpdateTodo:{request.PaperId}|{request.TodoId}");
        var todo = RequireTodo(request.PaperId, request.TodoId);
        if (request.Text is not null)
        {
            todo.Text = request.Text;
        }

        if (request.Done is { } done)
        {
            todo.Done = done;
        }

        if (request.Order is { } order)
        {
            todo.Order = order;
        }

        if (request.UpdateLinkedPaper)
        {
            todo.LinkedPaperId = request.LinkedPaperId;
        }

        return new TodoMutationResult(request.PaperId, request.TodoId);
    }

    public TodoMutationResult SetTodoReminder(SetTodoReminderRequest request)
    {
        Record($"SetTodoReminder:{request.PaperId}|{request.TodoId}");
        var todo = RequireTodo(request.PaperId, request.TodoId);
        todo.ReminderAt = request.ReminderAt;
        return new TodoMutationResult(request.PaperId, request.TodoId);
    }

    public NoteMutationResult WriteNote(WriteNoteRequest request)
    {
        Record($"WriteNote:{request.PaperId}|{request.Mode}|{request.Content.Length}");
        NoteWrites.Add(request);
        var paper = RequirePaper(request.PaperId);
        if (!ArchiveRules.IsBuiltinMarkdown(paper.BodyProviderId))
        {
            throw new PaperTodoPluginException("note_body_unavailable", "The note body is owned by another provider.");
        }

        paper.Content = request.Mode switch
        {
            NoteWriteMode.Replace => request.Content,
            NoteWriteMode.Append => paper.Content + request.Content,
            _ => string.IsNullOrEmpty(paper.Content) ? request.Content : paper.Content
        };
        return new NoteMutationResult(paper.Id, paper.Content.Length);
    }

    public DeleteMutationResult DeleteTodo(DeleteTodoRequest request)
    {
        Record($"DeleteTodo:{request.PaperId}|{request.TodoId}");
        DeletedTodos.Add(request);
        var paper = RequirePaper(request.PaperId);
        var todo = paper.Todos.FirstOrDefault(
            candidate => string.Equals(candidate.Id, request.TodoId, StringComparison.Ordinal));
        if (todo is null)
        {
            return new DeleteMutationResult(request.TodoId, Deleted: false);
        }

        paper.Todos.Remove(todo);
        return new DeleteMutationResult(request.TodoId, Deleted: true);
    }

    public DeleteMutationResult DeletePaper(string paperId)
    {
        Record("DeletePaper:" + paperId);
        DeletedPaperIds.Add(paperId);
        var paper = FindPaper(paperId);
        if (paper is null)
        {
            return new DeleteMutationResult(paperId, Deleted: false);
        }

        _papers.Remove(paper);
        return new DeleteMutationResult(paperId, Deleted: true);
    }

    public IDisposable Subscribe(PaperTodoEventFilter filter, Action<PaperTodoEvent> handler)
    {
        Record("Subscribe");
        _eventHandlers.Add(handler);
        return new Subscription(() => _eventHandlers.Remove(handler));
    }

    // ---------------------------------------------------------------- IPaperWorkspacePresentationApi

    public PaperPresentationResult ShowPaper(string paperId, bool activate = true)
    {
        Record("ShowPaper:" + paperId);
        if (FailShowPaper)
        {
            // Simulates a host that refuses to present the card (for example a torn-down window).
            throw new PaperTodoPluginException("presentation_unavailable", "ShowPaper is disabled by the check.");
        }

        var paper = RequirePaper(paperId);
        ShownPaperIds.Add(paperId);
        paper.Visible = true;
        if (activate)
        {
            ActivatedPaperIds.Add(paperId);
        }

        return new PaperPresentationResult(paper.Id, paper.Visible, paper.Collapsed);
    }

    public PaperPresentationResult HidePaper(string paperId)
    {
        Record("HidePaper:" + paperId);
        var paper = RequirePaper(paperId);
        HiddenPaperIds.Add(paperId);
        paper.Visible = false;
        return new PaperPresentationResult(paper.Id, paper.Visible, paper.Collapsed);
    }

    public PaperPresentationResult TogglePaperVisibility(string paperId, bool activate = true)
    {
        Record("TogglePaperVisibility:" + paperId);
        var paper = RequirePaper(paperId);
        paper.Visible = !paper.Visible;
        if (paper.Visible)
        {
            ShownPaperIds.Add(paperId);
        }
        else
        {
            HiddenPaperIds.Add(paperId);
        }

        return new PaperPresentationResult(paper.Id, paper.Visible, paper.Collapsed);
    }

    public PaperPresentationResult ExpandPaper(string paperId, bool activate = true)
    {
        Record("ExpandPaper:" + paperId);
        var paper = RequirePaper(paperId);
        ExpandedPaperIds.Add(paperId);
        paper.Visible = true;
        paper.Collapsed = false;
        return new PaperPresentationResult(paper.Id, paper.Visible, paper.Collapsed);
    }

    public PaperPresentationResult CollapsePaper(string paperId)
    {
        Record("CollapsePaper:" + paperId);
        var paper = RequirePaper(paperId);
        paper.Collapsed = true;
        return new PaperPresentationResult(paper.Id, paper.Visible, paper.Collapsed);
    }

    public PaperPresentationResult TogglePaperCollapsed(string paperId, bool activate = true)
    {
        Record("TogglePaperCollapsed:" + paperId);
        var paper = RequirePaper(paperId);
        paper.Collapsed = !paper.Collapsed;
        return new PaperPresentationResult(paper.Id, paper.Visible, paper.Collapsed);
    }

    public PaperPresentationResult ActivatePaper(string paperId)
    {
        Record("ActivatePaper:" + paperId);
        var paper = RequirePaper(paperId);
        ActivatedPaperIds.Add(paperId);
        paper.Visible = true;
        return new PaperPresentationResult(paper.Id, paper.Visible, paper.Collapsed);
    }

    // ---------------------------------------------------------------- IPaperPluginPaperActions

    /// <summary>The Runtime's per-paper context-menu handler, as last registered.</summary>
    public Action<PaperActionInvocation>? PaperActionHandler { get; private set; }

    /// <summary>Current host-side paper menus (replace semantics, one entry per paper id).</summary>
    public List<PaperMenuRegistration> PaperMenuRegistrations { get; } = [];

    /// <summary>Number of global "clear every paper menu" calls, so a dispose check can prove it.</summary>
    public int PaperMenuClearCount { get; private set; }

    public List<string> PaperMenuClearedPaperIds { get; } = [];

    void IPaperPluginPaperActions.SetActionHandler(Action<PaperActionInvocation>? handler)
    {
        Record("PaperActions.SetActionHandler:" + (handler is null ? "null" : "set"));
        PaperActionHandler = handler;
    }

    void IPaperPluginPaperActions.SetActions(string paperId, IReadOnlyList<PaperAction> actions)
    {
        Record("PaperActions.SetActions:" + paperId + "|n=" + actions.Count);
        PaperMenuRegistrations.RemoveAll(
            registration => string.Equals(registration.PaperId, paperId, StringComparison.Ordinal));
        PaperMenuRegistrations.Add(new PaperMenuRegistration(paperId, actions.ToArray()));
    }

    void IPaperPluginPaperActions.Clear(string paperId)
    {
        Record("PaperActions.Clear:" + paperId);
        PaperMenuClearedPaperIds.Add(paperId);
        PaperMenuRegistrations.RemoveAll(
            registration => string.Equals(registration.PaperId, paperId, StringComparison.Ordinal));
    }

    void IPaperPluginPaperActions.Clear()
    {
        Record("PaperActions.Clear");
        PaperMenuClearCount++;
        PaperMenuRegistrations.Clear();
    }

    public IReadOnlyList<PaperAction>? PaperMenuFor(string paperId) => PaperMenuRegistrations
        .FirstOrDefault(registration => string.Equals(registration.PaperId, paperId, StringComparison.Ordinal))
        ?.Actions;

    // ---------------------------------------------------------------- IPaperPluginRuntimeTodoActions

    /// <summary>The Runtime's Todo row action handler, as last registered.</summary>
    public Action<PaperTodoActionInvocation>? TodoActionHandler { get; private set; }

    /// <summary>Current host-side Todo row actions, keyed by paper and row.</summary>
    public Dictionary<(string PaperId, string TodoId), IReadOnlyList<PaperTodoAction>> TodoActionRegistrations
    { get; } = [];

    public int TodoMenuClearCount { get; private set; }

    public List<(string PaperId, string TodoId)> TodoClearedRows { get; } = [];

    void IPaperPluginRuntimeTodoActions.SetActionHandler(Action<PaperTodoActionInvocation>? handler)
    {
        Record("TodoActions.SetActionHandler:" + (handler is null ? "null" : "set"));
        TodoActionHandler = handler;
    }

    void IPaperPluginRuntimeTodoActions.SetActions(
        string paperId,
        string todoId,
        IReadOnlyList<PaperTodoAction> actions)
    {
        Record("TodoActions.SetActions:" + paperId + "/" + todoId + "|n=" + actions.Count);
        TodoActionRegistrations[(paperId, todoId)] = actions.ToArray();
    }

    void IPaperPluginRuntimeTodoActions.Clear(string paperId, string todoId)
    {
        Record("TodoActions.Clear:" + paperId + "/" + todoId);
        TodoClearedRows.Add((paperId, todoId));
        TodoActionRegistrations.Remove((paperId, todoId));
    }

    void IPaperPluginRuntimeTodoActions.Clear()
    {
        Record("TodoActions.Clear");
        TodoMenuClearCount++;
        TodoActionRegistrations.Clear();
    }

    public IReadOnlyList<PaperTodoAction>? TodoActionsFor(string paperId, string todoId) =>
        TodoActionRegistrations.TryGetValue((paperId, todoId), out var actions) ? actions : null;

    // ---------------------------------------------------------------- helpers

    private static PaperSnapshot ToSnapshot(FakePaper paper) => new(
        paper.Id,
        paper.Type,
        paper.Title,
        paper.Visible,
        paper.Collapsed,
        paper.AlwaysOnTop,
        paper.BodyProviderId);

    private FakePaper RequirePaper(string paperId)
    {
        if (string.IsNullOrWhiteSpace(paperId))
        {
            throw new PaperTodoPluginException("invalid_params", "paperId is required.");
        }

        return FindPaper(paperId) ??
               throw new PaperTodoPluginException("paper_not_found", "No paper with id " + paperId);
    }

    private FakeTodo RequireTodo(string paperId, string todoId)
    {
        var paper = RequirePaper(paperId);
        return paper.Todos.FirstOrDefault(
                   todo => string.Equals(todo.Id, todoId, StringComparison.Ordinal)) ??
               throw new PaperTodoPluginException("todo_not_found", "No todo with id " + todoId);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dispose();
        }
    }
}
