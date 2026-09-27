using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ImdbSync.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Syncs a user's watchlist shortly after they change their "Watchlist" playlist in Jellyfin.
/// </summary>
public sealed class PlaylistListener : IHostedService, IDisposable
{
    private static readonly TimeSpan _debounce = TimeSpan.FromSeconds(10);

    private readonly ILibraryManager _libraryManager;
    private readonly WatchlistSyncService _syncService;
    private readonly UserSettingsStore _store;
    private readonly ILogger<PlaylistListener> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _pending = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistListener"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="syncService">The watchlist sync service.</param>
    /// <param name="store">The settings store.</param>
    /// <param name="logger">The logger.</param>
    public PlaylistListener(ILibraryManager libraryManager, WatchlistSyncService syncService, UserSettingsStore store, ILogger<PlaylistListener> logger)
    {
        _libraryManager = libraryManager;
        _syncService = syncService;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated += OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
        foreach (var cts in _pending.Values)
        {
            cts.Dispose();
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Playlist playlist)
        {
            return;
        }

        var userId = playlist.OwnerUserId;
        var settings = _store.Get(userId);
        if (!settings.WatchlistEnabled || !playlist.Id.Equals(settings.WatchlistPlaylistId) || _syncService.IsOwnChange(userId))
        {
            return;
        }

        // Debounce: several quick edits cause one sync.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (_pending.TryGetValue(userId, out var previous))
        {
            previous.Cancel();
        }

        _pending[userId] = cts;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
                    await _syncService.SyncUserAsync(userId, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Watchlist sync after playlist change failed for {UserId}", userId);
                }
                finally
                {
                    _pending.TryRemove(new(userId, cts));
                    cts.Dispose();
                }
            },
            CancellationToken.None);
    }
}
