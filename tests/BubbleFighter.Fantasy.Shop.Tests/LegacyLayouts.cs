using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Reads a build's catalog layout from the repository's packets/builds.yaml, as the dialect row does.</summary>
internal static class LegacyLayouts
{
    private static readonly Lazy<PacketTables> Tables = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return PacketTables.Load(Path.Combine(dir.FullName, "packets"));
        throw new InvalidOperationException("Repository root not found above the test output.");
    });

    /// <summary>Gets the catalog layout of one build.</summary>
    public static EnterShopReplyLayout Of(string build) =>
        ShopCatalogWire.LayoutOf(build, Tables.Value.ValuesOf(build));
}
