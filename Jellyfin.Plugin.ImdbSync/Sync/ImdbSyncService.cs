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
    private readonly ConcurrentDictionary<Guid, SyncProgress> _progress = new();

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

    private enum PushOutcome
    {
        Synced,
        Refused,
        Error,
        CookieRejected
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
        if (!IsWantedType(item, settings))
        {
            return null;
        }

        var id = item.GetProviderId(MetadataProvider.Imdb);
        return !string.IsNullOrWhiteSpace(id) && id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? id.Trim() : null;
    }

    /// <summary>
    /// Gets the progress of a running sync of the user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The progress, or <c>null</c> if no bulk sync is running.</returns>
    public SyncProgress? GetProgress(Guid userId) => _progress.TryGetValue(userId, out var p) ? p : null;

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

    /// <summary>
    /// Pushes a single item that was just marked played.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task PushItemAsync(Guid userId, BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var settings = _store.Get(userId);
        if (!IsActive(settings, out var credentials) || !IsWantedType(item, settings))
        {
            return;
        }

        var imdbId = GetSyncableImdbId(item, settings);
        if (imdbId is null)
        {
            var skipped = new SyncLogEntry
            {
                Status = SyncLogStatus.Info,
                Source = SyncSource.Realtime,
                Title = DisplayName(item),
                Message = "Skipped: no IMDb id in the Jellyfin metadata."
            };
            _store.Update(userId, s => s.AddLog([skipped]));
            return;
        }

        if (settings.SyncedIds.Contains(imdbId))
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

            var batch = new Batch(SyncSource.Realtime);
            if (await PushOneAsync(userId, credentials, imdbId, DisplayName(item), batch, cancellationToken).ConfigureAwait(false) == PushOutcome.Synced)
            {
                batch.Error = string.Empty;
            }

            Flush(userId, batch);
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

        // Remove cookies of users deleted while the plugin was not running.
        foreach (var orphan in _store.GetUserIds().Where(id => _userManager.GetUserById(id) is null).ToList())
        {
            _store.Delete(orphan);
            _logger.LogInformation("Deleted IMDb sync data of removed user {UserId}", orphan);
        }

        var userIds = _store.GetUserIds().Where(id => IsActive(_store.Get(id), out _)).ToList();
        for (var i = 0; i < userIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = i;
            var userProgress = new Progress<double>(p => progress.Report(((offset + (p / 100)) / userIds.Count) * 100));
            try
            {
                var result = await SyncUserAsync(userIds[i], SyncSource.Daily, userProgress, cancellationToken).ConfigureAwait(false);
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
    /// <param name="source">What triggered the sync, see <see cref="SyncSource"/>.</param>
    /// <param name="progress">The progress, may be <c>null</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<SyncResult> SyncUserAsync(Guid userId, string source, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var settings = _store.Get(userId);
        if (!IsActive(settings, out var credentials))
        {
            return new SyncResult(0, 0, 0, settings.CookieExpired ? "IMDb cookie expired - please paste a new one." : "Sync is disabled or no valid IMDb cookie is set.");
        }

        var userLock = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var batch = new Batch(source);
        try
        {
            var pending = GetPendingItems(userId, _store.Get(userId));
            var state = new SyncProgress { Total = pending.Count, Source = source };
            _progress[userId] = state;
            if (pending.Count > 0 || source != SyncSource.Daily)
            {
                batch.Log.Add(new SyncLogEntry { Source = source, Title = $"Sync started: {pending.Count} title(s) to send." });
            }

            int synced = 0, failed = 0, consecutiveErrors = 0;
            string? error = null;
            var cookieRejected = false;
            for (var i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (imdbId, name) = pending[i];
                state.Current = name;
                var outcome = await PushOneAsync(userId, credentials, imdbId, name, batch, cancellationToken).ConfigureAwait(false);
                state.Done = i + 1;
                switch (outcome)
                {
                    case PushOutcome.Synced:
                        synced++;
                        consecutiveErrors = 0;
                        break;
                    case PushOutcome.Refused:
                        failed++;
                        consecutiveErrors = 0;
                        break;
                    case PushOutcome.CookieRejected:
                        cookieRejected = true;
                        error = batch.Error;
                        break;
                    default:
                        failed++;
                        error = batch.Error;
                        consecutiveErrors++;
                        break;
                }

                if (cookieRejected)
                {
                    break;
                }

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    error = $"Stopped after {MaxConsecutiveErrors} errors in a row: {error}";
                    batch.Log.Add(new SyncLogEntry { Status = SyncLogStatus.Error, Source = source, Title = "Sync stopped.", Message = error });
                    break;
                }

                if (batch.PendingWrites >= FlushEvery)
                {
                    Flush(userId, batch);
                }

                progress?.Report((i + 1) * 100.0 / pending.Count);
                if (i < pending.Count - 1 && RequestDelayMs > 0)
                {
                    await Task.Delay(RequestDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }

            var remaining = pending.Count - synced - failed;
            var message = $"{synced} marked as watched, {failed} failed, {remaining} pending"
                + (error is null ? "." : $". Last error: {error}");
            if (pending.Count > 0 || source != SyncSource.Daily)
            {
                batch.Log.Add(new SyncLogEntry { Source = source, Title = "Sync finished: " + message });
            }

            batch.Error = error ?? string.Empty;
            Flush(userId, batch);
            _store.Update(userId, s => s.LastSyncUtc = DateTime.UtcNow);
            progress?.Report(100);
            return new SyncResult(synced, failed, remaining, message);
        }
        catch (OperationCanceledException)
        {
            batch.Log.Add(new SyncLogEntry { Source = source, Title = "Sync cancelled (server shutting down?). It continues with the next sync." });
            Flush(userId, batch);
            throw;
        }
        finally
        {
            _progress.TryRemove(userId, out _);
            userLock.Release();
        }
    }

    private static bool IsWantedType(BaseItem item, ImdbUserSettings settings) => item switch
    {
        Movie => settings.SyncMovies,
        Episode => settings.SyncEpisodes,
        _ => false
    };

    private static string DisplayName(BaseItem item)
        => item is Episode ep ? $"{ep.SeriesName} {ep.ParentIndexNumber}x{ep.IndexNumber} {ep.Name}" : item.Name;

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
                result.Add((imdbId, DisplayName(item)));
            }
        }

        return result;
    }

    private async Task<PushOutcome> PushOneAsync(Guid userId, ImdbCredentials credentials, string imdbId, string name, Batch batch, CancellationToken cancellationToken)
    {
        var entry = new SyncLogEntry { Source = batch.Source, ImdbId = imdbId, Title = name };
        batch.Log.Add(entry);
        try
        {
            if (await _imdbClient.MarkWatchedAsync(credentials, imdbId, cancellationToken).ConfigureAwait(false))
            {
                entry.Status = SyncLogStatus.Synced;
                batch.Synced.Add(imdbId);
                batch.LastTitle = name;
                _logger.LogDebug("Marked {Name} ({ImdbId}) as watched on IMDb for {UserId}", name, imdbId, userId);
                return PushOutcome.Synced;
            }

            entry.Status = SyncLogStatus.Refused;
            entry.Message = "IMDb did not confirm the update. Not retried automatically.";
            batch.Failed.Add(imdbId);
            _logger.LogWarning("IMDb refused to mark {Name} ({ImdbId}) as watched", name, imdbId);
            return PushOutcome.Refused;
        }
        catch (ImdbAuthException ex)
        {
            entry.Status = SyncLogStatus.Cookie;
            entry.Message = ex.Message;
            batch.Error = ex.Message;
            batch.CookieRejected = true;
            _logger.LogWarning("IMDb cookie of user {UserId} was rejected: {Message}", userId, ex.Message);
            return PushOutcome.CookieRejected;
        }
        catch (HttpRequestException ex)
        {
            entry.Message = ex.Message;
            batch.Error = ex.Message;
            _logger.LogWarning("IMDb request for {Name} ({ImdbId}) failed: {Message}", name, imdbId, ex.Message);

            // A GraphQL error (the request reached IMDb) will not fix itself; network errors are retried next time.
            if (ex.StatusCode is null && ex.Message.Contains("GraphQL", StringComparison.Ordinal))
            {
                entry.Status = SyncLogStatus.Refused;
                batch.Failed.Add(imdbId);
                return PushOutcome.Refused;
            }

            entry.Status = SyncLogStatus.Error;
            entry.Message += " Retried on the next sync.";
            return PushOutcome.Error;
        }
    }

    private void Flush(Guid userId, Batch batch)
    {
        if (batch.PendingWrites == 0 && batch.Error is null && !batch.CookieRejected)
        {
            return;
        }

        var synced = batch.Synced.ToList();
        var failed = batch.Failed.ToList();
        var log = batch.Log.ToList();
        var lastTitle = batch.LastTitle;
        var error = batch.Error;
        var cookieRejected = batch.CookieRejected;
        _store.Update(userId, s =>
        {
            s.SyncedIds.UnionWith(synced);
            s.FailedIds.UnionWith(failed);
            s.AddLog(log);
            if (synced.Count > 0)
            {
                s.LastSuccessUtc = DateTime.UtcNow;
                s.LastSyncedTitle = lastTitle ?? s.LastSyncedTitle;
            }

            if (cookieRejected)
            {
                s.CookieExpired = true;
            }

            if (error is not null)
            {
                s.LastError = error.Length == 0 ? null : error;
            }
        });

        batch.Synced.Clear();
        batch.Failed.Clear();
        batch.Log.Clear();
        batch.Error = null;
    }

    /// <summary>
    /// Changes collected during a sync, written to the store in batches.
    /// </summary>
    private sealed class Batch(string source)
    {
        public string Source { get; } = source;

        public List<string> Synced { get; } = [];

        public List<string> Failed { get; } = [];

        public List<SyncLogEntry> Log { get; } = [];

        public string? LastTitle { get; set; }

        /// <summary>
        /// Gets or sets the error to store: <c>null</c> keeps the stored one, empty clears it.
        /// </summary>
        public string? Error { get; set; }

        public bool CookieRejected { get; set; }

        public int PendingWrites => Synced.Count + Failed.Count + Log.Count;
    }
}
