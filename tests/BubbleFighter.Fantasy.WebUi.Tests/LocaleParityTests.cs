using System.Text.RegularExpressions;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

/// <summary>
/// Every locale file of the web client must carry exactly the keys of the English one, with the
/// same <c>{placeholders}</c>. The files keep one entry per line precisely so this test can read
/// them without a JavaScript engine.
/// </summary>
public sealed partial class LocaleParityTests
{
    private static readonly string[] Locales = ["zh-Hant", "zh-Hans", "ko"];

    [GeneratedRegex(@"^\s*'(?<key>[^']+)':\s*(?<value>.*?),?\s*$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    private static string LocaleDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "Plugins", "BubbleFighter.Fantasy.WebUi", "client", "src", "locales");
    }

    private static Dictionary<string, string[]> Read(string locale)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(LocaleDirectory(), locale + ".js")))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("export default", StringComparison.Ordinal) || text == "}")
                continue;
            var match = EntryLine().Match(line);
            Assert.True(match.Success, $"{locale}.js: line is not a single 'key': 'value', entry: {line}");
            var key = match.Groups["key"].Value;
            Assert.True(result.TryAdd(key, Placeholder().Matches(match.Groups["value"].Value).Select(m => m.Groups[1].Value).Order().ToArray()),
                $"{locale}.js: duplicate key {key}");
        }
        return result;
    }

    [Fact]
    public void EnglishDictionaryIsNotEmpty()
    {
        Assert.True(Read("en").Count > 100);
    }

    [Theory]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans")]
    [InlineData("ko")]
    public void LocaleHasExactlyTheEnglishKeysAndPlaceholders(string locale)
    {
        var english = Read("en");
        var other = Read(locale);
        Assert.Empty(english.Keys.Except(other.Keys).Order());
        Assert.Empty(other.Keys.Except(english.Keys).Order());
        foreach (var (key, placeholders) in english)
            Assert.True(placeholders.SequenceEqual(other[key]), $"{locale}.js: placeholders of '{key}' differ from en.js");
    }

    [Fact]
    public void EveryLocaleFileIsCovered()
    {
        var files = Directory.GetFiles(LocaleDirectory(), "*.js").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Assert.Equal(new[] { "en" }.Concat(Locales).Order(), files);
    }
}
