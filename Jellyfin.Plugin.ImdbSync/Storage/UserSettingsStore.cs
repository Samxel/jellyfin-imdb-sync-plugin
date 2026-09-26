using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSync.Storage;

/// <summary>
/// Stores per-user settings as JSON files. They are kept out of the plugin XML
/// configuration so the IMDb cookies are never returned by the generic plugin configuration API.
/// </summary>
public class UserSettingsStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly ILogger<UserSettingsStore> _logger;
    private readonly ConcurrentDictionary<Guid, ImdbUserSettings> _cache = new();
    private readonly object _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSettingsStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public UserSettingsStore(IApplicationPaths applicationPaths, ILogger<UserSettingsStore> logger)
    {
        _directory = Path.Combine(applicationPaths.PluginConfigurationsPath, "ImdbSync", "users");
        _logger = logger;
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
        {
            // The files contain IMDb session cookies: readable by the Jellyfin user only.
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var file in Directory.EnumerateFiles(_directory))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    /// <summary>
    /// Gets the ids of all users that have a settings file.
    /// </summary>
    /// <returns>The user ids.</returns>
    public IReadOnlyList<Guid> GetUserIds()
    {
        return Directory.EnumerateFiles(_directory, "*.json")
            .Select(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out var id) ? id : Guid.Empty)
            .Where(id => !id.Equals(Guid.Empty))
            .ToList();
    }

    /// <summary>
    /// Gets a copy of the settings of a user. Returns defaults if none are stored.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The settings.</returns>
    public ImdbUserSettings Get(Guid userId)
    {
        return Clone(_cache.GetOrAdd(userId, Load));
    }

    /// <summary>
    /// Atomically modifies and persists the settings of a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="update">The modification.</param>
    /// <returns>A copy of the updated settings.</returns>
    public ImdbUserSettings Update(Guid userId, Action<ImdbUserSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_writeLock)
        {
            var settings = Clone(_cache.GetOrAdd(userId, Load));
            update(settings);
            var path = GetPath(userId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, _jsonOptions));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tmp, path, true);
            _cache[userId] = settings;
            return Clone(settings);
        }
    }

    /// <summary>
    /// Deletes all stored settings of a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    public void Delete(Guid userId)
    {
        lock (_writeLock)
        {
            _cache.TryRemove(userId, out _);
            var path = GetPath(userId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static ImdbUserSettings Clone(ImdbUserSettings settings)
    {
        var copy = JsonSerializer.Deserialize<ImdbUserSettings>(JsonSerializer.Serialize(settings))!;
        Normalize(copy);
        return copy;
    }

    private static void Normalize(ImdbUserSettings settings)
    {
        settings.SyncedIds = new HashSet<string>(settings.SyncedIds ?? [], StringComparer.OrdinalIgnoreCase);
        settings.FailedIds = new HashSet<string>(settings.FailedIds ?? [], StringComparer.OrdinalIgnoreCase);
        settings.Log ??= [];
    }

    private string GetPath(Guid userId) => Path.Combine(_directory, userId.ToString("N") + ".json");

    private ImdbUserSettings Load(Guid userId)
    {
        var path = GetPath(userId);
        if (!File.Exists(path))
        {
            return new ImdbUserSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<ImdbUserSettings>(File.ReadAllText(path)) ?? new ImdbUserSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "Could not read IMDb sync settings from {Path}", path);
            return new ImdbUserSettings();
        }
    }
}
