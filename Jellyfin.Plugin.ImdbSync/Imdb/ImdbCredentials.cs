using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ImdbSync.Imdb;

/// <summary>
/// IMDb credentials extracted from a pasted cookie.
/// </summary>
/// <param name="Token">The at-main token (starts with <c>Atna|</c>).</param>
/// <param name="SessionId">The session-id cookie.</param>
/// <param name="WafToken">The optional aws-waf-token cookie.</param>
public sealed record ImdbCredentials(string Token, string SessionId, string WafToken)
{
    /// <summary>
    /// Parses what a user pasted: either a full cookie header (<c>session-id=...; at-main=Atna|...</c>)
    /// or only the <c>at-main</c> value.
    /// </summary>
    /// <param name="cookie">The pasted cookie.</param>
    /// <param name="sessionIdOverride">An explicit session id, used when the cookie has none.</param>
    /// <returns>The credentials, or <c>null</c> if no token could be found.</returns>
    public static ImdbCredentials? Parse(string? cookie, string? sessionIdOverride)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = cookie.Trim();
        if (trimmed.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }

        if (trimmed.Contains('=', StringComparison.Ordinal))
        {
            foreach (var part in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = part.IndexOf('=', StringComparison.Ordinal);
                if (idx > 0)
                {
                    values[part[..idx].Trim()] = Clean(part[(idx + 1)..]);
                }
            }
        }
        else
        {
            values["at-main"] = Clean(trimmed);
        }

        if (!values.TryGetValue("at-main", out var token) || !token.StartsWith("Atna|", StringComparison.Ordinal))
        {
            return null;
        }

        var sessionId = string.IsNullOrWhiteSpace(sessionIdOverride)
            ? values.GetValueOrDefault("session-id", string.Empty)
            : sessionIdOverride.Trim();

        return new ImdbCredentials(token, sessionId, values.GetValueOrDefault("aws-waf-token", string.Empty));
    }

    /// <summary>
    /// Returns a masked version of the token for display.
    /// </summary>
    /// <returns>The masked token.</returns>
    public string MaskedToken() => Token.Length <= 16 ? "Atna|…" : Token[..10] + "…" + Token[^4..];

    private static string Clean(string value)
    {
        value = value.Trim().Trim('"');
        return value.StartsWith("Atna%7C", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(value) : value;
    }
}
