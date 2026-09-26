using System;
using Jellyfin.Plugin.ImdbSync.Sync;

namespace Jellyfin.Plugin.ImdbSync.Api;

/// <summary>
/// The IMDb sync status of a user, as returned by the API. Never contains the cookie itself.
/// </summary>
public class UserStatusDto
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the sync is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a valid-looking cookie is stored.
    /// </summary>
    public bool HasCookie { get; set; }

    /// <summary>
    /// Gets or sets the masked token.
    /// </summary>
    public string? MaskedToken { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a session id is known.
    /// </summary>
    public bool HasSessionId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether IMDb rejected the cookie.
    /// </summary>
    public bool CookieExpired { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether movies are synced.
    /// </summary>
    public bool SyncMovies { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether episodes are synced.
    /// </summary>
    public bool SyncEpisodes { get; set; }

    /// <summary>
    /// Gets or sets the number of titles already on IMDb.
    /// </summary>
    public int SyncedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of titles IMDb refused.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of played titles waiting to be synced.
    /// </summary>
    public int PendingCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a sync is running right now.
    /// </summary>
    public bool IsSyncing { get; set; }

    /// <summary>
    /// Gets or sets the progress of a running bulk sync.
    /// </summary>
    public SyncProgress? Progress { get; set; }

    /// <summary>
    /// Gets or sets the last sync time.
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Gets or sets the last success time.
    /// </summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>
    /// Gets or sets the last synced title.
    /// </summary>
    public string? LastSyncedTitle { get; set; }

    /// <summary>
    /// Gets or sets the last error.
    /// </summary>
    public string? LastError { get; set; }
}
