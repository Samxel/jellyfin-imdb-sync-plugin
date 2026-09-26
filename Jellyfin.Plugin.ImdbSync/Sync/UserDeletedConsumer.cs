using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.ImdbSync.Storage;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSync.Sync;

/// <summary>
/// Deletes the stored IMDb cookie and sync data when a Jellyfin user is deleted.
/// </summary>
public class UserDeletedConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly UserSettingsStore _store;
    private readonly ILogger<UserDeletedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDeletedConsumer"/> class.
    /// </summary>
    /// <param name="store">The settings store.</param>
    /// <param name="logger">The logger.</param>
    public UserDeletedConsumer(UserSettingsStore store, ILogger<UserDeletedConsumer> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        _store.Delete(eventArgs.Argument.Id);
        _logger.LogInformation("Deleted IMDb sync data of removed user {UserId}", eventArgs.Argument.Id);
        return Task.CompletedTask;
    }
}
