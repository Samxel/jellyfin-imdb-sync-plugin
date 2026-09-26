namespace Jellyfin.Plugin.ImdbSync.Storage;

/// <summary>
/// Values of <see cref="SyncLogEntry.Status"/>.
/// </summary>
public static class SyncLogStatus
{
    /// <summary>Marked as watched on IMDb.</summary>
    public const string Synced = "synced";

    /// <summary>IMDb refused the title; not retried automatically.</summary>
    public const string Refused = "refused";

    /// <summary>A request failed; retried on the next sync.</summary>
    public const string Error = "error";

    /// <summary>IMDb rejected the cookie.</summary>
    public const string Cookie = "cookie";

    /// <summary>Informational entry, e.g. a sync run started or finished.</summary>
    public const string Info = "info";
}
