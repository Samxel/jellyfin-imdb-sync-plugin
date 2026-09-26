using System;
using System.Net.Http;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.ImdbSync.Imdb;
using Jellyfin.Plugin.ImdbSync.Storage;
using Jellyfin.Plugin.ImdbSync.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
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
        // No shared cookie jar (IMDb Set-Cookie must never leak into another user's requests)
        // and no redirects (the cookie header must never be forwarded to another host).
        serviceCollection.AddHttpClient(ImdbClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        serviceCollection.AddSingleton<UserSettingsStore>();
        serviceCollection.AddSingleton<ImdbClient>();
        serviceCollection.AddSingleton<ImdbSyncService>();
        serviceCollection.AddHostedService<PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedConsumer>();
    }
}
