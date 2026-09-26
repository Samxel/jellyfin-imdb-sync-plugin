using System;

namespace Jellyfin.Plugin.ImdbSync.Imdb;

/// <summary>
/// Thrown when IMDb rejects the user's cookie.
/// </summary>
public class ImdbAuthException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbAuthException"/> class.
    /// </summary>
    public ImdbAuthException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbAuthException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ImdbAuthException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbAuthException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ImdbAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
