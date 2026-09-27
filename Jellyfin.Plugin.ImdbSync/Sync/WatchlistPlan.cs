using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Three-way merge of the IMDb watchlist and the Jellyfin playlist against the state of the last sync.
/// Changes made on one side since the last sync are applied to the other side; additions win over removals.
/// </summary>
public sealed class WatchlistPlan
{
    private WatchlistPlan()
    {
    }

    /// <summary>
    /// Gets the IMDb ids to add to the Jellyfin playlist.
    /// </summary>
    public IReadOnlySet<string> AddToJellyfin { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Gets the IMDb ids to add to the IMDb watchlist.
    /// </summary>
    public IReadOnlySet<string> AddToImdb { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Gets the IMDb ids to remove from the Jellyfin playlist.
    /// </summary>
    public IReadOnlySet<string> RemoveFromJellyfin { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Gets the IMDb ids to remove from the IMDb watchlist.
    /// </summary>
    public IReadOnlySet<string> RemoveFromImdb { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Gets the removals from the playlist that were held back by the safety limit.
    /// </summary>
    public IReadOnlySet<string> HeldBackFromJellyfin { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Gets the removals from the IMDb watchlist that were held back by the safety limit.
    /// </summary>
    public IReadOnlySet<string> HeldBackFromImdb { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Gets the titles newly added on IMDb that are not in the Jellyfin library.
    /// </summary>
    public IReadOnlySet<string> NotInLibrary { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Gets a value indicating whether this is the first sync (no removals, just a merge).
    /// </summary>
    public bool IsFirstSync { get; private init; }

    /// <summary>
    /// Creates the plan.
    /// </summary>
    /// <param name="imdb">IMDb ids currently on the IMDb watchlist.</param>
    /// <param name="jellyfin">IMDb ids of the movies and series (via their episodes) currently in the playlist.</param>
    /// <param name="baselineImdb">IMDb watchlist after the last sync, or <c>null</c>.</param>
    /// <param name="baselineJellyfin">Playlist after the last sync, or <c>null</c>.</param>
    /// <param name="library">IMDb ids of all movies and series in the library the user can access.</param>
    /// <returns>The plan.</returns>
    public static WatchlistPlan Create(
        IReadOnlySet<string> imdb,
        IReadOnlySet<string> jellyfin,
        IReadOnlySet<string>? baselineImdb,
        IReadOnlySet<string>? baselineJellyfin,
        IReadOnlySet<string> library)
    {
        ArgumentNullException.ThrowIfNull(imdb);
        ArgumentNullException.ThrowIfNull(jellyfin);
        ArgumentNullException.ThrowIfNull(library);

        var first = baselineImdb is null || baselineJellyfin is null;
        var bImdb = baselineImdb ?? new HashSet<string>();
        var bJf = baselineJellyfin ?? new HashSet<string>();

        var imdbAdded = first ? Set(imdb) : Set(imdb.Except(bImdb));
        var jfAdded = first ? Set(jellyfin) : Set(jellyfin.Except(bJf));
        var removedOnImdb = first ? Set() : Set(bImdb.Except(imdb));
        var removedOnJf = first ? Set() : Set(bJf.Except(jellyfin));

        // Removed on one side and still present on the other (unless re-added there): remove there too.
        var removeFromJf = Set(removedOnImdb.Intersect(jellyfin).Except(jfAdded));
        var removeFromImdb = Set(removedOnJf.Intersect(imdb).Except(imdbAdded));

        // Safety limit: never mass-delete because one side looked empty or broken.
        var heldJf = Set();
        if (IsMassRemoval(removeFromJf.Count, bImdb.Count))
        {
            heldJf = removeFromJf;
            removeFromJf = Set();
        }

        var heldImdb = Set();
        if (IsMassRemoval(removeFromImdb.Count, bJf.Count))
        {
            heldImdb = removeFromImdb;
            removeFromImdb = Set();
        }

        var imdbAfter = Set(imdb.Except(removeFromImdb));
        var jfAfter = Set(jellyfin.Except(removeFromJf));

        // Everything on the IMDb watchlist that is a movie in the library belongs in the playlist,
        // and everything in the playlist belongs on IMDb, except what the user removed on the other side.
        return new WatchlistPlan
        {
            IsFirstSync = first,
            RemoveFromJellyfin = removeFromJf,
            RemoveFromImdb = removeFromImdb,
            HeldBackFromJellyfin = heldJf,
            HeldBackFromImdb = heldImdb,
            AddToJellyfin = Set(imdbAfter.Intersect(library).Except(jfAfter).Except(removedOnJf.Except(imdbAdded))),
            AddToImdb = Set(jfAfter.Except(imdbAfter).Except(removedOnImdb.Except(jfAdded))),
            NotInLibrary = Set(imdbAdded.Except(library))
        };
    }

    private static bool IsMassRemoval(int removals, int baselineCount) => removals > 10 && removals * 2 > baselineCount;

    private static HashSet<string> Set(IEnumerable<string>? values = null)
        => new(values ?? [], StringComparer.OrdinalIgnoreCase);
}
