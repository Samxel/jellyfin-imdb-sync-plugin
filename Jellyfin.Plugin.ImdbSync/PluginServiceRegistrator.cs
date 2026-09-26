using Jellyfin.Plugin.ImdbSync.Imdb;
using Jellyfin.Plugin.ImdbSync.Storage;
using Jellyfin.Plugin.ImdbSync.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ImdbSync;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(ImdbClient.HttpClientName);
        serviceCollection.AddSingleton<UserSettingsStore>();
        serviceCollection.AddSingleton<ImdbClient>();
        serviceCollection.AddSingleton<ImdbSyncService>();
        serviceCollection.AddHostedService<PlaybackListener>();
    }
}
