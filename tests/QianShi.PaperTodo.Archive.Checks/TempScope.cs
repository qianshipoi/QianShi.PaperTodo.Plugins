using System.IO;

namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>
/// One isolated directory per check. The archive store is never pointed at the real plugin folder;
/// every check gets a unique temp root that is removed when the check ends.
/// </summary>
internal sealed class TempScope : IDisposable
{
    public TempScope(string name)
    {
        var safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        Root = Path.Combine(
            Path.GetTempPath(),
            "QianShi.PaperTodo.Archive.Checks",
            safe + "-" + Guid.NewGuid().ToString("N")[..10]);
        System.IO.Directory.CreateDirectory(Root);
        StorePath = Path.Combine(Root, "archive-store.json");
    }

    public string Root { get; }

    public string StorePath { get; }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Root))
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }

            // Leave no empty parent behind either.
            var parent = Path.GetDirectoryName(Root);
            if (!string.IsNullOrEmpty(parent) &&
                System.IO.Directory.Exists(parent) &&
                !System.IO.Directory.EnumerateFileSystemEntries(parent).Any())
            {
                System.IO.Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a check.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
