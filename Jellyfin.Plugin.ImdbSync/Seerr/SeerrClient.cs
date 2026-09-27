using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ImdbSync.Seerr;

/// <summary>
/// Minimal client for Seerr (Jellyseerr / Overseerr compatible API).
/// </summary>
public class SeerrClient
{
    /// <summary>
    /// The name of the http client.
    /// </summary>
    public const string HttpClientName = "ImdbSyncSeerr";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    public SeerrClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets a value indicating whether Seerr is configured.
    /// </summary>
    public static bool IsConfigured
        => Plugin.Instance?.Configuration is { } c && !string.IsNullOrWhiteSpace(c.SeerrUrl) && !string.IsNullOrWhiteSpace(c.SeerrApiKey);

    /// <summary>
    /// Gets a value indicating whether missing watchlist titles should be requested.
    /// </summary>
    public static bool RequestsEnabled => IsConfigured && Plugin.Instance!.Configuration.SeerrRequestsEnabled;

    /// <summary>
    /// Gets the Seerr version.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The version string.</returns>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        var status = await SendAsync(HttpMethod.Get, "api/v1/status", null, null, cancellationToken).ConfigureAwait(false);
        return status?["version"]?.GetValue<string>() ?? "unknown";
    }

    /// <summary>
    /// Maps Jellyfin user ids to Seerr user ids.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Seerr user id by Jellyfin user id.</returns>
    public async Task<Dictionary<Guid, int>> GetUserMapAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, int>();
        for (var skip = 0; skip < 10000; skip += 100)
        {
            var page = await SendAsync(HttpMethod.Get, $"api/v1/user?take=100&skip={skip}", null, null, cancellationToken).ConfigureAwait(false);
            var results = page?["results"]?.AsArray() ?? [];
            foreach (var user in results)
            {
                if (Guid.TryParse(user?["jellyfinUserId"]?.GetValue<string>(), out var jellyfinId) && user?["id"] is { } id)
                {
                    map.TryAdd(jellyfinId, id.GetValue<int>());
                }
            }

            if (results.Count < 100)
            {
                break;
            }
        }

        return map;
    }

    /// <summary>
    /// Looks up a title by IMDb id.
    /// </summary>
    /// <param name="imdbId">The IMDb id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The title, or <c>null</c> if Seerr does not know it.</returns>
    public async Task<SeerrTitle?> FindByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get, "api/v1/search?query=" + Uri.EscapeDataString("imdb:" + imdbId), null, null, cancellationToken).ConfigureAwait(false);
        var item = (result?["results"]?.AsArray() ?? [])
            .FirstOrDefault(r => r?["mediaType"]?.GetValue<string>() is "movie" or "tv");
        if (item is null)
        {
            return null;
        }

        return new SeerrTitle(
            item["mediaType"]!.GetValue<string>(),
            item["id"]!.GetValue<int>(),
            item["title"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? imdbId,
            item["mediaInfo"]?["status"]?.GetValue<int>() ?? 0);
    }

    /// <summary>
    /// Requests a movie or all seasons of a series, on behalf of a Seerr user.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="seerrUserId">The Seerr user to request as.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RequestAsync(SeerrTitle title, int seerrUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        var body = new JsonObject { ["mediaType"] = title.MediaType, ["mediaId"] = title.TmdbId };
        if (title.MediaType == "tv")
        {
            var tv = await SendAsync(HttpMethod.Get, "api/v1/tv/" + title.TmdbId.ToString(CultureInfo.InvariantCulture), null, null, cancellationToken).ConfigureAwait(false);
            var seasons = new JsonArray();
            foreach (var season in tv?["seasons"]?.AsArray() ?? [])
            {
                var number = season?["seasonNumber"]?.GetValue<int>() ?? 0;
                if (number > 0)
                {
                    seasons.Add(number);
                }
            }

            body["seasons"] = seasons;
        }

        await SendAsync(HttpMethod.Post, "api/v1/request", body, seerrUserId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, int? asUser, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin not initialized.");
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Seerr is not configured.");
        }

        var baseUri = new Uri(config.SeerrUrl.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.TryAddWithoutValidation("X-Api-Key", config.SeerrApiKey.Trim());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (asUser is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Api-User", asUser.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = text;
            try
            {
                message = JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text;
            }
            catch (JsonException)
            {
            }

            throw new HttpRequestException($"Seerr returned {(int)response.StatusCode}: {(message.Length > 200 ? message[..200] : message)}", null, response.StatusCode);
        }

        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Seerr returned invalid JSON - is the URL correct?", ex);
        }
    }
}
