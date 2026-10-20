using BubbleFighter.Fantasy.WebUi;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

/// <summary>Checks that external tutorial packs expose only their fixed members within a bounded allocation.</summary>
public sealed class TutorialWebContentTests
{
    /// <summary>The default assembly contains complete narrator packs without an external resource directory.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("ko")]
    public void DefaultNarratorPackIncludesEveryStageAndResource(string language)
    {
        var content = WebContentFiles.FromAssembly(typeof(WebUiPlugin).Assembly);
        var root = $"tutorial/fantasy-setup/{language}/";
        foreach (var member in new[] { "manifest.json", "glyphs.json", "glyphs.png", "Images/TutorialStage/Dizni.png",
            "Images/TutorialStage/Dao.png", "Images/TutorialStage/ChatBalloonNew.png", "Sounds/ui-tut-type.wav", "Sounds/ui-tut-textbox.wav" })
        {
            Assert.True(content.TryGetFile(root + member, out var data, out _), root + member);
            Assert.False(data.IsEmpty);
        }
        Assert.True(content.TryGetFile(root + "manifest.json", out var manifest, out _));
        using var parsed = System.Text.Json.JsonDocument.Parse(manifest);
        var pages = parsed.RootElement.GetProperty("dialogues").EnumerateArray().ToArray();
        foreach (var stage in Enumerable.Range(1, 5))
            Assert.Contains(pages, page => page.GetProperty("stage").GetString() == "FantasySetup"
                && page.GetProperty("sourceProgress").GetInt32() == stage);
    }

    [Fact]
    public void ExtraFilesAreNeverPublishedAndBudgetIsEnforced()
    {
        var root = Path.Combine(Path.GetTempPath(), "bff-tutorial-test", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var language in new[] { "en", "zh-Hans", "zh-Hant", "ko" })
            foreach (var member in new[] { "manifest.json", "glyphs.json", "glyphs.png", "Images/TutorialStage/Dizni.png",
                "Images/TutorialStage/Dao.png", "Images/TutorialStage/ChatBalloonNew.png", "Sounds/ui-tut-type.wav", "Sounds/ui-tut-textbox.wav" })
            {
                var path = Path.Combine(root, language, member);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [1, 2, 3, 4]);
            }
            File.WriteAllText(Path.Combine(root, "not-public.txt"), "fixture");
            var embedded = new WebContentFiles(new Dictionary<string, byte[]> { ["index.html"] = [1] });
            var content = new TutorialWebContent(embedded, new WebUiOptions { TutorialDirectory = root });
            Assert.True(content.TryGetFile("tutorial/fantasy-setup/ko/glyphs.png", out var data, out var mime));
            Assert.Equal(4, data.Length);
            Assert.Equal("image/png", mime);
            Assert.False(content.TryGetFile("tutorial/fantasy-setup/not-public.txt", out _, out _));
            Assert.False(content.TryGetFile("tutorial/fantasy-setup/../not-public.txt", out _, out _));
            Assert.True(content.TryGetFile("index.html", out _, out _));
            Assert.Throws<IOException>(() => new TutorialWebContent(embedded,
                new WebUiOptions { TutorialDirectory = root, TutorialMaxBytes = 16 }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
