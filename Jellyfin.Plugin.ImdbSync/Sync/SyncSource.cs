namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// What triggered a sync, shown in the activity log.
/// </summary>
public static class SyncSource
{
    /// <summary>An item was just finished or marked played.</summary>
    public const string Realtime = "realtime";

    /// <summary>The user pressed "Sync now".</summary>
    public const string Manual = "manual";

    /// <summary>The daily scheduled task.</summary>
    public const string Daily = "daily";

    /// <summary>The IMDb watchlist / Jellyfin playlist sync.</summary>
    public const string Watchlist = "watchlist";
}
