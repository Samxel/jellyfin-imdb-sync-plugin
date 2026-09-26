namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Progress of a running bulk sync.
/// </summary>
public class SyncProgress
{
    /// <summary>
    /// Gets or sets what triggered the sync.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of titles processed so far.
    /// </summary>
    public int Done { get; set; }

    /// <summary>
    /// Gets or sets the number of titles to process.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets the title currently being sent.
    /// </summary>
    public string? Current { get; set; }
}
