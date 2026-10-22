using System.Globalization;
using System.Text.Json;

namespace QianShi.PaperTodo.Archive;

/// <summary>Manifest setting ids. They must stay in sync with plugin.json.</summary>
public static class ArchiveSettingIds
{
    public const string AutoStart = "autoStart";
    public const string AutoBackup = "autoBackup";
    public const string TopBarAction = "topBarAction";
    public const string ContextMenuAction = "contextMenuAction";
    public const string LockArchivedHidden = "lockArchivedHidden";
    public const string CaptureHostDeletes = "captureHostDeletes";
    public const string RetentionDays = "retentionDays";
    public const string MaxEntries = "maxEntries";
    public const string KeepRestored = "keepRestored";
    public const string ShowCountInCapsule = "showCountInCapsule";
}

/// <summary>
/// Normalized view of the host-managed global settings for this plugin. The host owns persistence
/// and rendering; the plugin only reads the JSON snapshot and validates every value.
/// </summary>
public sealed record ArchiveSettings(
    bool AutoStart,
    bool AutoBackup,
    bool TopBarAction,
    bool ContextMenuAction,
    bool LockArchivedHidden,
    bool CaptureHostDeletes,
    int RetentionDays,
    int MaxEntries,
    bool KeepRestored,
    bool ShowCountInCapsule)
{
    public static ArchiveSettings Default { get; } = new(
        AutoStart: true,
        AutoBackup: true,
        TopBarAction: true,
        ContextMenuAction: true,
        LockArchivedHidden: true,
        CaptureHostDeletes: true,
        RetentionDays: 0,
        MaxEntries: 500,
        KeepRestored: true,
        ShowCountInCapsule: true);

    public ArchiveRetention ToRetention() => new(RetentionDays, MaxEntries, KeepRestored);

    /// <summary>
    /// Reads the host settings document. Missing or malformed members fall back to the declared
    /// defaults so a partial host snapshot never disables data protection silently.
    /// </summary>
    public static ArchiveSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Default;
            }

            var root = document.RootElement;
            return new ArchiveSettings(
                AutoStart: ReadBool(root, ArchiveSettingIds.AutoStart, Default.AutoStart),
                AutoBackup: ReadBool(root, ArchiveSettingIds.AutoBackup, Default.AutoBackup),
                TopBarAction: ReadBool(root, ArchiveSettingIds.TopBarAction, Default.TopBarAction),
                ContextMenuAction: ReadBool(root, ArchiveSettingIds.ContextMenuAction, Default.ContextMenuAction),
                LockArchivedHidden: ReadBool(root, ArchiveSettingIds.LockArchivedHidden, Default.LockArchivedHidden),
                CaptureHostDeletes: ReadBool(root, ArchiveSettingIds.CaptureHostDeletes, Default.CaptureHostDeletes),
                RetentionDays: Math.Clamp(
                    ReadInt(root, ArchiveSettingIds.RetentionDays, Default.RetentionDays),
                    0,
                    3650),
                MaxEntries: Math.Clamp(
                    ReadInt(root, ArchiveSettingIds.MaxEntries, Default.MaxEntries),
                    50,
                    5000),
                KeepRestored: ReadBool(root, ArchiveSettingIds.KeepRestored, Default.KeepRestored),
                ShowCountInCapsule: ReadBool(root, ArchiveSettingIds.ShowCountInCapsule, Default.ShowCountInCapsule));
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => fallback
        };
    }
}
