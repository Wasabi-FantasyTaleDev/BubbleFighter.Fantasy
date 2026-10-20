using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>The legacy login pair: kr-812 and id-400 pinned against their readers and serializers.</summary>
public sealed class LoginMessageCodecTests
{
    // part01 s2c#2: gspRepLogin, 111 bytes.
    private static readonly byte[] RecordedReply = Convert.FromHexString(
        "000000000C000000740065007300740070006C00610079006500720030003100000000000C00" +
        "0000540065007300740050006C006100790065007200300031001A0100000000000000000000" +
        "000000002B7846827A9AE640050000004C004F00430041004C00000000000000000000");

    [Fact]
    public void AcceptedReplyKeepsTheRecordedLayoutWithUserNumberAndCharacterTypeFromTheClientReader()
    {
        var time = DateTime.FromOADate(BitConverter.ToDouble(Convert.FromHexString("2B7846827A9AE640")));
        var body = LoginMessageCodec.WriteAccepted(new LoginAccepted(0x11A, "testplayer01", "TestPlayer01", time, 300010), TestLogin.Of("kr-812"), TestBuilds.Packets, "kr-812");
        Assert.Equal(111, body.Length);
        // The body puts 0 after the account and the user number after the nickname; the
        // handler sub_818680 reads the user number from the first and the character type
        // from the second.
        var expected = (byte[])RecordedReply.Clone();
        BitConverter.GetBytes(0x11A).CopyTo(expected, 32);
        BitConverter.GetBytes(10).CopyTo(expected, 64);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void RejectedReplyKeepsTheReaderShapeWithResultOne()
    {
        var body = LoginMessageCodec.WriteRejected(new LoginRejected(LoginRejection.AccountNotFound), TestLogin.Of("kr-812"), TestBuilds.Packets, "kr-812");
        Assert.Equal(1, BitConverter.ToInt32(body));
        Assert.Equal(4 + 4 + 4 + 4 + 16 + 8 + 4 + 4 + 1 + 4, body.Length);
    }

    [Fact]
    public void RequestReadsTheRecordedEmptyAccountAndThePaddedPassport()
    {
        // 40 bytes: empty strings and four adapter addresses.
        // Adapter values use documentation ranges; the count, widths and trailing bytes stay pinned.
        var recorded = Convert.FromHexString(
            "000000000000000000000000" + "04000000C0000201C6336402CB007103C0000204" + "0000000000000000");
        Assert.Equal(40, recorded.Length);
        Assert.Equal("", LoginMessageCodec.ReadRequest(recorded, TestLogin.Of("kr-812")).Account);

        var padded = new List<byte>();
        padded.AddRange(BitConverter.GetBytes(1024));
        var chars = new char[1024];
        "test812".CopyTo(chars);
        padded.AddRange(System.Text.Encoding.Unicode.GetBytes(chars));
        padded.AddRange(new byte[4 + 4 + 4 + 4 + 4]);
        Assert.Equal("test812", LoginMessageCodec.ReadRequest(padded.ToArray(), TestLogin.Of("kr-812")).Account);
    }

    [Fact]
    public void RequestsOfAnotherShapeAreRejected()
    {
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(new byte[39], TestLogin.Of("kr-812")));
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(new byte[41], TestLogin.Of("kr-812")));
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(BitConverter.GetBytes(1025), TestLogin.Of("kr-812")));
    }

    /// <summary>
    /// The kr-1256 launcher body (kr-1256 sub_10C6A00): three wstr, the counted adapter list, two words and one
    /// trailing wstr. It is consumed exactly; a trailing byte, a truncation or an implausible list count is refused.
    /// </summary>
    [Fact]
    public void Kr1256LauncherBodyIsConsumedExactlyAndMalformedBodiesAreRefused()
    {
        static byte[] Body(string account, int[] adapters, bool trailingByte = false)
        {
            var body = new List<byte>();
            void Wstr(string text)
            {
                body.AddRange(BitConverter.GetBytes(text.Length));
                body.AddRange(System.Text.Encoding.Unicode.GetBytes(text));
            }
            Wstr(account);
            Wstr("");
            Wstr("x");
            body.AddRange(BitConverter.GetBytes(adapters.Length));
            foreach (var adapter in adapters) body.AddRange(BitConverter.GetBytes(adapter));
            body.AddRange(BitConverter.GetBytes(7));
            body.AddRange(BitConverter.GetBytes(8));
            Wstr("z");
            if (trailingByte) body.Add(0);
            return [.. body];
        }
        var shape = TestLogin.Of("kr-1256");

        Assert.Equal(28 + 2 + 2, Body("", []).Length);
        Assert.Equal("", LoginMessageCodec.ReadRequest(Body("", []), shape).Account);
        var ticket = "bff-ticket:" + new string('a', 64);
        Assert.Equal(ticket, LoginMessageCodec.ReadRequest(Body(ticket, [1, 2]), shape).Account);

        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(Body("acct", [1], trailingByte: true), shape));
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(Body("acct", [1])[..^1], shape));
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest([], shape));
        var oversized = Body("", []);
        BitConverter.GetBytes(1025).CopyTo(oversized, 12 + 2);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(oversized, shape));
    }

    [Fact]
    public void TheReconnectBodyIsRecognisedByItsLeadingBuildMarkerOnly()
    {
        // kr-1256 sub_9E7080 and sub_9E1EB0 store the literal 1256 first; a launcher body starts with a string length.
        Assert.True(LoginBodyShape.IsReconnectShape(BitConverter.GetBytes(LoginBodyShape.ReconnectMarker)));
        Assert.False(LoginBodyShape.IsReconnectShape(BitConverter.GetBytes(75)));
        Assert.False(LoginBodyShape.IsReconnectShape([0xE8, 0x04, 0x00]));
    }

    [Fact]
    public void Cn2012NativeDialogSendsAnUnpaddedIdAndNoPasswordField()
    {
        // cn-324 sub_584B70 checks the password locally, then fills only the account in sub_583E10.
        var body = new List<byte>(32);
        body.AddRange(BitConverter.GetBytes(8));
        body.AddRange(System.Text.Encoding.Unicode.GetBytes("native12"));
        body.AddRange(new byte[12]);
        Assert.Equal(32, body.Count);
        Assert.Equal("native12", LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("cn-324")).Account);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray()[..31], TestLogin.Of("cn-324")));
    }

    [Fact]
    public void Id400ReplyStopsAfterUnknown6()
    {
        var time = DateTime.FromOADate(BitConverter.ToDouble(Convert.FromHexString("2B7846827A9AE640")));
        var body = LoginMessageCodec.WriteAccepted(new LoginAccepted(0x11A, "testplayer01", "TestPlayer01", time, 300010), TestLogin.Of("id-400"), TestBuilds.Packets, "id-400");
        // sub_798BB0 reads the kr-812 fields up to u32 unknown6 and no further.
        var kr812 = LoginMessageCodec.WriteAccepted(new LoginAccepted(0x11A, "testplayer01", "TestPlayer01", time, 300010), TestLogin.Of("kr-812"), TestBuilds.Packets, "kr-812");
        Assert.Equal(111 - 1 - 4, body.Length);
        Assert.Equal(kr812[..body.Length], body);
        Assert.Equal(4 + 4 + 4 + 4 + 16 + 8 + 4 + 4, LoginMessageCodec.WriteRejected(new LoginRejected(LoginRejection.AccountNotFound), TestLogin.Of("id-400"), TestBuilds.Packets, "id-400").Length);
    }

    [Fact]
    public void Id400RequestHasNoTrailingPair()
    {
        // sub_5E8B10: the padded account, two strings and the adapter list, nothing after it.
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(1024));
        var chars = new char[1024];
        "admin".CopyTo(chars);
        body.AddRange(System.Text.Encoding.Unicode.GetBytes(chars));
        body.AddRange(new byte[4 + 4]);
        body.AddRange(BitConverter.GetBytes(1));
        body.AddRange(new byte[] { 127, 0, 0, 1 });
        Assert.Equal("admin", LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("id-400")).Account);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("kr-812")));
    }

    [Fact]
    public void Id1809RequestEndsWithOneTokenAndTheReplyMatchesId400()
    {
        // sub_632220: the padded account, two strings, the adapter list, then one u32 (this[26]).
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(1024));
        var chars = new char[1024];
        "admin".CopyTo(chars);
        body.AddRange(System.Text.Encoding.Unicode.GetBytes(chars));
        body.AddRange(new byte[4 + 4]);
        body.AddRange(BitConverter.GetBytes(1));
        body.AddRange(new byte[] { 127, 0, 0, 1 });
        Assert.Equal(2048 + 4 + 4 + 4 + 4 + 4, body.Count);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("id-1809")));
        body.AddRange(BitConverter.GetBytes(0x5A5A5A5A));
        Assert.Equal("admin", LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("id-1809")).Account);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("id-400")));
        body.AddRange(new byte[4]);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("id-1809")));

        // sub_7BE790 reads the same fields as id-400 sub_798BB0.
        var accepted = new LoginAccepted(0x11A, "testplayer01", "TestPlayer01", DateTime.FromOADate(0), 300010);
        Assert.Equal(LoginMessageCodec.WriteAccepted(accepted, TestLogin.Of("id-400"), TestBuilds.Packets, "id-400"), LoginMessageCodec.WriteAccepted(accepted, TestLogin.Of("id-1809"), TestBuilds.Packets, "id-1809"));
    }

    [Fact]
    public void Cn706RequestEndsWithOneTokenAndTheReplyMatchesId400()
    {
        // sub_611A60/sub_611AD0: the padded account, two strings, the adapter list, then one
        // u32 at +104 (an obfuscated dword; read and ignored).
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(1024));
        var chars = new char[1024];
        "admin".CopyTo(chars);
        body.AddRange(System.Text.Encoding.Unicode.GetBytes(chars));
        body.AddRange(new byte[4 + 4]);
        body.AddRange(BitConverter.GetBytes(1));
        body.AddRange(new byte[] { 127, 0, 0, 1 });
        Assert.Equal(2048 + 4 + 4 + 4 + 4 + 4, body.Count);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("cn-706")));
        body.AddRange(BitConverter.GetBytes(0x5A5A5A5A));
        Assert.Equal("admin", LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("cn-706")).Account);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("id-400")));
        body.AddRange(new byte[4]);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(body.ToArray(), TestLogin.Of("cn-706")));

        // sub_7A0FA0 reads the same fields as id-400 sub_798BB0.
        var accepted = new LoginAccepted(0x11A, "testplayer01", "TestPlayer01", DateTime.FromOADate(0), 300010);
        Assert.Equal(LoginMessageCodec.WriteAccepted(accepted, TestLogin.Of("id-400"), TestBuilds.Packets, "id-400"), LoginMessageCodec.WriteAccepted(accepted, TestLogin.Of("cn-706"), TestBuilds.Packets, "cn-706"));
    }

    [Fact]
    public void Cn717KeepsTheRequestTrailerButNotTheReplyTrailer()
    {
        var time = DateTime.FromOADate(0);
        var reply = LoginMessageCodec.WriteAccepted(new LoginAccepted(1, "a", "b", time, 300001), TestLogin.Of("cn-717"), TestBuilds.Packets, "cn-717");
        Assert.Equal(LoginMessageCodec.WriteAccepted(new LoginAccepted(1, "a", "b", time, 300001), TestLogin.Of("id-400"), TestBuilds.Packets, "id-400"), reply);
        var request = new byte[4 * 3 + 4 + 4 + 4];
        Assert.Equal("", LoginMessageCodec.ReadRequest(request, TestLogin.Of("cn-717")).Account);
    }

    [Fact]
    public void Cn279ReplyStopsAfterTheFinalStringAndPinsIdentityOffsets()
    {
        // 0x44F740 ends at the string; 0x6F7FE0 consumes user number before character type.
        var accepted = new LoginAccepted(7, "ab", "cd", DateTime.FromOADate(0), 300003);
        var reply = LoginMessageCodec.WriteAccepted(accepted, TestLogin.Of("cn-279"), TestBuilds.Packets, "cn-279");
        Assert.Equal(62, reply.Length);
        Assert.Equal(7, BitConverter.ToInt32(reply, 12));
        Assert.Equal(3, BitConverter.ToInt32(reply, 24));
        Assert.Equal(5, BitConverter.ToInt32(reply, 48));
        Assert.Equal(System.Text.Encoding.Unicode.GetBytes("LOCAL"), reply[52..]);
        Assert.Equal(LoginMessageCodec.WriteAccepted(accepted, TestLogin.Of("cn-324"), TestBuilds.Packets, "cn-324")[..^4], reply);
        var rejected = LoginMessageCodec.WriteRejected(new LoginRejected(LoginRejection.AccountNotFound), TestLogin.Of("cn-279"), TestBuilds.Packets, "cn-279");
        Assert.Equal(44, rejected.Length);
        Assert.Equal(1, BitConverter.ToInt32(rejected));
    }

    [Fact]
    public void Cn279RequestConsumesThreeStringsAndTheAdapterVectorOnly()
    {
        // 0x420100: wstr "a", two empty strings, one adapter, and no trailer.
        var request = Convert.FromHexString("0100000061000000000000000000010000007F000001");
        Assert.Equal(22, request.Length);
        Assert.Equal("a", LoginMessageCodec.ReadRequest(request, TestLogin.Of("cn-279")).Account);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(request[..^1], TestLogin.Of("cn-279")));
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest([.. request, 0], TestLogin.Of("cn-279")));
    }

    [Fact]
    public void Cn774UsesTheLoginResponseLayout()
    {
        var reply = LoginMessageCodec.WriteAccepted(new LoginAccepted(7, "ab", "cd", DateTime.FromOADate(0), 300003), TestLogin.Cn774, TestBuilds.Packets, "cn-735");
        // i32, wstr "ab" (4 + 4), i32, wstr "cd" (4 + 4), i32 x4, i64, i32, national flag.
        Assert.Equal(4 + 8 + 4 + 8 + 16 + 8 + 4 + 4, reply.Length);
        Assert.Equal(7, BitConverter.ToInt32(reply, 12));
        Assert.Equal(3, BitConverter.ToInt32(reply, 24));
        Assert.Equal(1, BitConverter.ToInt32(reply, reply.Length - 4));
        // The request carries the kr-812 trailer and one more wstr.
        var request = new byte[4 * 3 + 4 + 4 + 4 + 4];
        Assert.Equal("", LoginMessageCodec.ReadRequest(request, TestLogin.Cn774).Account);
        Assert.Equal(12, LoginMessageCodec.WriteRejected(new LoginRejected(LoginRejection.AccountNotFound), TestLogin.Cn774, TestBuilds.Packets, "cn-735").Length);
    }

    [Theory]
    [InlineData("cn-735", "cn-717", "kr-812")]
    [InlineData("cn-738", "cn-735", "cn-717", "kr-812")]
    [InlineData("cn-742", "cn-738", "cn-735", "cn-717", "kr-812")]
    public void MidCnLoginResponseBuildsKeepCn717RequestAndCn774Reply(params string[] lineage)
    {
        Assert.Equal("cn-735", TestBuilds.Packets.NearestBuild(lineage));
        var shape = TestLogin.OfLineage(lineage);
        var accepted = new LoginAccepted(7, "ab", "cd", DateTime.FromOADate(0), 300003);
        var reply = LoginMessageCodec.WriteAccepted(accepted, shape, TestBuilds.Packets, "cn-735");
        Assert.Equal(LoginMessageCodec.WriteAccepted(accepted, TestLogin.Cn774, TestBuilds.Packets, "cn-735"), reply);
        Assert.Equal(4 + 8 + 4 + 8 + 16 + 8 + 4 + 4, reply.Length);
        Assert.Equal(7, BitConverter.ToInt32(reply, 12));
        Assert.Equal(3, BitConverter.ToInt32(reply, 24));
        Assert.Equal(1, BitConverter.ToInt32(reply, reply.Length - 4));
        // cn-717 request: three empty wstr, zero adapters, two trailer u32; no extra wstr.
        var request = new byte[4 * 3 + 4 + 4 + 4];
        Assert.Equal(24, request.Length);
        Assert.Equal("", LoginMessageCodec.ReadRequest(request, shape).Account);
        Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(request, TestLogin.Cn774));
        Assert.Equal(12, LoginMessageCodec.WriteRejected(new LoginRejected(LoginRejection.AccountNotFound), shape, TestBuilds.Packets, "cn-735").Length);
    }
}
