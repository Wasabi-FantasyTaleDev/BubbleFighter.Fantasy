using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks bounded native stage counts without requiring racing object initialization.</summary>
public sealed class ExtremeRacingMapRoundDataTests
{
    /// <summary>Different tracks retain their own counts, including the client's missing-key fallback.</summary>
    [Theory]
    [InlineData("[SavePoint]\nStageCount=2\n", 2)]
    [InlineData("[SavePoint]\nStageCount=5\n", 5)]
    [InlineData("[SavePoint]\n", 5)]
    public void NativeStageCountIsReadPerMap(string text, int expected)
    {
        var store = new MapRoundStore(text);
        Assert.Equal(expected, ExtremeRacingMapData.ReadStageCount(store, "map", 4096));
        Assert.Equal(1, store.Reads);
    }

    /// <summary>Out-of-range, malformed and duplicated native counts fail instead of ending at an invented stage.</summary>
    [Theory]
    [InlineData("[SavePoint]\nStageCount=0\n")]
    [InlineData("[SavePoint]\nStageCount=6\n")]
    [InlineData("[SavePoint]\nStageCount=two\n")]
    [InlineData("[SavePoint]\nStageCount=2\nStageCount=5\n")]
    [InlineData("[SavePoint\nStageCount=2\n")]
    public void InvalidNativeCountsFail(string text) =>
        Assert.Throws<InvalidDataException>(() => ExtremeRacingMapData.ReadStageCount(new MapRoundStore(text), "map", 4096));

    /// <summary>Missing and oversized map options cannot silently use the configured fallback.</summary>
    [Fact]
    public void MissingOrOversizedResourcesFail()
    {
        Assert.Throws<FileNotFoundException>(() => ExtremeRacingMapData.ReadStageCount(new MapRoundStore("[SavePoint]"), "other", 4096));
        Assert.Throws<InvalidDataException>(() => ExtremeRacingMapData.ReadStageCount(new MapRoundStore("[SavePoint]\nStageCount=5"), "map", 8));
    }

    /// <summary>Unsafe map keys fail before any client data is read.</summary>
    [Theory]
    [InlineData("../map")]
    [InlineData("map/../other")]
    [InlineData("C:/map")]
    [InlineData("/map")]
    [InlineData("map\\other")]
    public void UnsafePathsFailBeforeRead(string stage)
    {
        var store = new MapRoundStore("[SavePoint]");
        Assert.Throws<InvalidDataException>(() => ExtremeRacingMapData.ReadStageCount(store, stage, 4096));
        Assert.Equal(0, store.Reads);
    }
}

internal sealed class MapRoundStore(string text) : IClientDataStore
{
    internal string Text { get; set; } = text;
    internal int Reads { get; private set; }
    /// <summary>Only the single configured test track is available.</summary>
    public bool Exists(string path) => path == "Maps/map/option.ini";
    /// <summary>Refuses unbounded reads so the tests require the resource limit.</summary>
    public byte[] Read(string path) => throw new InvalidOperationException("A bounded read is required.");
    /// <summary>Returns the mutable track options only within the caller's bound.</summary>
    public byte[] Read(string path, int maximumBytes)
    {
        Reads++;
        if (!Exists(path)) throw new FileNotFoundException(path);
        var bytes = Encoding.ASCII.GetBytes(Text);
        return bytes.Length <= maximumBytes ? bytes : throw new InvalidDataException("Map exceeds bound.");
    }
}
