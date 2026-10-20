using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Pins the names of the cn-324 bag requests in the shipped table.</summary>
public sealed class Cn324BagOpcodeTests
{
    private static OpcodeTable Load()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "profiles", "cn-324", "opcodes.yaml");
            if (File.Exists(candidate))
                return OpcodeTableLoader.Load(candidate, new ClientProfile("cn-324", "cn", 324, 0, 0, "cn-324"));
        }
        throw new InvalidOperationException("profiles/cn-324/opcodes.yaml was not found above the test output folder.");
    }

    [Fact]
    public void TheBagNamesEquipAndUnequipSoBothRequestsReachTheEquipmentHandler()
    {
        var table = Load();

        // Wear and removal are two requests of one u32 each (senders cn-324 sub_7FE040 and sub_7FE0D0).
        Assert.Equal((ushort)803, table.C2S("gcpReqInventoryEquip"));
        Assert.Equal((ushort)804, table.C2S("gcpReqInventoryUnequip"));
        Assert.Equal("gcpReqInventoryUnequip", table.Describe(PacketDirection.ClientToServer, 804).Split('(')[0]);
    }
}
