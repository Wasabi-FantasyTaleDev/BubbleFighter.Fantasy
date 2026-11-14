using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins equipment reference transport without interpreting the signed correlation field as an identity limit.</summary>
public sealed class EquipmentArticleReferenceTests
{
    /// <summary>Supplies native eight-byte requests across the signed boundary and through the final unsigned value.</summary>
    public static TheoryData<string, string> References
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
                foreach (var body in new[] { "00E1F50500000000", "FFFFFFFFFFFFFF7F", "0000000000000080", "D997040000000080", "FFFFFFFFFFFFFFFF" })
                    data.Add(build, body);
            return data;
        }
    }

    /// <summary>Every reference retains its exact bits in the request and result echo, including values above long.MaxValue.</summary>
    [Theory]
    [MemberData(nameof(References))]
    public void ModernEquipmentRequestsAndRepliesPreserveAllReferenceBits(string build, string hex)
    {
        EquipmentRequestWire.RequireLayout(TestBuilds.Packets, build);
        var body = Convert.FromHexString(hex);
        var request = EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, body, true);
        Assert.Equal(BinaryPrimitives.ReadUInt64LittleEndian(body), unchecked((ulong)request.Reference));
        Assert.Null(request.ItemId);
        Assert.True(request.Worn);
        var reply = EquipmentRequestWire.WriteResult(TestBuilds.Packets, build, 0, request.Reference);
        Assert.Equal(12, reply.Length);
        Assert.Equal("00000000" + hex, Convert.ToHexString(reply));
    }

    /// <summary>An ordinary item reference remains eligible for item routing and removal correlation.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void OrdinaryItemReferenceKeepsItsResolvedItemId(string build)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(body, 301001);
        var request = EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, body, false);
        Assert.Equal(301001, request.Reference);
        Assert.Equal(301001, request.ItemId);
        Assert.False(request.Worn);
    }

    /// <summary>Partial references and trailing bytes cannot reach an equipment handler.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernReferencesRequireExactlyEightBytes(string build)
    {
        var body = Convert.FromHexString("FFFFFFFFFFFFFFFF");
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, body[..length], true));
        Assert.Throws<PacketFormatException>(() => EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, [.. body, 0], true));
    }
}
