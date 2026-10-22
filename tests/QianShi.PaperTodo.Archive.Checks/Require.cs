namespace QianShi.PaperTodo.Archive.Checks;

/// <summary>Thrown by <see cref="Require"/>; the runner reports it as one failed check.</summary>
internal sealed class CheckFailedException(string message) : Exception(message);

/// <summary>
/// Minimal assertion helper. Deliberately not a test framework: this project has to build offline
/// with no NuGet package, and the runner only needs "one failure must not abort the run".
/// </summary>
internal static class Require
{
    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new CheckFailedException(what);
        }
    }

    public static void False(bool condition, string what) => True(!condition, what);

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailedException($"{what}: expected <{Show(expected)}>, actual <{Show(actual)}>");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new CheckFailedException($"{what}: expected a value different from <{Show(unexpected)}>");
        }
    }

    public static void Null(object? value, string what)
    {
        if (value is not null)
        {
            throw new CheckFailedException($"{what}: expected null, actual <{Show(value)}>");
        }
    }

    public static void NotNull(object? value, string what)
    {
        if (value is null)
        {
            throw new CheckFailedException($"{what}: expected a value, got null");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        var expectedItems = expected.ToArray();
        var actualItems = actual.ToArray();
        if (expectedItems.Length != actualItems.Length ||
            !expectedItems.SequenceEqual(actualItems))
        {
            throw new CheckFailedException(
                $"{what}: expected [{string.Join(", ", expectedItems.Select(Show))}], " +
                $"actual [{string.Join(", ", actualItems.Select(Show))}]");
        }
    }

    public static void In<T>(T actual, IEnumerable<T> allowed, string what)
    {
        if (!allowed.Contains(actual))
        {
            throw new CheckFailedException(
                $"{what}: actual <{Show(actual)}> is not one of [{string.Join(", ", allowed.Select(Show))}]");
        }
    }

    private static string Show<T>(T value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        _ => value.ToString() ?? "null"
    };
}
