namespace BubbleFighter.Fantasy.Contracts.Admin;

/// <summary>
/// Read-only set of static files the administration HTTP endpoint serves as its web interface.
/// A row that owns the files (the built web client) publishes this service; the admin endpoint
/// consumes it by interface type only, so it carries no HTTP framework types and the files can
/// come from embedded resources, a directory, or memory without the endpoint knowing which.
/// </summary>
public interface IAdminWebContent
{
    /// <summary>
    /// Looks up one file by its path relative to the content root, using <c>/</c> separators and
    /// no leading slash (for example <c>index.html</c> or <c>assets/index.js</c>). A path that
    /// is empty, escapes the root (<c>..</c>, a drive or a backslash) or names no file is not
    /// found; callers need no traversal checks of their own.
    /// </summary>
    /// <param name="relativePath">The path relative to the content root.</param>
    /// <param name="content">Receives the file bytes; the memory is shared and must not be modified.</param>
    /// <param name="contentType">Receives the media type to serve the file with.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    bool TryGetFile(string relativePath, out ReadOnlyMemory<byte> content, out string contentType);
}
