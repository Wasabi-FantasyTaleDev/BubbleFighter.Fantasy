using System.Text;

namespace BubbleFighter.Fantasy.Host;

/// <summary>Writes the <c>--dump-config</c> YAML where its reader can decode it.</summary>
internal static class ConfigDumpOutput
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes redirected output as UTF-8 bytes, because it is YAML read by another program and the console's ANSI
    /// code page cannot encode values such as Korean AI preset nicknames; an interactive console keeps its own
    /// encoding so the text still displays.
    /// </summary>
    /// <param name="yaml">The composed row list.</param>
    /// <param name="redirected">Whether standard output is redirected to a file or another process.</param>
    /// <param name="output">Raw standard output, used when redirected.</param>
    /// <param name="console">The console writer, used when not redirected.</param>
    internal static void Write(string yaml, bool redirected, Stream output, TextWriter console)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (redirected)
        {
            ArgumentNullException.ThrowIfNull(output);
            output.Write(Utf8.GetBytes(yaml));
            output.Flush();
        }
        else
        {
            ArgumentNullException.ThrowIfNull(console);
            console.Write(yaml);
        }
    }
}
