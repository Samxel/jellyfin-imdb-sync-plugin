using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Gives every "Watchlist" playlist the IMDb watchlist poster and thumb and pins it to the top of the playlist list.
/// The files live in the plugin's data folder, outside the item's metadata folder, so Jellyfin's
/// automatic playlist collages never replace them.
/// </summary>
public class WatchlistImages
{
    // Sorts before every other name (Jellyfin's default playlist sort) while the name stays "Watchlist".
    private const string PinnedSortName = "!!!!!watchlist";

    private static readonly (ImageType Type, string Resource, string File, int Width, int Height)[] _images =
    [
        (ImageType.Primary, "Jellyfin.Plugin.ImdbSync.Web.watchlist-poster.png", "watchlist-poster.png", 333, 500),
        (ImageType.Thumb, "Jellyfin.Plugin.ImdbSync.Web.watchlist-thumb.png", "watchlist-thumb.png", 960, 540)
    ];

    // Newest "date added", so it is also first when sorting by date added (newest first).
    private static readonly DateTime _pinnedDateCreated = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _directory;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistImages"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    public WatchlistImages(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _directory = Path.Combine(applicationPaths.PluginConfigurationsPath, "ImdbSync", "images");
    }

    /// <summary>
    /// Sets the poster and thumb of the playlist unless it already uses them, and pins it to the top.
    /// </summary>
    /// <param name="playlist">The playlist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if the playlist was changed.</returns>
    public async Task<bool> EnsureAsync(BaseItem playlist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        var changed = false;
        foreach (var (type, resource, file, width, height) in _images)
        {
            var path = WriteFile(resource, file);
            var current = playlist.GetImageInfo(type, 0);
            if (current is not null && string.Equals(current.Path, path, StringComparison.Ordinal))
            {
                continue;
            }

            playlist.SetImage(
                new ItemImageInfo
                {
                    Path = path,
                    Type = type,
                    DateModified = File.GetLastWriteTimeUtc(path),
                    Width = width,
                    Height = height
                },
                0);
            changed = true;
        }

        var metadataChanged = false;
        if (!string.Equals(playlist.ForcedSortName, PinnedSortName, StringComparison.Ordinal))
        {
            playlist.ForcedSortName = PinnedSortName;
            metadataChanged = true;
        }

        if (playlist.DateCreated != _pinnedDateCreated)
        {
            playlist.DateCreated = _pinnedDateCreated;
            metadataChanged = true;
        }

        if (changed || metadataChanged)
        {
            await playlist.UpdateToRepositoryAsync(metadataChanged ? ItemUpdateType.MetadataEdit : ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }

        changed |= metadataChanged;

        return changed;
    }

    private string WriteFile(string resource, string file)
    {
        var path = Path.Combine(_directory, file);
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(_directory);
                using var source = typeof(WatchlistImages).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException("Missing embedded image " + resource);
                using var target = File.Create(path);
                source.CopyTo(target);
            }
        }

        return path;
    }
}
