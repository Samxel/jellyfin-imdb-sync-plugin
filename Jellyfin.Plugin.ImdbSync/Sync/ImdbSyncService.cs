using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ImdbSync.Imdb;
using Jellyfin.Plugin.ImdbSync.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Pushes the Jellyfin watch history of users to their IMDb accounts.
/// </summary>
public class ImdbSyncService
{
    private const int FlushEvery = 20;
    private const int MaxConsecutiveErrors = 5;

    private readonly UserSettingsStore _store;
    private readonly ImdbClient _imdbClient;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<ImdbSyncService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbSyncService"/> class.
    /// </summary>
    /// <param name="store">The settings store.</param>
    /// <param name="imdbClient">The IMDb client.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public ImdbSyncService(
        UserSettingsStore store,
        ImdbClient imdbClient,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<ImdbSyncService> logger)
    {
        _store = store;
        _imdbClient = imdbClient;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    private static int RequestDelayMs => Math.Max(0, Plugin.Instance?.Configuration.RequestDelayMs ?? 500);

    /// <summary>
    /// Gets the IMDb id of an item if it is a type the user wants synced.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="settings">The user's settings.</param>
    /// <returns>The IMDb id or <c>null</c>.</returns>
    public static string? GetSyncableImdbId(BaseItem item, ImdbUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        var wanted = item switch
        {
            Movie => settings.SyncMovies,
            Episode => settings.SyncEpisodes,
            _ => false
        };

        if (!wanted)
        {
            return null;
        }

        var id = item.GetProviderId(MetadataProvider.Imdb);
        return !string.IsNullOrWhiteSpace(id) && id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? id.Trim() : null;
    }

    /// <summary>
    /// Pushes a single item that was just marked played.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task PushItemAsync(Guid userId, BaseItem item, CancellationToken cancellationToken)
    {
        var settings = _store.Get(userId);
        if (!IsActive(settings, out var credentials))
        {
            return;
        }

        var imdbId = GetSyncableImdbId(item, settings);
        if (imdbId is null || settings.SyncedIds.Contains(imdbId))
        {
            return;
        }

        var userLock = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Marking a whole series played fires one event per episode: re-check and throttle inside the lock.
            var current = _store.Get(userId);
            if (current.SyncedIds.Contains(imdbId) || current.FailedIds.Contains(imdbId))
            {
                return;
            }

            await PushAsync(userId, credentials, imdbId, item.Name, cancellationToken).ConfigureAwait(false);
            if (RequestDelayMs > 0)
            {
                await Task.Delay(RequestDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            userLock.Release();
        }
    }

    /// <summary>
    /// Syncs all users that enabled the sync.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var userIds = _store.GetUserIds().Where(id => IsActive(_store.Get(id), out _)).ToList();
        for (var i = 0; i < userIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = i;
            var userProgress = new Progress<double>(p => progress.Report(((offset + (p / 100)) / userIds.Count) * 100));
            try
            {
                var result = await SyncUserAsync(userIds[i], userProgress, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("IMDb sync for user {UserId}: {Message}", userIds[i], result.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "IMDb sync for user {UserId} failed", userIds[i]);
            }
        }

        progress.Report(100);
    }

    /// <summary>
    /// Pushes every played movie/episode of a user that is not yet on IMDb.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="progress">The progress, may be <c>null</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<SyncResult> SyncUserAsync(Guid userId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var settings = _store.Get(userId);
        if (!IsActive(settings, out var credentials))
        {
            return new SyncResult(0, 0, 0, settings.CookieExpired ? "IMDb cookie expired - please paste a new one." : "Sync is disabled or no valid IMDb cookie is set.");
        }

        var userLock = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = GetPendingItems(userId, _store.Get(userId));
            int synced = 0, failed = 0, consecutiveErrors = 0;
            var newlySynced = new List<string>();
            var newlyFailed = new List<string>();
            string? lastTitle = null;
            string? error = null;

            for (var i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (imdbId, name) = pending[i];
                try
                {
                    if (await _imdbClient.MarkWatchedAsync(credentials, imdbId, cancellationToken).ConfigureAwait(false))
                    {
                        synced++;
                        newlySynced.Add(imdbId);
                        lastTitle = name;
                        _logger.LogDebug("Marked {Name} ({ImdbId}) as watched on IMDb for {UserId}", name, imdbId, userId);
                    }
                    else
                    {
                        failed++;
                        newlyFailed.Add(imdbId);
                        _logger.LogWarning("IMDb refused to mark {Name} ({ImdbId}) as watched", name, imdbId);
                    }

                    consecutiveErrors = 0;
                }
                catch (ImdbAuthException ex)
                {
                    error = ex.Message;
                    _store.Update(userId, s => s.CookieExpired = true);
                    _logger.LogWarning("IMDb cookie of user {UserId} was rejected: {Message}", userId, ex.Message);
                    break;
                }
                catch (HttpRequestException ex)
                {
                    failed++;
                    error = ex.Message;
                    _logger.LogWarning("IMDb request for {Name} ({ImdbId}) failed: {Message}", name, imdbId, ex.Message);
                    if (ex.StatusCode is null && ex.Message.Contains("GraphQL", StringComparison.Ordinal))
                    {
                        newlyFailed.Add(imdbId);
                    }

                    if (++consecutiveErrors >= MaxConsecutiveErrors)
                    {
                        error = $"Stopped after {MaxConsecutiveErrors} errors in a row: {ex.Message}";
                        break;
                    }
                }

                if ((newlySynced.Count + newlyFailed.Count) >= FlushEvery)
                {
                    Flush(userId, newlySynced, newlyFailed, lastTitle, null);
                }

                progress?.Report((i + 1) * 100.0 / pending.Count);
                if (i < pending.Count - 1 && RequestDelayMs > 0)
                {
                    await Task.Delay(RequestDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }

            Flush(userId, newlySynced, newlyFailed, lastTitle, error ?? string.Empty);
            _store.Update(userId, s => s.LastSyncUtc = DateTime.UtcNow);
            progress?.Report(100);

            var remaining = pending.Count - synced - failed;
            var message = $"{synced} marked as watched, {failed} failed, {remaining} pending"
                + (error is null ? "." : $". Last error: {error}");
            return new SyncResult(synced, failed, remaining, message);
        }
        finally
        {
            userLock.Release();
        }
    }

    /// <summary>
    /// Gets a value indicating whether a sync for the user is running.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> if running.</returns>
    public bool IsSyncing(Guid userId) => _userLocks.TryGetValue(userId, out var l) && l.CurrentCount == 0;

    /// <summary>
    /// Gets the number of played items that still have to be pushed.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The count.</returns>
    public int CountPending(Guid userId) => GetPendingItems(userId, _store.Get(userId)).Count;

    private static bool IsActive(ImdbUserSettings settings, [NotNullWhen(true)] out ImdbCredentials? credentials)
    {
        credentials = ImdbCredentials.Parse(settings.Cookie, settings.SessionId);
        return settings.Enabled && !settings.CookieExpired && credentials is not null;
    }

    private List<(string ImdbId, string Name)> GetPendingItems(Guid userId, ImdbUserSettings settings)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return [];
        }

        var types = new List<BaseItemKind>();
        if (settings.SyncMovies)
        {
            types.Add(BaseItemKind.Movie);
        }

        if (settings.SyncEpisodes)
        {
            types.Add(BaseItemKind.Episode);
        }

        if (types.Count == 0)
        {
            return [];
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = types.ToArray(),
            IsPlayed = true,
            Recursive = true,
            IsVirtualItem = false
        });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string ImdbId, string Name)>();
        foreach (var item in items)
        {
            var imdbId = GetSyncableImdbId(item, settings);
            if (imdbId is not null
                && !settings.SyncedIds.Contains(imdbId)
                && !settings.FailedIds.Contains(imdbId)
                && seen.Add(imdbId))
            {
                result.Add((imdbId, item is Episode ep ? $"{ep.SeriesName} {ep.ParentIndexNumber}x{ep.IndexNumber} {ep.Name}" : item.Name));
            }
        }

        return result;
    }

    private async Task PushAsync(Guid userId, ImdbCredentials credentials, string imdbId, string name, CancellationToken cancellationToken)
    {
        try
        {
            var success = await _imdbClient.MarkWatchedAsync(credentials, imdbId, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                Flush(userId, [imdbId], [], name, string.Empty);
                _logger.LogInformation("Marked {Name} ({ImdbId}) as watched on IMDb for user {UserId}", name, imdbId, userId);
            }
            else
            {
                Flush(userId, [], [imdbId], null, $"IMDb refused to mark {name} ({imdbId}) as watched.");
                _logger.LogWarning("IMDb refused to mark {Name} ({ImdbId}) as watched", name, imdbId);
            }
        }
        catch (ImdbAuthException ex)
        {
            _store.Update(userId, s =>
            {
                s.CookieExpired = true;
                s.LastError = ex.Message;
            });
            _logger.LogWarning("IMDb cookie of user {UserId} was rejected: {Message}", userId, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            // Not recorded as failed: the daily sync retries it.
            _store.Update(userId, s => s.LastError = ex.Message);
            _logger.LogWarning("IMDb request for {Name} ({ImdbId}) failed: {Message}", name, imdbId, ex.Message);
        }
    }

    private void Flush(Guid userId, List<string> synced, List<string> failed, string? lastTitle, string? error)
    {
        if (synced.Count == 0 && failed.Count == 0 && error is null)
        {
            return;
        }

        var syncedCopy = synced.ToList();
        var failedCopy = failed.ToList();
        _store.Update(userId, s =>
        {
            s.SyncedIds.UnionWith(syncedCopy);
            s.FailedIds.UnionWith(failedCopy);
            if (syncedCopy.Count > 0)
            {
                s.LastSuccessUtc = DateTime.UtcNow;
                s.LastSyncedTitle = lastTitle ?? s.LastSyncedTitle;
            }

            if (error is not null)
            {
                s.LastError = error.Length == 0 ? null : error;
            }
        });
        synced.Clear();
        failed.Clear();
    }
}
