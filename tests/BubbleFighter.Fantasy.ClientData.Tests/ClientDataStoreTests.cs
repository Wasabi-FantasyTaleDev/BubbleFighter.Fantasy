using System.Text;
using BubbleFighter.Fantasy.Archives;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.ClientData.Tests;

public sealed class ClientDataStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bff-clientdata-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Raw => Path.Combine(_root, "dataraw");

    private void Bfz(string relative, params (string Path, string Text)[] members)
    {
        var file = Path.Combine(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = File.Create(file);
        BfzArchive.Write(stream, relative, members.Select(m => (m.Path, Encoding.UTF8.GetBytes(m.Text))).ToList());
    }

    private void Pkn(string relative, params (string Path, string Text)[] members)
    {
        var file = Path.Combine(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = File.Create(file);
        PknWriter.Write(stream, relative.ToLowerInvariant(), members.Select(m => (m.Path, Encoding.UTF8.GetBytes(m.Text))).ToList(), true, true);
    }

    private ClientDataStore Open() => ClientDataStore.Open(Data, Raw, NullLogger.Instance);

    private static string Text(ClientDataStore store, string path) => Encoding.UTF8.GetString(store.Read(path));

    [Fact]
    public void BfzFileComesFromTheNearestFolderArchiveOfItsOwnName()
    {
        // kr-812 sub_A4B600: Maps/rel_X/a.xml tries Maps/rel_X/rel_X.bfz before Maps/Maps.bfz.
        Bfz("Maps/Maps.bfz", ("Maps/rel_X/a.xml", "outer"), ("Maps/list.xml", "list"));
        Bfz("Maps/rel_X/rel_X.bfz", ("Maps/rel_X/a.xml", "inner"));
        using var store = Open();
        Assert.Equal("inner", Text(store, "Maps/rel_X/a.xml"));
        Assert.Equal("list", Text(store, "maps/LIST.xml"));
        Assert.False(store.Exists("Maps/rel_X/missing.xml"));
        Assert.False(store.Exists("Config/ShopItem2.xml"));
    }

    [Fact]
    public void PknFileComesFromWhicheverPackHoldsIt()
    {
        Pkn("Packs/Config_00000.pkn", ("Config/A.xml", "a"));
        Pkn("Packs/Anything_00007.pkn", ("ServerShared/MapMeta.xml", "meta"));
        using var store = Open();
        Assert.Equal("meta", Text(store, "ServerShared/MapMeta.xml"));
        Assert.Equal("a", Text(store, "config/a.xml"));
    }

    [Fact]
    public void LooseFilesWinAndIgnoreLetterCase()
    {
        Bfz("Config/Config.bfz", ("Config/ShopItem2.xml", "archive"));
        Directory.CreateDirectory(Path.Combine(Raw, "Config"));
        File.WriteAllText(Path.Combine(Raw, "Config", "ShopItem2.xml"), "loose");
        Directory.CreateDirectory(Path.Combine(Raw, "NpcMode", "Scripts"));
        File.WriteAllText(Path.Combine(Raw, "NpcMode", "Scripts", "Boss.lua"), "lua");
        using var store = Open();
        Assert.Equal("loose", Text(store, "Config/ShopItem2.xml"));
        Assert.Equal("lua", Text(store, "npcmode/scripts/boss.LUA"));
        Assert.False(store.Exists("../outside.txt"));
        Assert.False(store.Exists("Config/../../outside.txt"));
    }

    [Fact]
    public void MissingDirectoriesFailLoud()
    {
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() => Open());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
