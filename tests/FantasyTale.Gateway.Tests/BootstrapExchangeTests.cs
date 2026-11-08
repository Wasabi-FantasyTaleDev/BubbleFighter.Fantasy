using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks native startup requests after directory delivery without replacing client configuration.</summary>
public sealed class BootstrapExchangeTests
{
    /// <summary>Every mask bit selects exactly one checksum word, including the unsigned high bit.</summary>
    [Theory]
    [InlineData("kr-1256", 32, 4)]
    [InlineData("cn-735", 10, 2)]
    [InlineData("cn-774", 16, 2)]
    [InlineData("tw-454", 16, 2)]
    public void Conditional_checksums_pin_all_bits_and_reject_incomplete_or_trailing_data(string build, int bits, int width)
    {
        var exchange = Assert.Single(CompiledBuildCatalog.Resolve(build).BootstrapExchanges);
        Assert.Equal(1500, exchange.Request);
        Assert.Equal(1500, exchange.Reply);
        Assert.Equal(new byte[width], exchange.ReplyPayload);
        for (var bit = 0; bit < bits; bit++)
        {
            var bytes = exchange.RequestLayout.Write(new Dictionary<string, object?>
            {
                ["mask"] = 1u << bit, ["checksum_" + bit] = 0xABCDEF01u,
            });
            Assert.Equal(width + 4, bytes.Length);
            Assert.Equal(1u << bit, width == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes));
            Assert.Equal(0xABCDEF01u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(width)));
            exchange.RequestLayout.ValidatePayload(bytes, 1024, 32);
            Assert.Throws<PacketFormatException>(() => exchange.RequestLayout.ValidatePayload(bytes[..^1], 1024, 32));
            Assert.Throws<PacketFormatException>(() => exchange.RequestLayout.ValidatePayload([.. bytes, 0], 1024, 32));
        }
        var all = exchange.RequestLayout.Write(new Dictionary<string, object?> { ["mask"] = bits == 32 ? uint.MaxValue : (1u << bits) - 1 });
        Assert.Equal(width + bits * 4, all.Length);
        exchange.RequestLayout.ValidatePayload(all, 1024, 32);
        exchange.RequestLayout.ValidatePayload(new byte[width], 1024, 32);
    }

    /// <summary>Coalesced or fragmented startup requests retain the continuous cipher after patch and directory replies.</summary>
    [Theory]
    [InlineData("kr-1256", false, false)]
    [InlineData("kr-1256", true, false)]
    [InlineData("cn-735", false, false)]
    [InlineData("cn-735", true, false)]
    [InlineData("cn-774", false, false)]
    [InlineData("cn-774", true, false)]
    [InlineData("cn-774", false, true)]
    [InlineData("cn-774", true, true)]
    [InlineData("tw-454", false, false)]
    [InlineData("tw-454", true, false)]
    [InlineData("tw-454", false, true)]
    [InlineData("tw-454", true, true)]
    public async Task Repeated_startup_requests_receive_exact_replies_without_restarting_the_cipher(string build, bool fragmented, bool beforeFirstConnect)
    {
        var options = GatewayConfigurationTests.Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var game = await session.ConnectGameAsync();
        var profile = options.Validate();
        var exchange = Assert.Single(profile.BootstrapExchanges);
        var first = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        var request = profile.Dialect.EncodeFrame(exchange.Request,
            exchange.RequestLayout.Write(new Dictionary<string, object?> { ["mask"] = 1u, ["checksum_0"] = 0xABCDEF01u }));
        var burst = beforeFirstConnect ? request.Concat(first).Concat(request).ToArray() : first.Concat(request).Concat(request).ToArray();
        var encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer);
        encryptor.Apply(burst);
        if (fragmented)
        {
            await game.GetStream().WriteAsync(burst.AsMemory(0, 3), session.Token);
            await game.GetStream().WriteAsync(burst.AsMemory(3), session.Token);
        }
        else await game.GetStream().WriteAsync(burst, session.Token);
        using var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ServerToClient, 16384);
        var frames = new List<DecodedFrame>();
        var buffer = new byte[4096];
        while (frames.Count < 4)
        {
            var read = await game.GetStream().ReadAsync(buffer, session.Token);
            Assert.NotEqual(0, read);
            frames.AddRange(decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(item => item.Value));
        }
        var expected = beforeFirstConnect ? new[] { exchange.Reply, profile.PatchReply, profile.CenterList, exchange.Reply }
            : new[] { profile.PatchReply, profile.CenterList, exchange.Reply, exchange.Reply };
        Assert.Equal(expected, frames.Select(frame => frame.Opcode));
        Assert.All(frames.Where(frame => frame.Opcode == exchange.Reply), frame => Assert.Equal(exchange.ReplyPayload, frame.Payload));
        Assert.Contains("bf-center-selector: native startup exchange answered; opcode=1500.", session.Log.ToString());
        Assert.False(session.Work.IsCompleted);
        game.Dispose();
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>Invalid startup bodies fail rather than being discarded or answered optimistically.</summary>
    [Theory]
    [InlineData("kr-1256", false, 0)]
    [InlineData("kr-1256", false, 3)]
    [InlineData("kr-1256", false, 64)]
    [InlineData("kr-1256", false, 69)]
    [InlineData("cn-735", false, 0)]
    [InlineData("cn-735", false, 1)]
    [InlineData("cn-735", false, 5)]
    [InlineData("cn-735", false, 7)]
    [InlineData("cn-774", false, 1)]
    [InlineData("cn-774", false, 30)]
    [InlineData("cn-774", true, 1)]
    [InlineData("cn-774", true, 30)]
    [InlineData("tw-454", false, 1)]
    [InlineData("tw-454", false, 30)]
    [InlineData("tw-454", true, 1)]
    [InlineData("tw-454", true, 30)]
    public async Task Malformed_startup_requests_remain_protocol_errors(string build, bool beforeFirstConnect, int length)
    {
        await using var session = await GatewayLifecycleTests.Session.StartAsync(GatewayConfigurationTests.Options(build));
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve(build);
        var exchange = Assert.Single(profile.BootstrapExchanges);
        var first = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        var invalid = new byte[length];
        if (length >= 4 && build == "kr-1256") BinaryPrimitives.WriteUInt32LittleEndian(invalid, 0xFFFF);
        else if (length >= 2) BinaryPrimitives.WriteUInt16LittleEndian(invalid, 1);
        var malformed = profile.Dialect.EncodeFrame(exchange.Request, invalid);
        var burst = beforeFirstConnect ? malformed.Concat(first).ToArray() : first.Concat(malformed).ToArray();
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(burst);
        await game.GetStream().WriteAsync(burst, session.Token);
        await Assert.ThrowsAsync<PacketFormatException>(() => session.Work.WaitAsync(session.Token));
        Assert.Contains($"bootstrap failure; phase={(beforeFirstConnect ? "handshake" : "selection")}, kind=PacketFormatException.", session.Log.ToString());
    }
}
