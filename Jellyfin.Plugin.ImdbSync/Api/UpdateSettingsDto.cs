namespace Jellyfin.Plugin.ImdbSync.Api;

/// <summary>
/// Settings update sent by a user. Null values are left unchanged.
/// </summary>
public class UpdateSettingsDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the sync is enabled.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets a new cookie. Empty or null keeps the current one.
    /// </summary>
    public string? Cookie { get; set; }

    /// <summary>
    /// Gets or sets a session id. Null keeps the current one, empty clears it.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether movies are synced.
    /// </summary>
    public bool? SyncMovies { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether episodes are synced.
    /// </summary>
    public bool? SyncEpisodes { get; set; }
}
