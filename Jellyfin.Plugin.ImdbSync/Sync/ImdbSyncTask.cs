using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Daily task that pushes everything not yet synced (catches items the realtime sync missed).
/// </summary>
public class ImdbSyncTask : IScheduledTask
{
    private readonly ImdbSyncService _syncService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbSyncTask"/> class.
    /// </summary>
    /// <param name="syncService">The sync service.</param>
    public ImdbSyncTask(ImdbSyncService syncService)
    {
        _syncService = syncService;
    }

    /// <inheritdoc />
    public string Name => "Sync watch history to IMDb";

    /// <inheritdoc />
    public string Key => "ImdbSyncWatchHistory";

    /// <inheritdoc />
    public string Description => "Marks every played movie and episode as watched on the IMDb account of each user who enabled IMDb Sync.";

    /// <inheritdoc />
    public string Category => "IMDb Sync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _syncService.SyncAllAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }
}
