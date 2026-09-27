namespace Jellyfin.Plugin.ImdbSync.Seerr;

/// <summary>
/// A movie or series as known by Seerr.
/// </summary>
/// <param name="MediaType"><c>movie</c> or <c>tv</c>.</param>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="Title">The title.</param>
/// <param name="MediaStatus">Seerr media status: 0/1 unknown, 2 pending, 3 processing, 4 partially available, 5 available.</param>
public sealed record SeerrTitle(string MediaType, int TmdbId, string Title, int MediaStatus)
{
    /// <summary>
    /// Gets a value indicating whether the title is already requested or (partially) available.
    /// </summary>
    public bool IsRequestedOrAvailable => MediaStatus >= 2;
}
