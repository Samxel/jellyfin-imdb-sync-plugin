using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ImdbSync.Imdb;
using Jellyfin.Plugin.ImdbSync.Storage;
using Jellyfin.Plugin.ImdbSync.Sync;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSync.Api;

/// <summary>
/// Endpoints for users to manage their own IMDb sync, plus an admin overview.
/// </summary>
[ApiController]
[Route("ImdbSync")]
[Authorize]
public class ImdbSyncController : ControllerBase
{
    private readonly UserSettingsStore _store;
    private readonly ImdbSyncService _syncService;
    private readonly ImdbClient _imdbClient;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authContext;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ImdbSyncController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbSyncController"/> class.
    /// </summary>
    /// <param name="store">The settings store.</param>
    /// <param name="syncService">The sync service.</param>
    /// <param name="imdbClient">The IMDb client.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="authContext">The authorization context.</param>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="logger">The logger.</param>
    public ImdbSyncController(
        UserSettingsStore store,
        ImdbSyncService syncService,
        ImdbClient imdbClient,
        IUserManager userManager,
        IAuthorizationContext authContext,
        IHostApplicationLifetime lifetime,
        ILogger<ImdbSyncController> logger)
    {
        _store = store;
        _syncService = syncService;
        _imdbClient = imdbClient;
        _userManager = userManager;
        _authContext = authContext;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Serves the self-service settings page for all users.
    /// </summary>
    /// <returns>The HTML page.</returns>
    [HttpGet("Page")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public ActionResult GetPage()
    {
        var stream = typeof(ImdbSyncController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.ImdbSync.Web.userPage.html");
        if (stream is null)
        {
            return NotFound();
        }

        // The page runs on the Jellyfin origin and reads the Jellyfin access token: lock it down.
        var headers = Response.Headers;
        headers["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; "
            + "connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'self'";
        headers["X-Frame-Options"] = "SAMEORIGIN";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
        return File(stream, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Gets the IMDb sync status of the current user.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Me")]
    public async Task<ActionResult<UserStatusDto>> GetMe()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        return userId is null ? BadRequest("This endpoint needs a user session, not an API key.") : ToDto(userId.Value);
    }

    /// <summary>
    /// Updates the IMDb sync settings of the current user.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <returns>The new status.</returns>
    [HttpPost("Me")]
    public async Task<ActionResult<UserStatusDto>> UpdateMe([FromBody] UpdateSettingsDto update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId is null)
        {
            return BadRequest("This endpoint needs a user session, not an API key.");
        }

        if (update.Cookie?.Length > ImdbCredentials.MaxCookieLength || update.SessionId?.Length > 100)
        {
            return BadRequest("Cookie or session-id is too long.");
        }

        if (!string.IsNullOrWhiteSpace(update.SessionId) && ImdbCredentials.Parse("at-main=Atza|x", update.SessionId) is null)
        {
            return BadRequest("Invalid session-id.");
        }

        if (!string.IsNullOrWhiteSpace(update.Cookie) && ImdbCredentials.Parse(update.Cookie, update.SessionId) is null)
        {
            return BadRequest("No IMDb token found. Paste the whole cookie (it must contain at-main=Atza|... or at-main=Atna|...) or just the at-main value.");
        }

        _store.Update(userId.Value, s =>
        {
            if (!string.IsNullOrWhiteSpace(update.Cookie))
            {
                s.Cookie = update.Cookie.Trim();
                s.CookieExpired = false;
                s.LastError = null;
            }

            s.SessionId = update.SessionId?.Trim() ?? s.SessionId;
            s.Enabled = update.Enabled ?? s.Enabled;
            s.SyncMovies = update.SyncMovies ?? s.SyncMovies;
            s.SyncEpisodes = update.SyncEpisodes ?? s.SyncEpisodes;
        });

        return ToDto(userId.Value);
    }

    /// <summary>
    /// Deletes all IMDb sync data (including the cookie) of the current user.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Me")]
    public async Task<ActionResult> DeleteMe()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId is null)
        {
            return BadRequest();
        }

        _store.Delete(userId.Value);
        return NoContent();
    }

    /// <summary>
    /// Checks whether IMDb accepts the stored cookie.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    [HttpPost("Me/Test")]
    public async Task<ActionResult<string>> TestMe(CancellationToken cancellationToken)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId is null)
        {
            return BadRequest();
        }

        var settings = _store.Get(userId.Value);
        var credentials = ImdbCredentials.Parse(settings.Cookie, settings.SessionId);
        if (credentials is null)
        {
            return BadRequest("No IMDb cookie saved.");
        }

        try
        {
            await _imdbClient.TestAsync(credentials, cancellationToken).ConfigureAwait(false);
            _store.Update(userId.Value, s =>
            {
                s.CookieExpired = false;
                s.LastError = null;
            });
            return Ok("IMDb accepted the cookie.");
        }
        catch (ImdbAuthException ex)
        {
            _store.Update(userId.Value, s =>
            {
                s.CookieExpired = true;
                s.LastError = ex.Message;
            });
            return UnprocessableEntity(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>
    /// Starts a full sync of the current user's watch history in the background.
    /// </summary>
    /// <returns>Accepted.</returns>
    [HttpPost("Me/Sync")]
    public async Task<ActionResult> SyncMe()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId is null)
        {
            return BadRequest();
        }

        var id = userId.Value;
        if (_syncService.IsSyncing(id))
        {
            return Conflict("A sync is already running.");
        }

        var stopping = _lifetime.ApplicationStopping;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = await _syncService.SyncUserAsync(id, SyncSource.Manual, null, stopping).ConfigureAwait(false);
                    _logger.LogInformation("Manual IMDb sync for user {UserId}: {Message}", id, result.Message);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Manual IMDb sync for user {UserId} failed", id);
                }
            },
            stopping);

        return Accepted();
    }

    /// <summary>
    /// Gets the activity log of the current user, newest first.
    /// </summary>
    /// <param name="limit">The maximum number of entries.</param>
    /// <returns>The log entries.</returns>
    [HttpGet("Me/Log")]
    public async Task<ActionResult<IEnumerable<SyncLogEntry>>> GetMyLog([FromQuery] int limit = ImdbUserSettings.MaxLogEntries)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId is null)
        {
            return BadRequest();
        }

        var log = _store.Get(userId.Value).Log;
        return log.AsEnumerable().Reverse().Take(Math.Clamp(limit, 1, ImdbUserSettings.MaxLogEntries)).ToList();
    }

    /// <summary>
    /// Clears the activity log of the current user.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Me/Log")]
    public async Task<ActionResult> ClearMyLog()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId is null)
        {
            return BadRequest();
        }

        _store.Update(userId.Value, s => s.Log.Clear());
        return NoContent();
    }

    /// <summary>
    /// Forgets which titles were already pushed, so the next sync sends everything again.
    /// </summary>
    /// <returns>The new status.</returns>
    [HttpPost("Me/Reset")]
    public async Task<ActionResult<UserStatusDto>> ResetMe()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId is null)
        {
            return BadRequest();
        }

        _store.Update(userId.Value, s =>
        {
            s.SyncedIds.Clear();
            s.FailedIds.Clear();
        });
        return ToDto(userId.Value);
    }

    /// <summary>
    /// Admin overview of all users. Never returns cookies.
    /// </summary>
    /// <returns>The statuses.</returns>
    [HttpGet("Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IEnumerable<UserStatusDto>> GetUsers()
    {
        return _userManager.GetUsers()
            .Select(u => ToDto(u.Id, countPending: false))
            .OrderBy(d => d.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Guid?> GetUserIdAsync()
    {
        var info = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return info.IsApiKey || info.UserId.Equals(Guid.Empty) ? null : info.UserId;
    }

    private UserStatusDto ToDto(Guid userId, bool countPending = true)
    {
        var s = _store.Get(userId);
        var credentials = ImdbCredentials.Parse(s.Cookie, s.SessionId);
        int pending = 0;
        if (countPending && credentials is not null)
        {
            pending = _syncService.CountPending(userId);
        }

        return new UserStatusDto
        {
            UserId = userId,
            UserName = _userManager.GetUserById(userId)?.Username ?? string.Empty,
            Enabled = s.Enabled,
            HasCookie = credentials is not null,
            MaskedToken = credentials?.MaskedToken(),
            HasSessionId = !string.IsNullOrEmpty(credentials?.SessionId),
            CookieExpired = s.CookieExpired,
            SyncMovies = s.SyncMovies,
            SyncEpisodes = s.SyncEpisodes,
            SyncedCount = s.SyncedIds.Count,
            FailedCount = s.FailedIds.Count,
            PendingCount = pending,
            IsSyncing = _syncService.IsSyncing(userId),
            Progress = _syncService.GetProgress(userId),
            LastSyncUtc = s.LastSyncUtc,
            LastSuccessUtc = s.LastSuccessUtc,
            LastSyncedTitle = s.LastSyncedTitle,
            LastError = s.LastError
        };
    }
}
