namespace QianShi.PaperTodo.Archive;

/// <summary>
/// Entry point. PaperTodo creates one fresh plugin object per body session or provider Runtime
/// activation, so this type must stay a stateless factory.
/// </summary>
public sealed class ArchivePlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new ArchiveSession(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new ArchiveRuntime(context);
}
