using System;

namespace Jellyfin.Plugin.ImdbSync.Storage;

/// <summary>
/// One entry of a user's IMDb sync activity log.
/// </summary>
public class SyncLogEntry
{
    /// <summary>
    /// Gets or sets the time of the entry.
    /// </summary>
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the result: <c>synced</c>, <c>refused</c>, <c>error</c>, <c>cookie</c> or <c>info</c>.
    /// </summary>
    public string Status { get; set; } = SyncLogStatus.Info;

    /// <summary>
    /// Gets or sets what triggered the sync: <c>realtime</c>, <c>manual</c> or <c>daily</c>.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the IMDb id, if the entry is about a title.
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the display title or a message.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets additional details (e.g. the error).
    /// </summary>
    public string? Message { get; set; }
}
