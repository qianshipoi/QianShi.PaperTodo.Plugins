namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>
/// Wires one ArchiveService to the fake host, an isolated real <see cref="ArchiveStore"/> in a temp
/// directory and a settings accessor, exactly the way the provider Runtime and the management
/// surface build it.
/// </summary>
internal sealed class ArchiveFixture : IDisposable
{
    /// <summary>The BodyProviderId of the plugin's own cards, from plugin.json.</summary>
    public const string ProviderId = "qianshi.archive.native";

    private readonly TempScope _scope;

    public ArchiveFixture(
        ArchiveSettings? settings = null,
        FakeHost? host = null,
        string? hostPaperId = null,
        string providerId = ProviderId)
    {
        _scope = new TempScope("archive");
        Store = new ArchiveStore(_scope.StorePath);
        Store.Load();
        Host = host ?? new FakeHost();
        CurrentSettings = settings ?? ArchiveSettings.Default;
        Service = new ArchiveService(new ArchiveHostContext
        {
            ProviderId = providerId,
            Workspace = Host,
            Presentation = Host,
            Repository = Store,
            Settings = () => CurrentSettings,
            HostPaperId = hostPaperId
        });
    }

    public FakeHost Host { get; }

    public ArchiveStore Store { get; }

    public ArchiveService Service { get; }

    public ArchiveSettings CurrentSettings { get; set; }

    /// <summary>Adds a Trashed record the way the runtime would after a host-side delete.</summary>
    public ArchiveEntry SeedEntry(ArchiveEntry entry)
    {
        Store.Upsert(entry, ArchiveChangeKind.EntryAdded);
        return entry;
    }

    public void Dispose()
    {
        Store.Dispose();
        _scope.Dispose();
    }
}
