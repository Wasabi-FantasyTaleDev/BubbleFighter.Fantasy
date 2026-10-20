using System.Text;
using BubbleFighter.Fantasy.Archives;
using Xunit;

namespace BubbleFighter.Fantasy.Archives.Tests;

public sealed class ClientDataGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bff-generation-" + Guid.NewGuid().ToString("N"));

    private string Client => Path.Combine(_root, "client");
    private string Output => Path.Combine(_root, "output");

    [Theory]
    [InlineData("Packs/Config_00000.pkn", true)]
    [InlineData("Pack/Config0000.pkn", true)]
    [InlineData("Packs/old/Config_00000.pkn", false)]
    [InlineData("Pkg/Config_00000.pkn", false)]
    [InlineData("Config/Config.bfz", true)]
    [InlineData("Config/backup.bfz", false)]
    public void MountedPathsMatchTheClientLookup(string path, bool expected) =>
        Assert.Equal(expected, ClientDataGenerator.IsMounted(path));

    [Fact]
    public void CopiesDataArchivesButExtractsOnlySelectedMembersOfMixedArchives()
    {
        Pack("Packs/Config_00000.pkn", [("Config/ShopItem2.xml", "shop"), ("Config/Other.xml", "other")]);
        Pack("Packs/Maps_00000.pkn", [("Maps/Example.xml", "map"), ("Maps/Example.dds", "texture")]);
        new ClientDataGenerator().Generate("kr-812", Client, Output, 100, 1048576, default);
        Assert.True(File.Exists(Path.Combine(Output, "data/Packs/Config_00000.pkn")));
        Assert.Equal("map", File.ReadAllText(Path.Combine(Output, "dataraw/Maps/Example.xml")));
        Assert.False(File.Exists(Path.Combine(Output, "dataraw/Maps/Example.dds")));
        Assert.False(File.Exists(Path.Combine(Output, "data/Packs/Maps_00000.pkn")));
        Assert.Contains("sources.tsv", Directory.GetFiles(Output).Select(Path.GetFileName));
    }

    [Fact]
    public void RequiredFilesInMixedArchivesAreExtractedWithoutTheirTextures()
    {
        Pack("Packs/Mixed_00000.pkn", [("Config/ShopItem2.xml", "shop"), ("Texture/T.dds", "texture")]);
        new ClientDataGenerator().Generate("kr-812", Client, Output, 100, 1048576, default);
        Assert.Equal("shop", File.ReadAllText(Path.Combine(Output, "dataraw/Config/ShopItem2.xml")));
        Assert.False(Directory.Exists(Path.Combine(Output, "data")));
    }

    [Fact]
    public void MissingRequiredFilesFailBeforeWritingAndNameTheMissingPath()
    {
        Pack("Packs/Other_00000.pkn", [("Config/Other.xml", "other")]);
        var error = Assert.Throws<InvalidDataException>(() =>
            new ClientDataGenerator().Generate("kr-812", Client, Output, 100, 1048576, default));
        Assert.Contains("Config/ShopItem2.xml", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void GenerationDoesNotOverwriteExistingData()
    {
        Pack("Packs/Config_00000.pkn", [("Config/ShopItem2.xml", "shop")]);
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "keep"), "original");
        Assert.Throws<IOException>(() => new ClientDataGenerator().Generate("kr-812", Client, Output, 100, 1048576, default));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Output, "keep")));
    }

    [Fact]
    public void CancellationAndByteLimitsLeaveNoGeneratedFiles()
    {
        Pack("Packs/Config_00000.pkn", [("Config/ShopItem2.xml", "shop")]);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new ClientDataGenerator().Generate("kr-812", Client, Output, 100, 1048576, new CancellationToken(true)));
        Assert.Throws<InvalidDataException>(() =>
            new ClientDataGenerator().Generate("kr-812", Client, Output, 100, 1, default));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void ProvenanceBytesAreIncludedInTheLimitAndPartialFilesAreRemoved()
    {
        Pack("Packs/Config_00000.pkn", [("Config/ShopItem2.xml", "shop")]);
        var packLength = new FileInfo(Path.Combine(Client, "Packs/Config_00000.pkn")).Length;
        Assert.Throws<InvalidDataException>(() =>
            new ClientDataGenerator().Generate("kr-812", Client, Output, 100, packLength, default));
        Assert.Empty(Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CustomBuildUsesTheManifestDefaultsAndCannotBypassRequiredFiles()
    {
        Pack("Packs/Config_00000.pkn", [("Config/ShopItem2.xml", "shop")]);
        new ClientDataGenerator().Generate("custom-build", Client, Output, 100, 1048576, default);
        Assert.True(File.Exists(Path.Combine(Output, "data/Packs/Config_00000.pkn")));
    }

    [Fact]
    public void TraversingMemberPathsAreRejectedBeforeOutput()
    {
        Pack("Packs/Config_00000.pkn", [("Config/ShopItem2.xml", "shop"), ("../outside.xml", "bad")]);
        Assert.Throws<InvalidDataException>(() =>
            new ClientDataGenerator().Generate("kr-812", Client, Output, 100, 1048576, default));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void CommandLineUsesTheSharedGeneratorAndRejectsInvalidLimits()
    {
        Pack("Packs/Config_00000.pkn", [("Config/ShopItem2.xml", "shop")]);
        Assert.Equal(1, Tool.Program.Main(["generate", "kr-812", Client, Output, "--max-files", "invalid"]));
        Assert.False(Directory.Exists(Output));
        Assert.Equal(0, Tool.Program.Main(["generate", "kr-812", Client, Output, "--max-files", "100", "--max-bytes", "1048576"]));
        Assert.True(File.Exists(Path.Combine(Output, "data/Packs/Config_00000.pkn")));
    }

    private void Pack(string relative, (string Path, string Text)[] entries)
    {
        var path = Path.Combine(Client, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        PknWriter.Write(output, relative.ToLowerInvariant(), entries.Select(e => (e.Path, Encoding.UTF8.GetBytes(e.Text))).ToList());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
