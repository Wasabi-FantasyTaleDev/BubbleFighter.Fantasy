using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Diagnostics;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

public sealed class PacketCatalogNamesTests
{
    [Fact]
    public void CatalogHintsOnlyChangeLogPresentationAndRespectConnectionSpaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "bff-log-catalog-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "kr-1256");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "opcode-catalog.yaml"), """
                profile: kr-1256
                opcodes:
                  - {value: 55, direction: c2s, name: gcpProposed, grade: pair}
                  - {value: 56, direction: c2s, name: gcpWrong, grade: label}
                  - {value: 57, direction: c2s, name: rcpUnscoped, grade: label}
                  - {value: 58, direction: c2s, name: gcpFirst, grade: strong}
                  - {value: 58, direction: c2s, name: gcpSecond, grade: strong}
                  - {value: 59, direction: c2s, connection: relay, name: rcpScoped, grade: label}
                """);
            var names = PacketCatalogNames.Load(root, 20, 8192);
            var table = new OpcodeTable(new ClientProfile("kr-1256", "kr", 1256, 0, 0, "Test"),
            [new OpcodeEntry(55, PacketDirection.ClientToServer, "Unknown55", Evidence.Unverified, null, null),
             new OpcodeEntry(56, PacketDirection.ClientToServer, "gcpReviewed", Evidence.Verified, null, null)]);
            Assert.Equal("gcpProposed?(55)", names.Describe("kr-1256/game", PacketDirection.ClientToServer, 55, table));
            Assert.Equal("gcpReviewed(56)", names.Describe("kr-1256/game", PacketDirection.ClientToServer, 56, table));
            Assert.Equal("Unknown(55)", names.Describe("kr-1256/game", PacketDirection.ServerToClient, 55, table));
            Assert.Equal("Unknown(57)", names.Describe("kr-1256/game", PacketDirection.ClientToServer, 57, table));
            Assert.Equal("Unknown(58)", names.Describe("kr-1256/game", PacketDirection.ClientToServer, 58, table));
            Assert.Equal("rcpScoped?(59)", names.Describe("kr-1256/relay", PacketDirection.ClientToServer, 59, null));
            Assert.Equal("Unknown(59)", names.Describe("kr-1256/game", PacketDirection.ClientToServer, 59, table));
            Assert.Equal("Unknown55(55)", table.Describe(PacketDirection.ClientToServer, 55));
            Assert.Null(table.Find(PacketDirection.ClientToServer, "gcpProposed"));
            Assert.Equal(2, table.All.Count());
            Assert.Throws<ConfigException>(() => PacketCatalogNames.Load(root, 1, 8192));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
