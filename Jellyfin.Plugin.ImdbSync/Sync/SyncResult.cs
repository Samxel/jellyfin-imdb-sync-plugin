namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// The result of a sync run for one user.
/// </summary>
/// <param name="Synced">Number of titles newly marked as watched on IMDb.</param>
/// <param name="Failed">Number of titles IMDb refused or that errored.</param>
/// <param name="Pending">Number of titles still waiting to be synced.</param>
/// <param name="Message">A human readable summary.</param>
public sealed record SyncResult(int Synced, int Failed, int Pending, string Message);
