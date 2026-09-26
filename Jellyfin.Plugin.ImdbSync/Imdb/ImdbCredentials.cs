using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ImdbSync.Imdb;

/// <summary>
/// IMDb credentials extracted from a pasted cookie.
/// </summary>
/// <param name="Token">The at-main token (e.g. <c>Atza|…</c> or <c>Atna|…</c>).</param>
/// <param name="SessionId">The session-id cookie.</param>
/// <param name="CookieHeader">The cookie header sent to IMDb.</param>
public sealed partial record ImdbCredentials(string Token, string SessionId, string CookieHeader)
{
    /// <summary>
    /// The maximum accepted length of a pasted cookie.
    /// </summary>
    public const int MaxCookieLength = 16 * 1024;

    /// <summary>
    /// Parses what a user pasted: either a full cookie header (<c>session-id=...; at-main=Atza|...</c>)
    /// or only the <c>at-main</c> value.
    /// </summary>
    /// <param name="cookie">The pasted cookie.</param>
    /// <param name="sessionIdOverride">An explicit session id, used when the cookie has none.</param>
    /// <returns>The credentials, or <c>null</c> if no token could be found.</returns>
    public static ImdbCredentials? Parse(string? cookie, string? sessionIdOverride)
    {
        // Control characters (e.g. CR/LF) would allow header injection.
        if (string.IsNullOrWhiteSpace(cookie)
            || cookie.Length > MaxCookieLength
            || HasControlChars(cookie.Trim())
            || (sessionIdOverride is not null && (sessionIdOverride.Length > 100 || HasControlChars(sessionIdOverride.Trim()) || sessionIdOverride.Contains(';', StringComparison.Ordinal))))
        {
            return null;
        }

        // Keeps the original order; later duplicates win.
        var values = new List<KeyValuePair<string, string>>();
        var trimmed = cookie.Trim();
        if (trimmed.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }

        if (trimmed.Contains('=', StringComparison.Ordinal) && !TokenRegex().IsMatch(trimmed))
        {
            foreach (var part in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = part.IndexOf('=', StringComparison.Ordinal);
                if (idx > 0)
                {
                    Set(values, part[..idx].Trim(), Clean(part[(idx + 1)..]));
                }
            }
        }
        else
        {
            Set(values, "at-main", Clean(trimmed));
        }

        var token = Get(values, "at-main");
        if (token is null || !TokenRegex().IsMatch(token))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(sessionIdOverride))
        {
            Set(values, "session-id", sessionIdOverride.Trim());
        }

        var header = string.Join("; ", values.Select(kv => kv.Key + "=" + kv.Value));
        return new ImdbCredentials(token, Get(values, "session-id") ?? string.Empty, header);
    }

    /// <summary>
    /// Returns a masked version of the token for display.
    /// </summary>
    /// <returns>The masked token.</returns>
    public string MaskedToken() => Token.Length <= 16 ? Token[..5] + "…" : Token[..5] + "…" + Token[^4..];

    /// <summary>
    /// Returns a string that does not contain any secret.
    /// </summary>
    /// <returns>The masked credentials.</returns>
    public override string ToString() => MaskedToken();

    [GeneratedRegex(@"^At[A-Za-z]{2}\|\S+$")]
    private static partial Regex TokenRegex();

    private static bool HasControlChars(string value) => value.Any(char.IsControl);

    private static string? Get(List<KeyValuePair<string, string>> values, string key)
        => values.LastOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Set(List<KeyValuePair<string, string>> values, string key, string value)
    {
        values.RemoveAll(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        values.Add(new KeyValuePair<string, string>(key, value));
    }

    private static string Clean(string value)
    {
        value = value.Trim().Trim('"');
        return value.StartsWith("At", StringComparison.Ordinal) && value.Contains("%7C", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(value)
            : value;
    }
}
