using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// Body of the Archive Center card. It is a management surface, not a second application: it scans
/// the plugin-owned archive, restores one record or everything, deletes forever only after an
/// inline confirmation, and says plainly that archiving never destroys data.
///
/// All data access goes through <see cref="IArchiveRepository"/> (plugin-owned storage) and
/// <see cref="IArchiveOperations"/> (the same business behavior the provider Runtime uses), so the
/// card can never drift from the background service.
/// </summary>
internal sealed class ArchiveSession : IPaperBodySession
{
    private const int RowCount = 8;
    private const int ListRow = 5;
    private const int MaxRenderedRows = 300;

    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan StateSaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly PaperBodyContext _context;
    private readonly IArchiveRepository _repository;
    private readonly IArchiveOperations? _operations;

    private readonly Grid _root;
    private readonly TextBlock _titleText;
    private readonly TextBlock _subtitleText;
    private readonly StatCard _archivedCard;
    private readonly StatCard _trashCard;
    private readonly StatCard _restoredCard;
    private readonly StatCard _mirrorCard;
    private readonly TextBlock _hintText;
    private readonly List<FilterChip> _chips = [];
    private readonly TextBlock _countText;
    private readonly TextBox _searchBox;
    private readonly TextBlock _searchPlaceholder;
    private readonly Button _clearSearchButton;
    private readonly Button _restoreAllButton;
    private readonly Button _refreshButton;
    private readonly ListBox _list;
    private readonly TextBlock _emptyText;
    private readonly TextBlock _statusText;
    private readonly TextBlock _storageLabel;
    private readonly TextBlock _storageValue;
    private readonly TextBlock _storageHint;
    private readonly TextBlock _storageWarning;
    private readonly Border _overlay;
    private readonly Border _confirmCard;
    private readonly TextBlock _confirmTitle;
    private readonly TextBlock _confirmBody;
    private readonly Button _confirmOkButton;
    private readonly Button _confirmCancelButton;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _stateTimer;
    private readonly Border _deleteWarningPanel;
    private readonly TextBlock _deleteWarningTitle;
    private readonly List<TextBlock> _deleteWarningLines = [];

    private ArchiveSettings _settings;
    private ArchiveCenterTheme _theme;
    private ArchiveListFilter _filter = ArchiveListFilter.All;
    private string _search = string.Empty;
    private string? _capsuleKey;
    private long? _rowSignature;
    private bool _forceRowRebuild = true;
    private bool _pendingFullRefresh = true;
    private bool? _statusOk;
    private Action? _pendingConfirm;
    private bool _compact;
    private volatile bool _disposed;

    public ArchiveSession(PaperBodyContext context)
    {
        _context = context;
        ArchiveText.Configure(context.UiLanguage);
        _settings = ArchiveSettings.Parse(context.SettingsJson);
        _theme = ArchiveCenterTheme.From(context.Theme);

        var (filter, search) = ReadViewState(context.StateJson);
        _filter = filter;
        _search = search;

        _repository = ArchiveStore.Shared;
        _repository.Load();
        _operations = CreateOperations();

        // Header -------------------------------------------------------------------------------
        _titleText = new TextBlock
        {
            Text = ArchiveText.Get("AppTitle"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold
        };
        _subtitleText = new TextBlock
        {
            Text = ArchiveText.Get("AppSubtitle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var header = new StackPanel();
        header.Children.Add(_titleText);
        header.Children.Add(_subtitleText);

        // Permanent delete warning ---------------------------------------------------------------
        // This card is the plugin's only paper: PaperTodo keeps the provider Runtime (top-bar
        // action, card context menu, Todo-row menu) alive only while a Note paper uses this
        // provider, so deleting the card silently removes every entry point. The archive file is
        // untouched, so the warning also explains how to get the card back.
        _deleteWarningTitle = new TextBlock
        {
            Text = ArchiveText.Get("DeleteWarningTitle"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold
        };
        _deleteWarningPanel = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Child = BuildDeleteWarningBody()
        };
        header.Children.Add(_deleteWarningPanel);

        // Statistics ---------------------------------------------------------------------------
        _archivedCard = new StatCard(ArchiveText.Get("StatArchived"));
        _trashCard = new StatCard(ArchiveText.Get("StatTrash"));
        _restoredCard = new StatCard(ArchiveText.Get("StatRestored"));
        _mirrorCard = new StatCard(ArchiveText.Get("StatMirror"));
        var statsPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        statsPanel.Children.Add(_archivedCard.Root);
        statsPanel.Children.Add(_trashCard.Root);
        statsPanel.Children.Add(_restoredCard.Root);
        statsPanel.Children.Add(_mirrorCard.Root);

        _hintText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        // Filters ------------------------------------------------------------------------------
        var filterRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var candidate in new[]
                 {
                     ArchiveListFilter.All,
                     ArchiveListFilter.Archived,
                     ArchiveListFilter.Trashed,
                     ArchiveListFilter.Restored
                 })
        {
            var chip = ArchiveUiControls.CreateChip(ArchiveText.ForFilter(candidate));
            var selected = candidate;
            chip.Click += (_, _) => SelectFilter(selected);
            _chips.Add(new FilterChip(candidate, chip));
            filterRow.Children.Add(chip);
        }

        _countText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 6)
        };
        filterRow.Children.Add(_countText);

        // Search and actions --------------------------------------------------------------------
        _searchBox = new TextBox
        {
            Height = 30,
            Padding = new Thickness(8, 4, 8, 4),
            BorderThickness = new Thickness(1),
            Text = _search,
            ToolTip = ArchiveText.Get("SearchPlaceholder")
        };
        _searchPlaceholder = new TextBlock
        {
            Text = ArchiveText.Get("SearchPlaceholder"),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 9, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var searchHost = new Grid
        {
            MinWidth = 130,
            MaxWidth = 300,
            Margin = new Thickness(0, 0, 6, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        searchHost.Children.Add(_searchBox);
        searchHost.Children.Add(_searchPlaceholder);

        _clearSearchButton = ArchiveUiControls.CreateActionButton(
            ArchiveText.Get("ButtonClearSearch"),
            _theme);
        _clearSearchButton.Visibility = _search.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _clearSearchButton.Click += (_, _) => _searchBox.Clear();

        _restoreAllButton = ArchiveUiControls.CreateActionButton(
            ArchiveText.Get("ButtonRestoreAll"),
            _theme);
        _restoreAllButton.Click += (_, _) => RestoreAll();

        _refreshButton = ArchiveUiControls.CreateActionButton(ArchiveText.Get("ButtonRefresh"), _theme);
        _refreshButton.Click += (_, _) => Refresh();

        var actionRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        actionRow.Children.Add(searchHost);
        actionRow.Children.Add(_clearSearchButton);
        actionRow.Children.Add(_restoreAllButton);
        actionRow.Children.Add(_refreshButton);

        UpdateSearchPlaceholder();
        _searchBox.TextChanged += OnSearchChanged;

        // Record list ---------------------------------------------------------------------------
        _list = ArchiveUiControls.CreateVirtualizedList();
        _emptyText = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
            Visibility = Visibility.Collapsed
        };
        var listHost = new Grid();
        listHost.Children.Add(_list);
        listHost.Children.Add(_emptyText);

        // Status line and footer -----------------------------------------------------------------
        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };

        _storageLabel = new TextBlock
        {
            Text = ArchiveText.Get("StoragePath"),
            TextWrapping = TextWrapping.Wrap
        };
        _storageValue = new TextBlock
        {
            Text = _repository.StoragePath,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        _storageHint = new TextBlock
        {
            Text = ArchiveText.Get("StorageHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        _storageWarning = new TextBlock
        {
            Text = ArchiveText.Get("StorageWarning"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed
        };
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_storageLabel);
        footer.Children.Add(_storageValue);
        footer.Children.Add(_storageHint);
        footer.Children.Add(_storageWarning);
        footer.Visibility = string.IsNullOrWhiteSpace(_storageValue.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Inline confirmation overlay ------------------------------------------------------------
        _confirmTitle = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold
        };
        _confirmBody = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _confirmOkButton = ArchiveUiControls.CreateActionButton(ArchiveText.Get("Confirm"), _theme);
        _confirmOkButton.Click += (_, _) => AcceptConfirm();
        _confirmCancelButton = ArchiveUiControls.CreateActionButton(ArchiveText.Get("Cancel"), _theme);
        _confirmCancelButton.Click += (_, _) => HideConfirm();

        var confirmButtons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        confirmButtons.Children.Add(_confirmCancelButton);
        confirmButtons.Children.Add(_confirmOkButton);

        var confirmPanel = new StackPanel();
        confirmPanel.Children.Add(_confirmTitle);
        confirmPanel.Children.Add(_confirmBody);
        confirmPanel.Children.Add(confirmButtons);

        _confirmCard = new Border
        {
            Child = confirmPanel,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420,
            Margin = new Thickness(12)
        };
        _overlay = new Border
        {
            Child = new Grid { Children = { _confirmCard } },
            Visibility = Visibility.Collapsed
        };
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _overlay))
            {
                HideConfirm();
            }
        };

        // Root -----------------------------------------------------------------------------------
        _root = new Grid
        {
            Margin = new Thickness(16, 14, 16, 16),
            Background = Brushes.Transparent
        };
        for (var index = 0; index < RowCount; index++)
        {
            _root.RowDefinitions.Add(new RowDefinition
            {
                Height = index == ListRow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });
        }

        AddToRow(header, 0);
        AddToRow(statsPanel, 1);
        AddToRow(_hintText, 2);
        AddToRow(filterRow, 3);
        AddToRow(actionRow, 4);
        AddToRow(listHost, ListRow);
        AddToRow(_statusText, 6);
        AddToRow(footer, 7);
        Grid.SetRowSpan(_overlay, RowCount);
        Panel.SetZIndex(_overlay, 100);
        _root.Children.Add(_overlay);

        _root.SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshDebounce
        };
        _refreshTimer.Tick += (_, _) => OnRefreshTimerTick();

        _stateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = StateSaveDebounce
        };
        _stateTimer.Tick += (_, _) =>
        {
            _stateTimer.Stop();
            SaveViewState();
        };

        _repository.Changed += OnRepositoryChanged;

        _context.Paper.SetTitle(ArchiveText.Get("AppTitle"));
        _context.Paper.SetHeaderText(ArchiveText.Get("AppTitle"));
        UpdateCapsule();

        ApplyTheme(context.Theme);
        Refresh();
    }

    public FrameworkElement View => _root;

    // ---------------------------------------------------------------------------------------------
    // Session lifecycle
    // ---------------------------------------------------------------------------------------------

    public void Commit() => SaveViewState();

    public void RefreshFromModel() => Refresh();

    public void CancelInteractions() => HideConfirm();

    public void OnActivated() => Refresh();

    public void OnVisibilityChanged(bool visible)
    {
        if (visible)
        {
            Refresh();
        }
    }

    public void OnPresentationChanged(bool visible)
    {
        if (visible)
        {
            Refresh();
        }
    }

    public void OnThemeChanged(PaperBodyTheme theme)
    {
        ApplyTheme(theme);
        _forceRowRebuild = true;
        Refresh();
    }

    public void OnTypographyChanged(PaperBodyTheme theme) => OnThemeChanged(theme);

    public void OnSettingsChanged(string settingsJson)
    {
        _settings = ArchiveSettings.Parse(settingsJson);
        UpdateCapsule();
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _stateTimer.Stop();
        _repository.Changed -= OnRepositoryChanged;
        _pendingConfirm = null;
        SaveViewState();
    }

    // ---------------------------------------------------------------------------------------------
    // Data refresh
    // ---------------------------------------------------------------------------------------------

    private void OnRepositoryChanged(ArchiveChangeKind kind)
    {
        if (_disposed)
        {
            return;
        }

        if (kind != ArchiveChangeKind.MirrorsChanged)
        {
            _pendingFullRefresh = true;
        }

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        var dispatcher = _root.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            RestartRefreshTimer();
        }
        else
        {
            // The repository may raise Changed from a worker thread, so every touch of the visual
            // tree has to be marshalled back to the dispatcher.
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RestartRefreshTimer));
        }
    }

    private void RestartRefreshTimer()
    {
        if (_disposed)
        {
            return;
        }

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnRefreshTimerTick()
    {
        _refreshTimer.Stop();
        if (_disposed)
        {
            return;
        }

        if (_pendingFullRefresh)
        {
            _pendingFullRefresh = false;
            Refresh();
        }
        else
        {
            // Only the mirror cache changed: the record rows are still valid, so keep the current
            // list (and the user's scroll position) and just update the counters.
            RefreshStatistics(_repository.Statistics());
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var statistics = _repository.Statistics();
        RefreshStatistics(statistics);

        var entries = _repository.Snapshot();
        var matches = FilterAndSearch(entries);
        var shown = matches.Length > MaxRenderedRows ? matches[..MaxRenderedRows] : matches;

        var signature = ComputeRowSignature(shown);
        if (_rowSignature != signature || _forceRowRebuild)
        {
            var rows = new List<Border>(shown.Length);
            foreach (var entry in shown)
            {
                rows.Add(ArchiveCenterRowBuilder.Build(
                    entry,
                    _theme,
                    _operations != null,
                    RequestRestore,
                    RequestPurge));
            }

            _list.ItemsSource = rows;
            _rowSignature = signature;
            _forceRowRebuild = false;
        }

        var hasRows = shown.Length > 0;
        _list.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        _emptyText.Text = EmptyStateText();
        _emptyText.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;

        var count = ArchiveText.Format("ResultCount", matches.Length);
        if (matches.Length > shown.Length)
        {
            count += " " + ArchiveText.Format("ListTruncated", shown.Length);
        }

        _countText.Text = count;
        _restoreAllButton.IsEnabled = _operations != null && entries.Any(ArchiveRules.IsRestorable);
    }

    private void RefreshStatistics(ArchiveStatistics statistics)
    {
        _archivedCard.ValueText.Text = statistics.Archived.ToString();
        _trashCard.ValueText.Text = statistics.Trashed.ToString();
        _restoredCard.ValueText.Text = statistics.Restored.ToString();
        _mirrorCard.ValueText.Text = statistics.MirroredPapers.ToString();

        var hint = FilterHint(statistics);
        _hintText.Text = hint;
        _hintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;

        _storageWarning.Visibility = string.IsNullOrWhiteSpace(_repository.LastError)
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateCapsule(statistics);
    }

    private ArchiveEntry[] FilterAndSearch(IReadOnlyList<ArchiveEntry> entries)
    {
        IEnumerable<ArchiveEntry> result = _filter switch
        {
            ArchiveListFilter.Archived => entries.Where(entry =>
                entry.State == ArchiveEntryState.Archived),
            ArchiveListFilter.Trashed => entries.Where(entry =>
                entry.State == ArchiveEntryState.Trashed),
            ArchiveListFilter.Restored => entries.Where(entry =>
                entry.State == ArchiveEntryState.Restored),
            _ => entries
        };

        if (_search.Length > 0)
        {
            result = result.Where(entry => Matches(entry, _search));
        }

        // The repository already returns newest first.
        return result.ToArray();
    }

    private static bool Matches(ArchiveEntry entry, string term) =>
        Contains(entry.Title, term) ||
        Contains(entry.PaperTitle, term) ||
        Contains(entry.NoteContent, term) ||
        entry.Todos.Any(todo => Contains(todo.Text, term));

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value) &&
        value.Contains(term, StringComparison.CurrentCultureIgnoreCase);

    private static long ComputeRowSignature(ArchiveEntry[] entries)
    {
        unchecked
        {
            long signature = 17;
            foreach (var entry in entries)
            {
                signature = signature * 31 + entry.Id.GetHashCode(StringComparison.Ordinal);
                signature = signature * 31 + (int)entry.State;
                signature = signature * 31 + (entry.RestoredAt?.UtcTicks ?? 0L);
                signature = signature * 31 + entry.CapturedAt.UtcTicks;
            }

            return signature;
        }
    }

    private string EmptyStateText()
    {
        if (_search.Length > 0)
        {
            return ArchiveText.Get("EmptySearch");
        }

        return _filter switch
        {
            ArchiveListFilter.Archived => ArchiveText.Get("EmptyArchived"),
            ArchiveListFilter.Trashed => ArchiveText.Get("EmptyTrash"),
            ArchiveListFilter.Restored => ArchiveText.Get("EmptyRestored"),
            _ => ArchiveText.Get("EmptyAll")
        };
    }

    private string FilterHint(ArchiveStatistics statistics) => _filter switch
    {
        ArchiveListFilter.Trashed => ArchiveText.Get("HintTrash"),
        ArchiveListFilter.Archived => ArchiveText.Get("HintArchive"),
        ArchiveListFilter.Restored => string.Empty,
        _ => statistics.Trashed > 0
            ? ArchiveText.Get("HintTrash")
            : ArchiveText.Get("HintArchive")
    };

    // ---------------------------------------------------------------------------------------------
    // User actions
    // ---------------------------------------------------------------------------------------------

    private void SelectFilter(ArchiveListFilter filter)
    {
        if (_filter == filter)
        {
            return;
        }

        _filter = filter;
        UpdateChips();
        ScheduleStateSave();
        Refresh();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();

        var next = _searchBox.Text.Trim();
        if (string.Equals(next, _search, StringComparison.Ordinal))
        {
            return;
        }

        _search = next;
        _clearSearchButton.Visibility = _search.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleStateSave();
        ScheduleRefresh();
    }

    private void RequestRestore(ArchiveEntry entry)
    {
        ShowStatus(Run(operations => operations.RestoreEntry(entry.Id)));
        Refresh();
    }

    /// <summary>
    /// Deleting forever is irreversible, so the inline confirmation is always shown for it. There is
    /// no purge setting, and the archive action itself lives on the top bar and context menu, which
    /// are owned by the provider Runtime.
    /// </summary>
    private void RequestPurge(ArchiveEntry entry)
    {
        var body = ArchiveText.Format("ConfirmPurgeBody", ArchiveCenterRowBuilder.DisplayTitle(entry));
        ShowConfirm(
            ArchiveText.Get("ConfirmPurgeTitle"),
            body + Environment.NewLine + ArchiveText.Get("ConfirmDangerHint"),
            ArchiveText.Get("ButtonPurge"),
            () =>
            {
                ShowStatus(Run(operations => operations.PurgeEntry(entry.Id)));
                Refresh();
            });
    }

    private void RestoreAll()
    {
        ShowStatus(Run(operations => operations.RestoreAll()));
        Refresh();
    }

    private ArchiveOperationResult Run(Func<IArchiveOperations, ArchiveOperationResult> action)
    {
        if (_operations == null)
        {
            return ArchiveOperationResult.Failure(ArchiveResultCodes.PresentationUnavailable);
        }

        try
        {
            return action(_operations);
        }
        catch (Exception)
        {
            // The card must never crash the paper because a host call failed.
            return ArchiveOperationResult.Failure(ArchiveResultCodes.Unknown);
        }
    }

    private void ShowStatus(ArchiveOperationResult result)
    {
        _statusOk = result.Ok;
        _statusText.Text = ArchiveText.ForResult(result.Code, result.Detail);
        ApplyStatusColor();
        _statusText.Visibility = Visibility.Visible;
    }

    private void ApplyStatusColor()
    {
        if (_statusOk is bool ok)
        {
            _statusText.Foreground = ok ? _theme.Accent : _theme.Danger;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Inline confirmation (never a second window or a MessageBox)
    // ---------------------------------------------------------------------------------------------

    private void ShowConfirm(string title, string body, string confirmLabel, Action onConfirm)
    {
        _pendingConfirm = onConfirm;
        _confirmTitle.Text = title;
        _confirmBody.Text = body;
        _confirmOkButton.Content = confirmLabel;
        ArchiveUiControls.StyleActionButton(_confirmOkButton, _theme, danger: true);
        _overlay.Visibility = Visibility.Visible;

        // The panel is not measured yet, so move focus on the next input pass. Cancel is the
        // default so a stray Enter never destroys a record.
        _confirmCard.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!_disposed && _overlay.Visibility == Visibility.Visible)
                {
                    _confirmCancelButton.Focus();
                }
            }));
    }

    private void AcceptConfirm()
    {
        var action = _pendingConfirm;
        HideConfirm();
        action?.Invoke();
    }

    private void HideConfirm()
    {
        _pendingConfirm = null;
        _overlay.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------------------------------------
    // Theme
    // ---------------------------------------------------------------------------------------------

    private void ApplyTheme(PaperBodyTheme theme)
    {
        _theme = ArchiveCenterTheme.From(theme);
        var tokens = _theme;

        _titleText.Foreground = tokens.Text;
        _titleText.FontFamily = tokens.Font;
        _titleText.FontSize = tokens.FontSize(_compact ? 16 : 17);

        _subtitleText.Foreground = tokens.Weak;
        _subtitleText.FontFamily = tokens.Font;
        _subtitleText.FontSize = tokens.FontSize(11);

        foreach (var card in new[] { _archivedCard, _trashCard, _restoredCard, _mirrorCard })
        {
            card.Root.BorderBrush = tokens.Border;
            card.Root.Background = tokens.Surface;
            card.ValueText.Foreground = tokens.Text;
            card.ValueText.FontFamily = tokens.Font;
            card.ValueText.FontSize = tokens.FontSize(15);
            card.LabelText.Foreground = tokens.Weak;
            card.LabelText.FontFamily = tokens.Font;
            card.LabelText.FontSize = tokens.FontSize(9.5);
        }

        _hintText.Foreground = tokens.Weak;
        _hintText.FontFamily = tokens.Font;
        _hintText.FontSize = tokens.FontSize(10.5);

        _countText.Foreground = tokens.Weak;
        _countText.FontFamily = tokens.Font;
        _countText.FontSize = tokens.FontSize(10.5);

        _searchBox.Foreground = tokens.Text;
        _searchBox.Background = tokens.Surface;
        _searchBox.BorderBrush = tokens.Border;
        _searchBox.CaretBrush = tokens.Text;
        _searchBox.FontFamily = tokens.Font;
        _searchBox.FontSize = tokens.FontSize(_compact ? 11 : 11.5);

        _searchPlaceholder.Foreground = tokens.Weak;
        _searchPlaceholder.FontFamily = tokens.Font;
        _searchPlaceholder.FontSize = tokens.FontSize(_compact ? 11 : 11.5);

        ArchiveUiControls.StyleActionButton(_clearSearchButton, tokens, danger: false);
        ArchiveUiControls.StyleActionButton(_restoreAllButton, tokens, danger: false);
        ArchiveUiControls.StyleActionButton(_refreshButton, tokens, danger: false);

        UpdateChips();

        _emptyText.Foreground = tokens.Weak;
        _emptyText.FontFamily = tokens.Font;
        _emptyText.FontSize = tokens.FontSize(11.5);

        _statusText.FontFamily = tokens.Font;
        _statusText.FontSize = tokens.FontSize(11.5);
        ApplyStatusColor();

        _storageLabel.Foreground = tokens.Weak;
        _storageLabel.FontFamily = tokens.Font;
        _storageLabel.FontSize = tokens.FontSize(10);

        _storageValue.Foreground = tokens.Text;
        _storageValue.FontFamily = tokens.Font;
        _storageValue.FontSize = tokens.FontSize(10.5);

        _storageHint.Foreground = tokens.Weak;
        _storageHint.FontFamily = tokens.Font;
        _storageHint.FontSize = tokens.FontSize(10.5);

        _storageWarning.Foreground = tokens.Danger;
        _storageWarning.FontFamily = tokens.Font;
        _storageWarning.FontSize = tokens.FontSize(10.5);

        // Same danger pair as the purge overlay, so "destructive" always looks the same.
        _deleteWarningPanel.BorderBrush = tokens.Danger;
        _deleteWarningPanel.Background = tokens.DangerSurface;
        _deleteWarningTitle.Foreground = tokens.Danger;
        _deleteWarningTitle.FontFamily = tokens.Font;
        _deleteWarningTitle.FontSize = tokens.FontSize(11.5);
        foreach (var line in _deleteWarningLines)
        {
            line.Foreground = tokens.Text;
            line.FontFamily = tokens.Font;
            line.FontSize = tokens.FontSize(10.5);
        }

        _overlay.Background = tokens.Overlay;
        _confirmCard.Background = tokens.Paper;
        _confirmCard.BorderBrush = tokens.Border;
        _confirmTitle.Foreground = tokens.Text;
        _confirmTitle.FontFamily = tokens.Font;
        _confirmTitle.FontSize = tokens.FontSize(14);
        _confirmBody.Foreground = tokens.Weak;
        _confirmBody.FontFamily = tokens.Font;
        _confirmBody.FontSize = tokens.FontSize(11.5);
        ArchiveUiControls.StyleActionButton(_confirmCancelButton, tokens, danger: false);
        ArchiveUiControls.StyleActionButton(_confirmOkButton, tokens, danger: true);
    }

    private void UpdateChips()
    {
        foreach (var chip in _chips)
        {
            ArchiveUiControls.StyleChip(chip.Button, _theme, chip.Filter == _filter);
        }
    }

    private void UpdateSearchPlaceholder()
    {
        _searchPlaceholder.Visibility = string.IsNullOrWhiteSpace(_searchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyResponsiveLayout(double width)
    {
        var compact = width < 380;
        if (_compact == compact)
        {
            return;
        }

        _compact = compact;
        _root.Margin = compact ? new Thickness(8, 8, 8, 10) : new Thickness(16, 14, 16, 16);
        _searchBox.FontSize = _theme.FontSize(compact ? 11 : 11.5);
        _titleText.FontSize = _theme.FontSize(compact ? 16 : 17);

        foreach (var card in new[] { _archivedCard, _trashCard, _restoredCard, _mirrorCard })
        {
            card.Root.MinWidth = compact ? 62 : 72;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Capsule and header presentation
    // ---------------------------------------------------------------------------------------------

    private void UpdateCapsule() => UpdateCapsule(_repository.Statistics());

    private void UpdateCapsule(ArchiveStatistics statistics)
    {
        if (_disposed)
        {
            return;
        }

        var key = (_settings.ShowCountInCapsule ? "1" : "0") +
                  "|" + statistics.Archived +
                  "|" + statistics.Trashed;
        if (string.Equals(key, _capsuleKey, StringComparison.Ordinal))
        {
            return;
        }

        _capsuleKey = key;

        PaperCapsulePresentation presentation;
        if (_settings.ShowCountInCapsule)
        {
            var counts = ArchiveText.Format("CapsuleCounts", statistics.Archived, statistics.Trashed);
            var components = new List<PaperCapsuleComponent>
            {
                new()
                {
                    Kind = PaperCapsuleComponentKind.Text,
                    Text = ArchiveText.Format("CapsuleText", statistics.Archived),
                    Fill = true
                }
            };

            if (statistics.Trashed > 0)
            {
                components.Add(new PaperCapsuleComponent
                {
                    Kind = PaperCapsuleComponentKind.Text,
                    Text = ArchiveText.Format("CapsuleTrash", statistics.Trashed),
                    Tone = PaperCapsuleTone.Muted
                });
            }

            presentation = new PaperCapsulePresentation
            {
                Components = components.ToArray(),
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = counts,
                ToolTip = counts
            };
        }
        else
        {
            var title = ArchiveText.Get("AppTitle");
            presentation = new PaperCapsulePresentation
            {
                Components =
                [
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = title,
                        Fill = true
                    }
                ],
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = title,
                ToolTip = ArchiveText.Get("AppSubtitle")
            };
        }

        _context.Paper.SetCapsulePresentation(presentation);
    }

    // ---------------------------------------------------------------------------------------------
    // Host-managed view state
    // ---------------------------------------------------------------------------------------------

    private void ScheduleStateSave()
    {
        if (_disposed)
        {
            return;
        }

        _stateTimer.Stop();
        _stateTimer.Start();
    }

    private void SaveViewState()
    {
        try
        {
            var state = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["filter"] = FilterId(_filter),
                ["search"] = _search
            };
            _context.SaveStateJson(JsonSerializer.Serialize(state));
        }
        catch (Exception)
        {
            // Losing the remembered filter is not worth interrupting the card.
        }
    }

    private static (ArchiveListFilter Filter, string Search) ReadViewState(string? json)
    {
        var filter = ArchiveListFilter.All;
        var search = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return (filter, search);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (filter, search);
            }

            var root = document.RootElement;
            if (root.TryGetProperty("filter", out var filterValue) &&
                filterValue.ValueKind == JsonValueKind.String)
            {
                filter = ParseFilter(filterValue.GetString());
            }

            if (root.TryGetProperty("search", out var searchValue) &&
                searchValue.ValueKind == JsonValueKind.String)
            {
                search = (searchValue.GetString() ?? string.Empty).Trim();
            }
        }
        catch (JsonException)
        {
            // A malformed legacy state just means "start from the default view".
        }

        return (filter, search);
    }

    private static ArchiveListFilter ParseFilter(string? value) => value switch
    {
        "archived" => ArchiveListFilter.Archived,
        "trashed" => ArchiveListFilter.Trashed,
        "restored" => ArchiveListFilter.Restored,
        _ => ArchiveListFilter.All
    };

    private static string FilterId(ArchiveListFilter filter) => filter switch
    {
        ArchiveListFilter.Archived => "archived",
        ArchiveListFilter.Trashed => "trashed",
        ArchiveListFilter.Restored => "restored",
        _ => "all"
    };

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Warning body: short lines instead of one paragraph, so a narrow card wraps them cleanly
    /// instead of clipping a long sentence.
    /// </summary>
    private StackPanel BuildDeleteWarningBody()
    {
        var panel = new StackPanel();
        panel.Children.Add(_deleteWarningTitle);

        foreach (var key in new[]
                 {
                     "DeleteWarningEffect",
                     "DeleteWarningSafe",
                     "DeleteWarningRecoverMenu",
                     "DeleteWarningRecoverRestart"
                 })
        {
            var line = new TextBlock
            {
                Text = ArchiveText.Get(key),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            _deleteWarningLines.Add(line);
            panel.Children.Add(line);
        }

        return panel;
    }

    private IArchiveOperations? CreateOperations()
    {
        try
        {
            return new ArchiveService(new ArchiveHostContext
            {
                ProviderId = _context.ProviderId,
                Workspace = _context.Workspace,
                Presentation = _context.WorkspacePresentation,
                Repository = _repository,
                Settings = () => _settings,
                HostPaperId = _context.PaperId
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // A host without cross-paper presentation cannot restore anything; the card still
            // lists the archive instead of failing to open.
            return null;
        }
    }

    private void AddToRow(UIElement element, int row)
    {
        Grid.SetRow(element, row);
        _root.Children.Add(element);
    }

    private sealed class StatCard
    {
        public StatCard(string label)
        {
            ValueText = new TextBlock { FontWeight = FontWeights.SemiBold };
            LabelText = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel();
            panel.Children.Add(ValueText);
            panel.Children.Add(LabelText);
            Root = new Border
            {
                Child = panel,
                MinWidth = 72,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7)
            };
        }

        public Border Root { get; }

        public TextBlock ValueText { get; }

        public TextBlock LabelText { get; }
    }

    private sealed class FilterChip
    {
        public FilterChip(ArchiveListFilter filter, Button button)
        {
            Filter = filter;
            Button = button;
        }

        public ArchiveListFilter Filter { get; }

        public Button Button { get; }
    }
}
