using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Pushes items to IMDb as soon as a user finishes them or marks them played.
/// </summary>
public sealed class PlaybackListener : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly ImdbSyncService _syncService;
    private readonly ILogger<PlaybackListener> _logger;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackListener"/> class.
    /// </summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="syncService">The sync service.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackListener(IUserDataManager userDataManager, ImdbSyncService syncService, ILogger<PlaybackListener> logger)
    {
        _userDataManager = userDataManager;
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (Plugin.Instance?.Configuration.EnableRealtimeSync != true
            || e.Item is null
            || e.UserData is null
            || !e.UserData.Played
            || e.SaveReason is not (UserDataSaveReason.PlaybackFinished or UserDataSaveReason.TogglePlayed))
        {
            return;
        }

        var userId = e.UserId;
        var item = e.Item;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _syncService.PushItemAsync(userId, item, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Realtime IMDb sync of {Name} failed", item.Name);
                }
            },
            _stopping.Token);
    }
}
