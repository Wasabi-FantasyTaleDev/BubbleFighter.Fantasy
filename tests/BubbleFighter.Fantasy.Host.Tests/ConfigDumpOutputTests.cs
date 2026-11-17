using System.Text;
using Xunit;

namespace BubbleFighter.Fantasy.Host.Tests;

/// <summary>Checks that a redirected configuration dump stays decodable YAML on any console code page.</summary>
public sealed class ConfigDumpOutputTests
{
    private const string Yaml = "nickname: 왕초보★투탑\n";

    /// <summary>Redirected output is strict UTF-8 without a byte order mark, so a reader decodes non-ASCII values.</summary>
    [Fact]
    public void RedirectedDumpIsStrictUtf8WithoutBom()
    {
        using var output = new MemoryStream();
        using var console = new StringWriter();

        ConfigDumpOutput.Write(Yaml, redirected: true, output, console);

        var bytes = output.ToArray();
        Assert.Equal(Yaml, new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Empty(console.ToString());
    }

    /// <summary>An interactive console keeps its own writer and encoding.</summary>
    [Fact]
    public void InteractiveDumpUsesTheConsoleWriter()
    {
        using var output = new MemoryStream();
        using var console = new StringWriter();

        ConfigDumpOutput.Write(Yaml, redirected: false, output, console);

        Assert.Equal(Yaml, console.ToString());
        Assert.Equal(0, output.Length);
    }
}
