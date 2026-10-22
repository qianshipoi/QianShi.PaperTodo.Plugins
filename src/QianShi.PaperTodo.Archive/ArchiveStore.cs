using System.IO;
using System.Text.Json;

namespace QianShi.PaperTodo.Archive;

/// <summary>
/// Plugin-owned archive storage under &lt;plugin directory&gt;/.runtime/archive-store.json.
///
/// PaperTodo explicitly does not provide a business recovery system for plugins, so this type owns
/// format versioning, atomic replacement, corrupt-file preservation and debounced writes. It is
/// deliberately not host-managed state: the archive can grow past the per-paper/runtime JSON limits
/// and must outlive every individual paper.
///
/// Threading: every mutation takes one lock and the <see cref="Changed"/> event is raised outside
/// it. Host calls may arrive from a worker thread, so no caller may assume the UI thread.
/// </summary>
internal sealed class ArchiveStore : IArchiveRepository, IDisposable
{
    private const string FileName = "archive-store.json";
    private const int DebounceMilliseconds = 700;
    private const long MaximumDocumentBytes = 32L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly Lazy<ArchiveStore> SharedLazy =
        new(() => new ArchiveStore(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<ArchiveEntry> _entries = [];
    private readonly Dictionary<string, PaperMirror> _mirrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _archivedPaperIndex = new(StringComparer.Ordinal);
    private readonly string? _overridePath;

    private Timer? _debounce;
    private string? _path;
    private bool _loaded;
    private bool _dirty;
    private bool _readOnly;
    private bool _disposed;

    /// <summary>The one store for this process. Paper sessions and the provider Runtime share it.</summary>
    public static ArchiveStore Shared => SharedLazy.Value;

    private ArchiveStore()
    {
    }

    /// <summary>
    /// Isolated instance on an explicit file. Used by tests so a verification run can never touch
    /// the user's real archive.
    /// </summary>
    internal ArchiveStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        _overridePath = Path.GetFullPath(storagePath);
        _path = _overridePath;
    }

    public event Action<ArchiveChangeKind>? Changed;

    public string StoragePath => _path ?? ResolvePath();

    public string? LastError { get; private set; }

    private string ResolvePath() => _overridePath ?? DefaultPath();

    private static string DefaultPath()
    {
        var pluginDirectory = Path.GetDirectoryName(
            typeof(ArchivePlugin).Assembly.Location) ?? AppContext.BaseDirectory;
        var runtimeDirectory = Path.Combine(pluginDirectory, ".runtime");
        return Path.Combine(runtimeDirectory, FileName);
    }

    public void Load()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _path = ResolvePath();
            LoadCore(_path);
            _loaded = true;
        }

        Raise(ArchiveChangeKind.Reloaded);
    }

    private void LoadCore(string path)
    {
        var document = TryRead(path);
        if (document == null)
        {
            return;
        }

        _entries.Clear();
        _mirrors.Clear();
        _archivedPaperIndex.Clear();

        foreach (var entry in document.Entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id))
            {
                continue;
            }

            _entries.Add(entry);
        }

        foreach (var pair in document.Mirrors)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
            {
                continue;
            }

            _mirrors[pair.Key] = pair.Value;
        }

        // Rebuild the paper index instead of trusting the persisted copy: an older build, a manual
        // edit or a half-applied migration must never leave a stale "this paper is archived" claim.
        foreach (var entry in _entries)
        {
            if (entry.Kind == ArchiveEntryKind.Paper &&
                entry.State == ArchiveEntryState.Archived &&
                !string.IsNullOrWhiteSpace(entry.PaperId))
            {
                _archivedPaperIndex[entry.PaperId] = entry.Id;
            }
        }
    }

    private ArchiveDocument? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<ArchiveDocument>(json, JsonOptions);
            if (document == null)
            {
                PreserveCorruptFile(path, "empty document");
                return null;
            }

            if (document.Version > ArchiveDocument.CurrentVersion)
            {
                // A newer plugin wrote this file. Refuse to read it AND refuse to write it: a
                // downgrade must not rewrite newer records with an older shape. Read-only is the
                // whole protection, so it is latched before anything else can mutate the store.
                _readOnly = true;
                LastError = "archive_store_newer_version";
                return null;
            }

            if (document.Version < ArchiveDocument.CurrentVersion)
            {
                document = Migrate(document);
            }

            return document;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LastError = "archive_store_read_failed";
            PreserveCorruptFile(path, ex.Message);
            return null;
        }
    }

    private static ArchiveDocument Migrate(ArchiveDocument document)
    {
        // Version 1 is the first released shape; the hook exists so a later build can lift older
        // records instead of dropping them. Entries that cannot be understood are kept as-is.
        document.Version = ArchiveDocument.CurrentVersion;
        return document;
    }

    /// <summary>Never overwrite evidence of a failed read: keep the original bytes beside the file.</summary>
    private void PreserveCorruptFile(string path, string reason)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var target = path + ".corrupt-" + stamp;
            File.Copy(path, target, overwrite: true);
            LastError = "archive_store_preserved_corrupt:" + reason;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "archive_store_read_failed";
        }
    }

    public IReadOnlyList<ArchiveEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries
                .OrderByDescending(entry => entry.CapturedAt)
                .Select(entry => entry.Clone())
                .ToArray();
        }
    }

    public ArchiveEntry? Find(string entryId)
    {
        if (string.IsNullOrEmpty(entryId))
        {
            return null;
        }

        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(
                candidate => string.Equals(candidate.Id, entryId, StringComparison.Ordinal));
            return entry?.Clone();
        }
    }

    public string? FindEntryIdByArchivedPaper(string paperId)
    {
        if (string.IsNullOrEmpty(paperId))
        {
            return null;
        }

        lock (_gate)
        {
            return _archivedPaperIndex.TryGetValue(paperId, out var entryId) ? entryId : null;
        }
    }

    public PaperMirror? FindMirror(string paperId)
    {
        if (string.IsNullOrEmpty(paperId))
        {
            return null;
        }

        lock (_gate)
        {
            return _mirrors.TryGetValue(paperId, out var mirror) ? mirror.Clone() : null;
        }
    }

    public IReadOnlyList<PaperMirror> SnapshotMirrors()
    {
        lock (_gate)
        {
            return _mirrors.Values.Select(mirror => mirror.Clone()).ToArray();
        }
    }

    public void Upsert(ArchiveEntry entry, ArchiveChangeKind kind = ArchiveChangeKind.EntryUpdated)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            entry.Id = Guid.NewGuid().ToString("N");
        }

        var change = kind;
        lock (_gate)
        {
            var index = _entries.FindIndex(
                candidate => string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                _entries.Add(entry);
                change = ArchiveChangeKind.EntryAdded;
            }
            else
            {
                _entries[index] = entry;
            }

            RefreshPaperIndexLocked(entry);
            _dirty = true;
        }

        Raise(change);
        Save();
    }

    public void Remove(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return;
        }

        bool removed;
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(
                candidate => string.Equals(candidate.Id, entryId, StringComparison.Ordinal));
            removed = entry != null && _entries.Remove(entry);
            if (removed)
            {
                if (entry!.Kind == ArchiveEntryKind.Paper &&
                    _archivedPaperIndex.TryGetValue(entry.PaperId, out var mapped) &&
                    string.Equals(mapped, entry.Id, StringComparison.Ordinal))
                {
                    _archivedPaperIndex.Remove(entry.PaperId);
                }

                _dirty = true;
            }
        }

        if (removed)
        {
            Raise(ArchiveChangeKind.EntryRemoved);
            Save();
        }
    }

    public void SetMirror(PaperMirror mirror)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (string.IsNullOrWhiteSpace(mirror.PaperId))
        {
            return;
        }

        lock (_gate)
        {
            _mirrors[mirror.PaperId] = mirror;
            _dirty = true;
        }

        Raise(ArchiveChangeKind.MirrorsChanged);
        Save();
    }

    public void RemoveMirror(string paperId)
    {
        if (string.IsNullOrWhiteSpace(paperId))
        {
            return;
        }

        bool removed;
        lock (_gate)
        {
            removed = _mirrors.Remove(paperId);
            if (removed)
            {
                _dirty = true;
            }
        }

        if (removed)
        {
            Raise(ArchiveChangeKind.MirrorsChanged);
            Save();
        }
    }

    public void ClearMirrors()
    {
        lock (_gate)
        {
            if (_mirrors.Count == 0)
            {
                return;
            }

            _mirrors.Clear();
            _dirty = true;
        }

        Raise(ArchiveChangeKind.MirrorsChanged);
        Save();
    }

    public void ApplyRetention(ArchiveRetention retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        var now = DateTimeOffset.Now;
        var removedAny = false;

        lock (_gate)
        {
            // Archived records are never pruned: they are the user's deliberate "keep but hide"
            // choice, and the backing paper still exists in PaperTodo.
            var removable = _entries
                .Where(entry => entry.State != ArchiveEntryState.Archived)
                .ToList();

            foreach (var entry in removable)
            {
                var expired = retention.RetentionDays > 0 &&
                    entry.CapturedAt < now.AddDays(-retention.RetentionDays);
                var dropRestored = entry.State == ArchiveEntryState.Restored && !retention.KeepRestored;
                if ((expired || dropRestored) && _entries.Remove(entry))
                {
                    removedAny = true;
                }
            }

            var history = _entries
                .Where(entry => entry.State != ArchiveEntryState.Archived)
                .OrderByDescending(entry => entry.CapturedAt)
                .ToList();
            if (retention.MaxEntries > 0)
            {
                foreach (var entry in history.Skip(retention.MaxEntries))
                {
                    if (_entries.Remove(entry))
                    {
                        removedAny = true;
                    }
                }
            }

            if (removedAny)
            {
                _archivedPaperIndex.Clear();
                foreach (var entry in _entries.Where(item =>
                             item.Kind == ArchiveEntryKind.Paper &&
                             item.State == ArchiveEntryState.Archived &&
                             !string.IsNullOrWhiteSpace(item.PaperId)))
                {
                    _archivedPaperIndex[entry.PaperId] = entry.Id;
                }

                _dirty = true;
            }
        }

        if (removedAny)
        {
            Raise(ArchiveChangeKind.EntryRemoved);
            Save();
        }
    }

    public ArchiveStatistics Statistics()
    {
        lock (_gate)
        {
            return new ArchiveStatistics(
                Archived: _entries.Count(entry => entry.State == ArchiveEntryState.Archived),
                Trashed: _entries.Count(entry => entry.State == ArchiveEntryState.Trashed),
                Restored: _entries.Count(entry => entry.State == ArchiveEntryState.Restored),
                MirroredPapers: _mirrors.Count);
        }
    }

    private void RefreshPaperIndexLocked(ArchiveEntry entry)
    {
        if (entry.Kind != ArchiveEntryKind.Paper || string.IsNullOrWhiteSpace(entry.PaperId))
        {
            return;
        }

        if (entry.State == ArchiveEntryState.Archived)
        {
            _archivedPaperIndex[entry.PaperId] = entry.Id;
        }
        else if (_archivedPaperIndex.TryGetValue(entry.PaperId, out var mapped) &&
                 string.Equals(mapped, entry.Id, StringComparison.Ordinal))
        {
            _archivedPaperIndex.Remove(entry.PaperId);
        }
    }

    private void Raise(ArchiveChangeKind kind)
    {
        try
        {
            Changed?.Invoke(kind);
        }
        catch
        {
            // A surface that throws while refreshing must not break data protection.
        }
    }

    public void Save(bool immediate = false)
    {
        if (_disposed || _readOnly)
        {
            return;
        }

        lock (_gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                _path ??= ResolvePath();
            }

            _dirty = true;
        }

        if (immediate)
        {
            Flush();
            return;
        }

        lock (_gate)
        {
            _debounce ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    /// <summary>Writes the current document through one atomic replacement.</summary>
    private void Flush()
    {
        if (_disposed || _readOnly)
        {
            return;
        }

        ArchiveDocument document;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            TrimMirrorsLocked();
            document = new ArchiveDocument
            {
                Version = ArchiveDocument.CurrentVersion,
                Entries = _entries.Select(entry => entry.Clone()).ToList(),
                Mirrors = _mirrors.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.Ordinal),
                ArchivedPaperIndex = new Dictionary<string, string>(
                    _archivedPaperIndex,
                    StringComparer.Ordinal)
            };
            _dirty = false;
        }

        _writeGate.Wait();
        try
        {
            WriteDocument(document);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void WriteDocument(ArchiveDocument document)
    {
        if (_readOnly)
        {
            return;
        }

        var path = StoragePath;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(document, JsonOptions);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Data protection must survive a full disk or a locked file. The in-memory document is
            // still authoritative; the next flush retries.
            LastError = "archive_store_write_failed";
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    /// <summary>
    /// Keeps the mirror set bounded. Mirrors are a recoverability cache, not the archive itself, so
    /// the oldest entries are dropped first and active records are never touched.
    /// </summary>
    private void TrimMirrorsLocked()
    {
        if (_mirrors.Count == 0)
        {
            return;
        }

        long approximate = 0;
        foreach (var mirror in _mirrors.Values)
        {
            approximate += 128;
            approximate += (mirror.Title?.Length ?? 0) * 2L;
            approximate += (mirror.NoteContent?.Length ?? 0) * 2L;
            approximate += mirror.Todos.Sum(item => (item.Text?.Length ?? 0) * 2L + 96L);
        }

        if (approximate <= MaximumDocumentBytes)
        {
            return;
        }

        foreach (var mirror in _mirrors.Values
                     .OrderBy(item => item.CapturedAt)
                     .ToList())
        {
            if (approximate <= MaximumDocumentBytes)
            {
                break;
            }

            approximate -= (mirror.NoteContent?.Length ?? 0) * 2L;
            _mirrors.Remove(mirror.PaperId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _debounce?.Dispose();
        }
        catch
        {
            // Shutdown best effort only.
        }

        FlushOnceOnDispose();
        _writeGate.Dispose();
    }

    private void FlushOnceOnDispose()
    {
        if (_readOnly)
        {
            return;
        }

        try
        {
            ArchiveDocument? document = null;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                document = new ArchiveDocument
                {
                    Version = ArchiveDocument.CurrentVersion,
                    Entries = _entries.Select(entry => entry.Clone()).ToList(),
                    Mirrors = _mirrors.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Clone(),
                        StringComparer.Ordinal),
                    ArchivedPaperIndex = new Dictionary<string, string>(
                        _archivedPaperIndex,
                        StringComparer.Ordinal)
                };
                _dirty = false;
            }

            if (document != null)
            {
                WriteDocument(document);
            }
        }
        catch
        {
            // Nothing may throw during host shutdown.
        }
    }
}
