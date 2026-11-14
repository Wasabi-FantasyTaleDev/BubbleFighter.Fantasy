using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed class WebContentFilesTests
{
    private static WebContentFiles Sample() => new(new Dictionary<string, byte[]>
    {
        ["index.html"] = Encoding.UTF8.GetBytes("<!doctype html><title>x</title>"),
        ["assets/app-1a2b.js"] = Encoding.UTF8.GetBytes("export {}"),
        ["assets/app-1a2b.css"] = Encoding.UTF8.GetBytes("a{}"),
        ["favicon.ico"] = new byte[] { 0, 0, 1, 0 },
        ["data.bin"] = [1, 2, 3],
    });

    [Fact]
    public void IndexIsServedAsHtml()
    {
        Assert.True(Sample().TryGetFile("index.html", out var content, out var type));
        Assert.Equal("text/html; charset=utf-8", type);
        Assert.StartsWith("<!doctype html>", Encoding.UTF8.GetString(content.Span));
    }

    [Theory]
    [InlineData("assets/app-1a2b.js", "text/javascript; charset=utf-8")]
    [InlineData("assets/app-1a2b.css", "text/css; charset=utf-8")]
    [InlineData("favicon.ico", "image/x-icon")]
    [InlineData("data.bin", "application/octet-stream")]
    public void ContentTypeFollowsTheExtension(string path, string expected)
    {
        Assert.True(Sample().TryGetFile(path, out _, out var type));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/index.html")]
    [InlineData("../index.html")]
    [InlineData("assets/../index.html")]
    [InlineData("assets/./app-1a2b.js")]
    [InlineData("assets//app-1a2b.js")]
    [InlineData("assets\\app-1a2b.js")]
    [InlineData("C:/index.html")]
    [InlineData("index.html\0")]
    [InlineData("%2e%2e/index.html")]
    [InlineData("missing.js")]
    [InlineData("assets/")]
    public void UnsafeOrUnknownPathsAreNotFound(string path)
    {
        Assert.False(Sample().TryGetFile(path, out var content, out var type));
        Assert.True(content.IsEmpty);
        Assert.Equal("", type);
    }

    [Fact]
    public void ConstructionRejectsAKeyThatEscapesTheRoot()
    {
        Assert.Throws<ArgumentException>(() => new WebContentFiles(new Dictionary<string, byte[]> { ["../secret.txt"] = [1] }));
    }

    [Fact]
    public void EmbeddedClientIsServedFromTheAssembly()
    {
        var files = WebContentFiles.FromAssembly(typeof(WebUiPlugin).Assembly);
        Assert.True(files.TryGetFile("index.html", out var content, out var type));
        Assert.Equal("text/html; charset=utf-8", type);
        Assert.Contains("id=\"app\"", Encoding.UTF8.GetString(content.Span));
        Assert.True(files.Count >= 3);
    }

    [Fact]
    public void EmbeddedFilesAreServedInPlaceWithoutAManagedCopy()
    {
        var assembly = typeof(WebUiPlugin).Assembly;
        var files = WebContentFiles.FromAssembly(assembly);
        var checkedFiles = 0;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith("wwwroot/", StringComparison.Ordinal))
                continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            var expected = new byte[stream.Length];
            stream.ReadExactly(expected);

            Assert.True(files.TryGetFile(normalized["wwwroot/".Length..], out var content, out _));
            Assert.False(MemoryMarshal.TryGetArray(content, out _), $"{name} was copied to a managed array.");
            Assert.True(content.Span.SequenceEqual(expected), $"{name} differs from the embedded resource.");
            checkedFiles++;
        }
        Assert.Equal(files.Count, checkedFiles);
    }

    [Fact]
    public unsafe void EmbeddedFileMemoryCanBePinnedAtAnyOffset()
    {
        var files = WebContentFiles.FromAssembly(typeof(WebUiPlugin).Assembly);
        Assert.True(files.TryGetFile("index.html", out var content, out _));
        Assert.True(content.Length > 16);

        using (var start = content.Pin())
        using (var middle = content[7..].Pin())
        {
            Assert.Equal((nint)start.Pointer + 7, (nint)middle.Pointer);
            Assert.Equal(content.Span[7], *(byte*)middle.Pointer);
        }

        // A slice of a few bytes in the middle is what a partial write hands to the socket.
        Assert.True(content.Slice(7, 9).Span.SequenceEqual(content.ToArray().AsSpan(7, 9)));
        using var end = content[content.Length..].Pin();
        Assert.Equal(0, content[content.Length..].Length);
    }

    [Fact]
    public void AnAssemblyWithoutTheClientFailsLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() => WebContentFiles.FromAssembly(typeof(WebContentFilesTests).Assembly));
        Assert.Contains("npm", error.Message);
    }
}
