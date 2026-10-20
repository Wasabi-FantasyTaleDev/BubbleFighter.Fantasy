using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Separates KR2015 matching from the native empty-body party departure.</summary>
public sealed class Kr812MatchingOpcodeTests
{
    [Fact]
    public void MatchingAndPartyDepartureUseDistinctPacketObjectsAndRoutes()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "profiles", "kr-812", "opcodes.yaml");
            if (!File.Exists(path))
                continue;
            var table = OpcodeTableLoader.Load(path, new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812"));
            // kr-812 sub_5B3640 uses the match vtable; kr-812 sub_677300 uses the party vtable.
            Assert.Equal((ushort)112, table.C2S("gcpReqMatch"));
            Assert.Equal((ushort)202, table.C2S("gcpReqLeaveParty"));
            Assert.True(table.TryGet(PacketDirection.ClientToServer, 112, out var matching));
            Assert.Equal(Evidence.Verified, matching!.Evidence);
            Assert.True(table.TryGet(PacketDirection.ClientToServer, 202, out var departure));
            Assert.Equal(Evidence.Verified, departure!.Evidence);
            return;
        }
        throw new InvalidOperationException("The repository KR2015 opcode table was not found.");
    }
}
