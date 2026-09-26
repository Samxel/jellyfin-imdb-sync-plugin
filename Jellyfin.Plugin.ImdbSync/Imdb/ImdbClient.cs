using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ImdbSync.Imdb;

/// <summary>
/// Minimal client for the IMDb GraphQL API, authenticated with a user's at-main cookie.
/// </summary>
public class ImdbClient
{
    /// <summary>
    /// The name of the http client.
    /// </summary>
    public const string HttpClientName = "ImdbSync";

    private const string SeenQuery =
        "mutation UserAddSeenTitleMutation($titleId: ID!) { "
        + "addWatchedTitle(titleId: $titleId) { success message { __typename ...LocalizedStringFragment } } } "
        + "fragment LocalizedStringFragment on LocalizedString { language value }";

    private const string SeenHash = "f71ce6ce7e20e914cbc00e744a77cb3d14611c82477834589106a5d57a886522";

    private const string WatchlistCountQuery =
        "query UserPredefinedListQuery { predefinedList(classType: WATCH_LIST) { items(first: 1) { "
        + "edges { node { item { __typename ... on Title { id } } } } pageInfo { hasNextPage } } } }";

    private static readonly Uri _graphQlUri = new("https://api.graphql.imdb.com/");

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    public ImdbClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Marks a title as watched on IMDb.
    /// </summary>
    /// <param name="credentials">The user's credentials.</param>
    /// <param name="imdbId">The IMDb id (tt...).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if IMDb reported success.</returns>
    public async Task<bool> MarkWatchedAsync(ImdbCredentials credentials, string imdbId, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["operationName"] = "UserAddSeenTitleMutation",
            ["variables"] = new JsonObject { ["titleId"] = imdbId },
            ["query"] = SeenQuery,
            ["extensions"] = new JsonObject
            {
                ["persistedQuery"] = new JsonObject { ["version"] = 1, ["sha256Hash"] = SeenHash }
            }
        };

        var data = await PostAsync(credentials, payload, cancellationToken).ConfigureAwait(false);
        return data?["addWatchedTitle"]?["success"]?.GetValue<bool>() ?? false;
    }

    /// <summary>
    /// Verifies the credentials with a read-only request.
    /// </summary>
    /// <param name="credentials">The user's credentials.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task TestAsync(ImdbCredentials credentials, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["operationName"] = "UserPredefinedListQuery",
            ["variables"] = new JsonObject(),
            ["query"] = WatchlistCountQuery
        };

        var data = await PostAsync(credentials, payload, cancellationToken).ConfigureAwait(false);
        if (data?["predefinedList"] is null)
        {
            throw new ImdbAuthException("IMDb returned no watchlist - the cookie is probably not logged in.");
        }
    }

    private async Task<JsonNode?> PostAsync(ImdbCredentials credentials, JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _graphQlUri);
        request.Content = JsonContent.Create(payload);
        request.Headers.TryAddWithoutValidation("x-imdb-map-authentication-token", credentials.Token);
        if (!string.IsNullOrEmpty(credentials.SessionId))
        {
            request.Headers.TryAddWithoutValidation("x-amzn-sessionid", credentials.SessionId);
        }

        request.Headers.TryAddWithoutValidation("x-imdb-client-name", "imdb-app-android");
        request.Headers.TryAddWithoutValidation("x-imdb-client-version", "9.3.4.109340300");
        request.Headers.TryAddWithoutValidation("x-imdb-user-language", "en-US");
        request.Headers.TryAddWithoutValidation("x-imdb-user-country", "US");
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("user-agent", "IMDb/9.3.4 (google|sdk_gphone64_x86_64; Android 34; google)");
        if (!string.IsNullOrEmpty(credentials.WafToken))
        {
            request.Headers.TryAddWithoutValidation("cookie", "aws-waf-token=" + credentials.WafToken);
        }

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ImdbAuthException("IMDb rejected the cookie (401) - it has expired or is invalid.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"IMDb returned {(int)response.StatusCode}: {Truncate(body)}", null, response.StatusCode);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("IMDb returned invalid JSON: " + Truncate(body), ex);
        }

        // IMDb answers 200 even for bad tokens and reports the token state here (e.g. "token.invalid").
        var authState = root?["extensions"]?["authToken"]?.GetValue<string>();
        if (authState is not null
            && (authState.Contains("invalid", StringComparison.OrdinalIgnoreCase) || authState.Contains("expired", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ImdbAuthException($"IMDb rejected the cookie ({authState}) - it has expired or is invalid.");
        }

        if (root?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var text = errors.ToJsonString();
            if (text.Contains("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                throw new ImdbAuthException("IMDb: " + Truncate(text));
            }

            throw new HttpRequestException("IMDb GraphQL error: " + Truncate(text));
        }

        return root?["data"];
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
