using BubbleFighter.Fantasy.Contracts.Admin;

namespace BubbleFighter.Fantasy.WebUi;

/// <summary>Loads only the fixed tutorial pack members; arbitrary operator files are never exposed over HTTP.</summary>
internal sealed class TutorialWebContent : IAdminWebContent
{
    private readonly IAdminWebContent _embedded;
    private readonly WebContentFiles _tutorial;

    internal TutorialWebContent(IAdminWebContent embedded, WebUiOptions options)
    {
        _embedded = embedded;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var language in new[] { "en", "zh-Hans", "zh-Hant", "ko" })
        foreach (var member in new[] { "manifest.json", "glyphs.json", "glyphs.png", "Images/TutorialStage/Dizni.png",
            "Images/TutorialStage/Dao.png", "Images/TutorialStage/ChatBalloonNew.png", "Sounds/ui-tut-type.wav", "Sounds/ui-tut-textbox.wav" })
        {
            var path = Path.GetFullPath(Path.Combine(options.TutorialDirectory, language, member));
            // Check every ancestor too: a linked Images directory would escape an otherwise ordinary pack directory.
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Tutorial resources cannot contain symbolic links.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > options.TutorialMaxBytes - total)
                throw new IOException("Tutorial resources exceed tutorial_max_bytes.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            total += bytes.Length;
            files.Add($"tutorial/fantasy-setup/{language}/{member}", bytes);
        }
        _tutorial = new WebContentFiles(files);
    }

    public bool TryGetFile(string relativePath, out ReadOnlyMemory<byte> content, out string contentType) =>
        _tutorial.TryGetFile(relativePath, out content, out contentType) || _embedded.TryGetFile(relativePath, out content, out contentType);
}
