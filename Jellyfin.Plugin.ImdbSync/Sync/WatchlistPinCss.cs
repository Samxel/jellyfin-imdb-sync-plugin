using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ImdbSync.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Branding;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Keeps a small, clearly marked block in Jellyfin's custom CSS (Dashboard → General → Branding)
/// that shows each user's "Watchlist" playlist first in card grids. The rest of the custom CSS is left untouched.
/// </summary>
public partial class WatchlistPinCss
{
    private const string Start = "/* IMDb Sync: Watchlist playlists first (managed by the plugin, do not edit) */";
    private const string End = "/* IMDb Sync: end */";

    private readonly IConfigurationManager _configurationManager;
    private readonly UserSettingsStore _store;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistPinCss"/> class.
    /// </summary>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="store">The settings store.</param>
    public WatchlistPinCss(IConfigurationManager configurationManager, UserSettingsStore store)
    {
        _configurationManager = configurationManager;
        _store = store;
    }

    /// <summary>
    /// Rewrites the managed CSS block for all current Watchlist playlists, if it changed.
    /// </summary>
    public void Update()
    {
        var ids = _store.GetUserIds()
            .Select(_store.Get)
            .Where(s => s.WatchlistEnabled && s.WatchlistPlaylistId.HasValue)
            .Select(s => s.WatchlistPlaylistId!.Value.ToString("N", CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        lock (_lock)
        {
            var branding = (BrandingOptions)_configurationManager.GetConfiguration("branding");
            var current = branding.CustomCss ?? string.Empty;
            var withoutBlock = BlockRegex().Replace(current, string.Empty).TrimEnd();
            var updated = ids.Count == 0 ? withoutBlock : (withoutBlock.Length > 0 ? withoutBlock + "\n\n" : string.Empty) + Block(ids);
            if (!string.Equals(updated, current.TrimEnd(), StringComparison.Ordinal))
            {
                branding.CustomCss = updated;
                _configurationManager.SaveConfiguration("branding", branding);
            }
        }
    }

    private static string Block(IReadOnlyList<string> ids)
    {
        // Cards are laid out in flex containers (.vertical-wrap), so "order" moves the card to the front.
        var selectors = string.Join(",\n", ids.Select(id => $".itemsContainer > [data-id=\"{id}\"]"));
        return $"{Start}\n{selectors} {{\n    order: -1;\n}}\n{End}";
    }

    [GeneratedRegex(@"\s*/\* IMDb Sync: Watchlist playlists first.*?/\* IMDb Sync: end \*/", RegexOptions.Singleline)]
    private static partial Regex BlockRegex();
}
