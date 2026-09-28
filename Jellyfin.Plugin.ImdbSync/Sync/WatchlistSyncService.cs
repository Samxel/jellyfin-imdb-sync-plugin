using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ImdbSync.Imdb;
using Jellyfin.Plugin.ImdbSync.Seerr;
using Jellyfin.Plugin.ImdbSync.Storage;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Keeps each user's IMDb watchlist and their private Jellyfin playlist "Watchlist" in sync, in both directions.
/// </summary>
public class WatchlistSyncService
{
    /// <summary>
    /// The name of the playlist created for each user.
    /// </summary>
    public const string PlaylistName = "Watchlist";

    private static readonly TimeSpan _ownChangeWindow = TimeSpan.FromSeconds(20);

    private readonly UserSettingsStore _store;
    private readonly ImdbClient _imdbClient;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly SeerrClient _seerrClient;
    private readonly WatchlistImages _images;
    private readonly WatchlistPinCss _pinCss;
    private readonly ILogger<WatchlistSyncService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _ownChangeUntil = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistSyncService"/> class.
    /// </summary>
    /// <param name="store">The settings store.</param>
    /// <param name="imdbClient">The IMDb client.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="playlistManager">The playlist manager.</param>
    /// <param name="seerrClient">The Seerr client.</param>
    /// <param name="images">The playlist images.</param>
    /// <param name="pinCss">The custom CSS that shows the playlist first.</param>
    /// <param name="logger">The logger.</param>
    public WatchlistSyncService(
        UserSettingsStore store,
        ImdbClient imdbClient,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPlaylistManager playlistManager,
        SeerrClient seerrClient,
        WatchlistImages images,
        WatchlistPinCss pinCss,
        ILogger<WatchlistSyncService> logger)
    {
        _store = store;
        _imdbClient = imdbClient;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _playlistManager = playlistManager;
        _seerrClient = seerrClient;
        _images = images;
        _pinCss = pinCss;
        _logger = logger;
    }

    private static int RequestDelayMs => Math.Max(0, Plugin.Instance?.Configuration.RequestDelayMs ?? 500);

    /// <summary>
    /// Gets a value indicating whether a watchlist sync for the user is running.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> if running.</returns>
    public bool IsSyncing(Guid userId) => _locks.TryGetValue(userId, out var l) && l.CurrentCount == 0;

    /// <summary>
    /// Gets a value indicating whether a playlist change was most likely made by this plugin itself.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> if the change should be ignored.</returns>
    public bool IsOwnChange(Guid userId) => _ownChangeUntil.TryGetValue(userId, out var until) && DateTime.UtcNow < until;

    /// <summary>
    /// Syncs the watchlists of all users that enabled it.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var userIds = _store.GetUserIds().Where(id => _store.Get(id).WatchlistEnabled).ToList();
        for (var i = 0; i < userIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncUserAsync(userIds[i], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "IMDb watchlist sync for user {UserId} failed", userIds[i]);
            }

            progress.Report((i + 1) * 100.0 / userIds.Count);
        }

        progress.Report(100);
    }

    /// <summary>
    /// Syncs the IMDb watchlist and the "Watchlist" playlist of one user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A summary of what happened.</returns>
    public async Task<string> SyncUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var settings = _store.Get(userId);
        var credentials = ImdbCredentials.Parse(settings.Cookie, settings.SessionId);
        var user = _userManager.GetUserById(userId);
        if (!settings.WatchlistEnabled || settings.CookieExpired || credentials is null || user is null)
        {
            return "Watchlist sync is disabled or no valid IMDb cookie is set.";
        }

        var userLock = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var log = new List<SyncLogEntry>();
        try
        {
            return await SyncLockedAsync(user, credentials, log, cancellationToken).ConfigureAwait(false);
        }
        catch (ImdbAuthException ex)
        {
            log.Add(Entry(SyncLogStatus.Cookie, "Watchlist sync stopped", null, ex.Message));
            _store.Update(userId, s =>
            {
                s.CookieExpired = true;
                s.LastError = ex.Message;
                s.WatchlistError = ex.Message;
            });
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            log.Add(Entry(SyncLogStatus.Error, "Watchlist sync failed", null, ex.Message + " Retried on the next sync."));
            _store.Update(userId, s => s.WatchlistError = ex.Message);
            _logger.LogWarning("IMDb watchlist sync for user {UserId} failed: {Message}", userId, ex.Message);
            return ex.Message;
        }
        finally
        {
            if (log.Count > 0)
            {
                _store.Update(userId, s => s.AddLog(log));
            }

            userLock.Release();
        }
    }

    /// <summary>
    /// Forgets the state of the last sync, so the next sync merges both sides without removing anything.
    /// </summary>
    /// <param name="userId">The user id.</param>
    public void ResetBaseline(Guid userId)
    {
        _store.Update(userId, s =>
        {
            s.WatchlistBaselineImdb = null;
            s.WatchlistBaselineJellyfin = null;
            s.WatchlistError = null;
        });
    }

    private static SyncLogEntry Entry(string status, string title, string? imdbId, string? message) => new()
    {
        Status = status,
        Source = SyncSource.Watchlist,
        Title = title,
        ImdbId = imdbId,
        Message = message
    };

    private async Task<string> SyncLockedAsync(User user, ImdbCredentials credentials, List<SyncLogEntry> log, CancellationToken cancellationToken)
    {
        var settings = _store.Get(user.Id);
        var (playlist, isNew) = await GetOrCreatePlaylistAsync(user, settings).ConfigureAwait(false);

        try
        {
            MarkOwnChange(user.Id);
            await _images.EnsureAsync(playlist, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not set the images of the Watchlist playlist of {User}", user.Username);
        }

        // Movies and series the user can see, by IMDb id.
        var library = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false
        }))
        {
            var id = item.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrWhiteSpace(id))
            {
                library.TryAdd(id.Trim(), item);
            }
        }

        // Current playlist entries. A movie counts as itself, any episode counts as its series.
        var entries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (child, item) in playlist.GetManageableItems())
        {
            var id = item switch
            {
                Movie => item.GetProviderId(MetadataProvider.Imdb),
                Episode episode => episode.Series?.GetProviderId(MetadataProvider.Imdb),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(id) && child.ItemId.HasValue)
            {
                var key = id.Trim();
                if (!entries.TryGetValue(key, out var list))
                {
                    entries[key] = list = [];
                }

                list.Add(child.ItemId.Value.ToString("N", CultureInfo.InvariantCulture));
            }
        }

        var imdb = await _imdbClient.GetWatchlistAsync(credentials, cancellationToken).ConfigureAwait(false);
        var imdbIds = new HashSet<string>(imdb.Keys, StringComparer.OrdinalIgnoreCase);
        var jfIds = new HashSet<string>(entries.Keys, StringComparer.OrdinalIgnoreCase);
        var plan = WatchlistPlan.Create(
            imdbIds,
            jfIds,
            isNew ? null : settings.WatchlistBaselineImdb,
            isNew ? null : settings.WatchlistBaselineJellyfin,
            new HashSet<string>(library.Keys, StringComparer.OrdinalIgnoreCase));

        string Name(string id) => library.TryGetValue(id, out var m) ? m.Name : imdb.GetValueOrDefault(id, id);

        // Jellyfin side: batch operations.
        if (plan.RemoveFromJellyfin.Count > 0)
        {
            MarkOwnChange(user.Id);
            await _playlistManager.RemoveItemFromPlaylistAsync(
                playlist.Id.ToString("N", CultureInfo.InvariantCulture),
                plan.RemoveFromJellyfin.SelectMany(id => entries[id]).ToList()).ConfigureAwait(false);
            jfIds.ExceptWith(plan.RemoveFromJellyfin);
            log.AddRange(plan.RemoveFromJellyfin.Select(id => Entry(SyncLogStatus.Synced, Name(id), id, "Removed on IMDb → removed from the Jellyfin playlist.")));
        }

        if (plan.AddToJellyfin.Count > 0)
        {
            // A series is represented by its first episode, so the playlist does not fill up with every episode.
            var toAdd = new List<Guid>();
            foreach (var id in plan.AddToJellyfin)
            {
                var item = library[id];
                if (item is Series series)
                {
                    var first = FirstEpisode(series, user);
                    if (first is null)
                    {
                        log.Add(Entry(SyncLogStatus.Info, Name(id), id, "On the IMDb watchlist, but the series has no episodes in Jellyfin."));
                        continue;
                    }

                    toAdd.Add(first.Id);
                    jfIds.Add(id);
                    log.Add(Entry(SyncLogStatus.Synced, Name(id), id, $"On the IMDb watchlist → added to the Jellyfin playlist (as {EpisodeLabel(first)})."));
                }
                else
                {
                    toAdd.Add(item.Id);
                    jfIds.Add(id);
                    log.Add(Entry(SyncLogStatus.Synced, Name(id), id, "On the IMDb watchlist → added to the Jellyfin playlist."));
                }
            }

            if (toAdd.Count > 0)
            {
                MarkOwnChange(user.Id);
                await _playlistManager.AddItemToPlaylistAsync(playlist.Id, toAdd, null, user.Id).ConfigureAwait(false);
            }
        }

        // IMDb side: one request per title. Failed removals stay "pending" in the baseline and are retried.
        var pendingImdbRemovals = new HashSet<string>(plan.HeldBackFromImdb, StringComparer.OrdinalIgnoreCase);
        var errors = 0;
        foreach (var id in plan.RemoveFromImdb)
        {
            if (await TryImdbAsync(() => _imdbClient.RemoveFromWatchlistAsync(credentials, id, cancellationToken), log, id, Name(id), "Removed from the Jellyfin playlist → removed from the IMDb watchlist.", cancellationToken).ConfigureAwait(false))
            {
                imdbIds.Remove(id);
            }
            else
            {
                pendingImdbRemovals.Add(id);
                errors++;
            }
        }

        foreach (var id in plan.AddToImdb)
        {
            if (await TryImdbAsync(() => _imdbClient.AddToWatchlistAsync(credentials, id, cancellationToken), log, id, Name(id), "Added to the Jellyfin playlist → added to the IMDb watchlist.", cancellationToken).ConfigureAwait(false))
            {
                imdbIds.Add(id);
            }
            else
            {
                errors++;
            }
        }

        log.AddRange(plan.NotInLibrary.Select(id => Entry(SyncLogStatus.Info, Name(id), id, "On the IMDb watchlist, but not in your Jellyfin library.")));

        if (SeerrClient.RequestsEnabled)
        {
            var missing = imdbIds.Where(id => !library.ContainsKey(id) && !settings.SeerrHandledIds.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                await RequestOnSeerrAsync(user, missing, Name, log, cancellationToken).ConfigureAwait(false);
            }
        }

        string? warning = null;
        if (plan.HeldBackFromJellyfin.Count > 0 || plan.HeldBackFromImdb.Count > 0)
        {
            warning = $"Safety stop: {plan.HeldBackFromJellyfin.Count + plan.HeldBackFromImdb.Count} removals held back because they would remove most of the list. "
                + "If this is intended, remove the titles on both sides, or use \"Re-merge watchlist\".";
            log.Add(Entry(SyncLogStatus.Error, "Watchlist: removals held back", null, warning));
        }

        // New baseline: the state now, plus removals that still have to happen (so they are detected again).
        var baselineImdb = new HashSet<string>(imdbIds.Union(plan.HeldBackFromJellyfin), StringComparer.OrdinalIgnoreCase);
        var baselineJf = new HashSet<string>(jfIds.Union(pendingImdbRemovals), StringComparer.OrdinalIgnoreCase);
        var changes = plan.AddToJellyfin.Count + plan.RemoveFromJellyfin.Count + plan.AddToImdb.Count + plan.RemoveFromImdb.Count - errors;
        _store.Update(user.Id, s =>
        {
            s.WatchlistPlaylistId = playlist.Id;
            s.WatchlistBaselineImdb = baselineImdb;
            s.WatchlistBaselineJellyfin = baselineJf;
            s.WatchlistLastSyncUtc = DateTime.UtcNow;
            s.WatchlistImdbCount = imdbIds.Count;
            s.WatchlistPlaylistCount = jfIds.Count;
            s.WatchlistError = warning ?? (errors > 0 ? $"{errors} IMDb update(s) failed, retried on the next sync." : null);
        });

        try
        {
            _pinCss.Update();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidCastException)
        {
            _logger.LogWarning(ex, "Could not update the custom CSS that shows the Watchlist playlist first");
        }

        var summary = $"{changes} change(s); IMDb watchlist {imdbIds.Count}, playlist {jfIds.Count}" + (plan.IsFirstSync ? " (first sync: merged)" : string.Empty);
        if (changes > 0 || plan.IsFirstSync)
        {
            log.Add(Entry(SyncLogStatus.Info, "Watchlist synced: " + summary, null, null));
        }

        _logger.LogInformation("IMDb watchlist sync for {User}: {Summary}", user.Username, summary);
        return summary;
    }

    private async Task RequestOnSeerrAsync(User user, List<string> missing, Func<string, string> name, List<SyncLogEntry> log, CancellationToken cancellationToken)
    {
        const int MaxPerSync = 25;
        Dictionary<Guid, int> users;
        try
        {
            users = await _seerrClient.GetUserMapAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            log.Add(Entry(SyncLogStatus.Error, "Seerr not reachable", null, ex.Message + " Retried on the next sync."));
            return;
        }

        if (!users.TryGetValue(user.Id, out var seerrUserId))
        {
            log.Add(Entry(SyncLogStatus.Error, "No Seerr account", null, "Your Jellyfin account is not linked to a Seerr user, so missing titles cannot be requested."));
            return;
        }

        var handled = new List<string>();
        foreach (var id in missing.Take(MaxPerSync))
        {
            try
            {
                var title = await _seerrClient.FindByImdbIdAsync(id, cancellationToken).ConfigureAwait(false);
                if (title is null)
                {
                    log.Add(Entry(SyncLogStatus.Info, name(id), id, "Not in your library and not found on Seerr."));
                }
                else if (title.IsRequestedOrAvailable)
                {
                    log.Add(Entry(SyncLogStatus.Info, title.Title, id, "Not in your library; already requested or available on Seerr."));
                }
                else
                {
                    await _seerrClient.RequestAsync(title, seerrUserId, cancellationToken).ConfigureAwait(false);
                    log.Add(Entry(SyncLogStatus.Synced, title.Title, id, title.MediaType == "tv" ? "Not in your library → series requested on Seerr (all seasons)." : "Not in your library → requested on Seerr."));
                }

                handled.Add(id);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict || ex.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
            {
                // Seerr already has a request or media entry for this title (a 409, or a duplicate-key error from its database).
                log.Add(Entry(SyncLogStatus.Info, name(id), id, "Not in your library; Seerr already has an entry for it (" + ex.Message + ")."));
                handled.Add(id);
            }
            catch (HttpRequestException ex)
            {
                log.Add(Entry(SyncLogStatus.Error, name(id), id, "Seerr request failed: " + ex.Message + " Retried on the next sync."));
                _logger.LogWarning("Seerr request for {ImdbId} failed: {Message}", id, ex.Message);
            }
        }

        if (handled.Count > 0)
        {
            _store.Update(user.Id, s => s.SeerrHandledIds.UnionWith(handled));
        }
    }

    private async Task<bool> TryImdbAsync(Func<Task> action, List<SyncLogEntry> log, string id, string name, string successMessage, CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(false);
            log.Add(Entry(SyncLogStatus.Synced, name, id, successMessage));
            return true;
        }
        catch (HttpRequestException ex)
        {
            log.Add(Entry(SyncLogStatus.Error, name, id, ex.Message + " Retried on the next sync."));
            _logger.LogWarning("IMDb watchlist update for {ImdbId} failed: {Message}", id, ex.Message);
            return false;
        }
        finally
        {
            if (RequestDelayMs > 0)
            {
                await Task.Delay(RequestDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Episode? FirstEpisode(Series series, User user)
    {
        var episodes = series.GetEpisodes(user, new DtoOptions(false), false).OfType<Episode>().Where(e => !e.IsVirtualItem).ToList();
        return episodes.FirstOrDefault(e => e.ParentIndexNumber >= 1) ?? episodes.FirstOrDefault();
    }

    private static string EpisodeLabel(Episode episode)
        => string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", episode.ParentIndexNumber ?? 0, episode.IndexNumber ?? 0);

    private void MarkOwnChange(Guid userId) => _ownChangeUntil[userId] = DateTime.UtcNow + _ownChangeWindow;

    private async Task<(Playlist Playlist, bool IsNew)> GetOrCreatePlaylistAsync(User user, ImdbUserSettings settings)
    {
        if (settings.WatchlistPlaylistId is Guid id
            && _libraryManager.GetItemById(id) is Playlist existing
            && existing.OwnerUserId.Equals(user.Id))
        {
            return (existing, false);
        }

        // Adopt a playlist the user already has with that name, otherwise create a private one.
        var adopted = _playlistManager.GetPlaylists(user.Id)
            .FirstOrDefault(p => p.OwnerUserId.Equals(user.Id) && string.Equals(p.Name, PlaylistName, StringComparison.OrdinalIgnoreCase));
        if (adopted is not null)
        {
            return (adopted, true);
        }

        MarkOwnChange(user.Id);
        var created = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = PlaylistName,
            UserId = user.Id,
            MediaType = MediaType.Video,
            ItemIdList = [],
            Public = false
        }).ConfigureAwait(false);

        var playlist = _libraryManager.GetItemById(Guid.Parse(created.Id)) as Playlist
            ?? throw new InvalidOperationException("The Watchlist playlist could not be created.");
        _store.Update(user.Id, s => s.AddLog([Entry(SyncLogStatus.Info, "Created your private playlist \"" + PlaylistName + "\"", null, null)]));
        return (playlist, true);
    }
}
