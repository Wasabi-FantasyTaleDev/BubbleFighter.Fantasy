using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the skill-slot and favourite-weapon-grade replies of <c>packets/skill-honor.yaml</c>: the
/// kr-1256 skill-slot body read by kr-1256 sub_1152CA0 (one word then nested counted slot maps) and the
/// favourite grade body read by kr-1256 sub_9D0E60 (24 bytes). A body that changes on purpose updates
/// its pin together with the client reader that decides it.
/// </summary>
public sealed class SkillHonorTablePins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The skill-slot reply retains both collection counts and all five words of each native record.</summary>
    [Fact]
    public void SkillSlotBodyPinsTheWordAndTheList()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "SkillSlotChanged", new Dictionary<string, object?>
        {
            ["unknown_0"] = 1,
            ["slots"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["unknown_0"] = 5,
                    ["entries"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["unknown_0"] = 6,
                            ["value"] = new Dictionary<string, object?>
                            {
                                ["unknown_4"] = 7, ["unknown_8"] = 8, ["unknown_12"] = 9,
                                ["unknown_16"] = 10, ["unknown_20"] = 11,
                            },
                        },
                    },
                },
            },
        });
        Assert.Equal(40, body.Length);
        Assert.Equal("01000000010000000500000001000000060000000700000008000000090000000A0000000B000000", Convert.ToHexString(body));

        var fields = PacketCodec.Read(Tables, "kr-1256", "SkillSlotChanged", body);
        Assert.Equal(2, fields.Count);
        Assert.Equal(body, PacketCodec.Write(Tables, "kr-1256", "SkillSlotChanged", fields));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, "kr-1256", "SkillSlotChanged", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, "kr-1256", "SkillSlotChanged", [.. body, 0]));
    }

    /// <summary>The favourite grade reply is a flag, an eight-byte value and three words.</summary>
    [Fact]
    public void FavoriteWeaponGradePinsTwentyFourBytes()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "FavoriteWeaponGrade", new Dictionary<string, object?>
        {
            ["flag"] = true,
            ["unknown_4"] = 0x1122334455667788L,
            ["item_id"] = 3,
            ["old_grade"] = 4,
            ["new_grade"] = 5,
        });
        Assert.Equal(24, body.Length);
        Assert.Equal("010000008877665544332211030000000400000005000000", Convert.ToHexString(body));

        var fields = PacketCodec.Read(Tables, "kr-1256", "FavoriteWeaponGrade", body);
        Assert.Equal(5, fields.Count);
        Assert.Equal(body, PacketCodec.Write(Tables, "kr-1256", "FavoriteWeaponGrade", fields));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, "kr-1256", "FavoriteWeaponGrade", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, "kr-1256", "FavoriteWeaponGrade", [.. body, 0]));
    }

    /// <summary>The binding writes the zero-flag grade branch with the discarded eight-byte word zero, and refuses a notice that changes nothing.</summary>
    [Fact]
    public void GradeNoticeEncodesTheZeroFlagBranch()
    {
        var body = FavoriteWeaponGradeBinding.Encode(Tables, "kr-1256", new(202020, 1, 2));
        Assert.Equal("00000000" + "0000000000000000" + Convert.ToHexString(BitConverter.GetBytes(202020)) + "01000000" + "02000000", Convert.ToHexString(body));
        Assert.Equal(24, body.Length);
        Assert.Throws<ArgumentException>(() => FavoriteWeaponGradeBinding.Encode(Tables, "kr-1256", new(202020, 2, 2)));
        Assert.Throws<ArgumentException>(() => FavoriteWeaponGradeBinding.Encode(Tables, "kr-1256", new(0, 1, 2)));
    }

    /// <summary>
    /// The structures are claimed for the kr-1256 family only: legacy bodies are not established, so a
    /// legacy build resolves them empty instead of inheriting a layout it never reads.
    /// </summary>
    [Fact]
    public void LegacyBuildsResolveTheStructuresEmpty()
    {
        Assert.Empty(Tables.Resolve("kr-812", "SkillSlotChanged"));
        Assert.Empty(Tables.Resolve("kr-812", "FavoriteWeaponGrade"));
    }
}
