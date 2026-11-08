namespace BubbleFighter.Fantasy.Installation;

// Windows can briefly deny directory rename while another process holds an imported file without delete sharing.
internal sealed class InstallationDirectoryPublisher(Action<string, string> move, bool windows,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    internal static InstallationDirectoryPublisher SystemFileSystem { get; } =
        new(Directory.Move, OperatingSystem.IsWindows(), Task.Delay);

    internal async ValueTask MoveAsync(string source, string destination, InstallationOptions options, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                move(source, destination);
                return;
            }
            catch (IOException error) when (windows && attempt < options.ClientDataPublishAttempts &&
                error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                // Retry only ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION at publication.
                // A permanent denial still throws the original final exception; the upload itself is never replayed.
                await delay(TimeSpan.FromMilliseconds(options.ClientDataPublishRetryMs), ct).ConfigureAwait(false);
            }
        }
    }
}
