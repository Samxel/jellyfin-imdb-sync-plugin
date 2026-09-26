using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ImdbSync.Storage;

/// <summary>
/// Per-user IMDb sync settings and state.
/// </summary>
public class ImdbUserSettings
{
    /// <summary>
    /// The maximum number of activity log entries kept per user.
    /// </summary>
    public const int MaxLogEntries = 500;

    /// <summary>
    /// Gets or sets a value indicating whether syncing is enabled for this user.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the raw IMDb cookie as pasted by the user (full cookie header or just the at-main value).
    /// </summary>
    public string Cookie { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an explicit session id, used when the cookie does not contain one.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether movies are synced.
    /// </summary>
    public bool SyncMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether episodes are synced.
    /// </summary>
    public bool SyncEpisodes { get; set; } = true;

    /// <summary>
    /// Gets or sets the IMDb ids already marked as watched on IMDb.
    /// </summary>
#pragma warning disable CA2227 // Setter needed for JSON deserialization
    public HashSet<string> SyncedIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore CA2227

    /// <summary>
    /// Gets or sets the IMDb ids IMDb refused to mark as watched. They are not retried automatically.
    /// </summary>
#pragma warning disable CA2227 // Setter needed for JSON deserialization
    public HashSet<string> FailedIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore CA2227

    /// <summary>
    /// Gets or sets the activity log, oldest first, capped at <see cref="MaxLogEntries"/>.
    /// </summary>
#pragma warning disable CA2227 // Setter needed for JSON deserialization
#pragma warning disable CA1002 // Concrete list keeps JSON (de)serialization simple
    public List<SyncLogEntry> Log { get; set; } = [];
#pragma warning restore CA1002
#pragma warning restore CA2227

    /// <summary>
    /// Gets or sets the time of the last completed sync run.
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Gets or sets the time of the last successful IMDb update.
    /// </summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>
    /// Gets or sets the title of the last item successfully pushed to IMDb.
    /// </summary>
    public string? LastSyncedTitle { get; set; }

    /// <summary>
    /// Gets or sets the last error, if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether IMDb rejected the cookie (expired or invalid).
    /// </summary>
    public bool CookieExpired { get; set; }

    /// <summary>
    /// Appends entries to the activity log and trims it.
    /// </summary>
    /// <param name="entries">The entries.</param>
    public void AddLog(IEnumerable<SyncLogEntry> entries)
    {
        Log.AddRange(entries);
        if (Log.Count > MaxLogEntries)
        {
            Log.RemoveRange(0, Log.Count - MaxLogEntries);
        }
    }
}
