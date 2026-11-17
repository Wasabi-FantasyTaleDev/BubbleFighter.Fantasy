using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class ExtremeRacingBinding : IMessageSender<RoundExtremeRacingMapState>
{
    internal void MountMapState(IPluginContext context)
    {
        RequireMapStateLayout(packets, build);
        _ = table.S2C("gspExtremeRacingMapState_");
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundExtremeRacingMapState>>(this));
    }

    /// <summary>Publishes the complete native selection after the receiving client's map has loaded.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingMapState message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingMapState_"), WriteMapState(packets, build, message), ct);

    internal static byte[] WriteMapState(PacketTables packets, string build, RoundExtremeRacingMapState message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.VanishSelections);
        RequireMapStateLayout(packets, build);
        // kr-1256 sub_CE5600 case 0x2CC reads three words plus list<u32>; its full body must fit the native frame.
        if (message.SecondBombPattern is < 0 or > 9 || message.ThirdBombPattern is < 0 or > 9 || message.StoneSeed < 0
            || message.VanishSelections.Count > (ushort.MaxValue - 16) / 4 || message.VanishSelections.Any(route => route < 0))
            throw new InvalidDataException("ExtremeRacing map state has an invalid pattern, seed, route or wire count.");
        return PacketCodec.Write(packets, build, "ExtremeRacingMapState", new Dictionary<string, object?>
        {
            ["second_bomb_pattern"] = message.SecondBombPattern, ["third_bomb_pattern"] = message.ThirdBombPattern,
            ["stone_seed"] = message.StoneSeed, ["vanish_selections"] = message.VanishSelections.Select(route => (object?)route).ToArray(),
        });
    }

    private static void RequireMapStateLayout(PacketTables packets, string build) =>
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingMapState", "ExtremeRacing map state",
            "second_bomb_pattern:u32,third_bomb_pattern:u32,stone_seed:u32,vanish_selections:list<u32>");
}
