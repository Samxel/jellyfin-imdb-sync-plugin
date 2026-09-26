using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ImdbSync.Configuration;

/// <summary>
/// Server-wide plugin configuration. Per-user settings (including IMDb cookies)
/// are stored separately, see <see cref="Storage.UserSettingsStore"/>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether items are pushed to IMDb as soon as they are marked played.
    /// </summary>
    public bool EnableRealtimeSync { get; set; } = true;

    /// <summary>
    /// Gets or sets the delay in milliseconds between two IMDb requests during a bulk sync.
    /// </summary>
    public int RequestDelayMs { get; set; } = 500;
}
