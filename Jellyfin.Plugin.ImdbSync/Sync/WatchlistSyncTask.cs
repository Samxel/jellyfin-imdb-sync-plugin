using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Periodically picks up changes made to the IMDb watchlists (changes to the playlists are synced right away).
/// </summary>
public class WatchlistSyncTask : IScheduledTask
{
    private readonly WatchlistSyncService _syncService;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistSyncTask"/> class.
    /// </summary>
    /// <param name="syncService">The watchlist sync service.</param>
    public WatchlistSyncTask(WatchlistSyncService syncService)
    {
        _syncService = syncService;
    }

    /// <inheritdoc />
    public string Name => "Sync IMDb watchlist with playlist";

    /// <inheritdoc />
    public string Key => "ImdbSyncWatchlist";

    /// <inheritdoc />
    public string Description => "Two-way sync between each user's IMDb watchlist and their private Jellyfin playlist \"Watchlist\".";

    /// <inheritdoc />
    public string Category => "IMDb Sync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _syncService.SyncAllAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Jellyfin runs an interval task that never ran only one hour after startup; also run it at startup.
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks
        };
    }
}
