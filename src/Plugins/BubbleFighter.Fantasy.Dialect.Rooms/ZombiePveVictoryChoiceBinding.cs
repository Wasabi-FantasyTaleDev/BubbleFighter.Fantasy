using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed class ZombiePveVictoryChoiceBinding(string protocol, IOpcodeTable table, PacketTables tables, string build)
    : IMessageSender<RoundZombiePveVictoryRanks>, IMessageSender<RoundZombiePveVictoryOpened>, IMessageSender<RoundZombiePveVictoryFollowUp>
{
    public string Protocol => protocol;

    internal void MountIfAudited(IPluginContext context)
    {
        if (!tables.ValuesOf(build).TryGetValue("zombie_pve_victory_choice_builds", out var raw)
            || raw is not string[] audited || audited.Length == 0 || audited.Distinct(StringComparer.Ordinal).Count() != audited.Length
            || audited.Any(item => !tables.Builds.Contains(item, StringComparer.Ordinal)))
            throw new ConfigException("zombie_pve_victory_choice_builds must list distinct known builds.");
        if (!audited.Contains(build, StringComparer.Ordinal)) return;
        _ = table.S2C("gspNotifyRankInfo");
        _ = table.S2C("gspGameAwardDialog");
        _ = table.S2C("gspRepAwardSelectSuccess");
        PacketLayoutContract.RequireSignature(tables, build, "PveRankInfo", "Zombie PvE victory snapshot",
            "selected_display_id:u32,other_display_ids:list<u32>,rows:list<PveRankRow>");
        PacketLayoutContract.RequireSignature(tables, build, "PveRankRow", "Zombie PvE victory ranking",
            "seat:u32,unknown_8:u32,unknown_12:u32,unknown_16:u32,rank:u32");
        PacketLayoutContract.RequireSignature(tables, build, "HordeLotterySelected", "Zombie PvE result event",
            "card:u32,seat:u32,second_card:u32");
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundZombiePveVictoryRanks>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundZombiePveVictoryOpened>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundZombiePveVictoryFollowUp>>(this));
    }

    public ValueTask SendAsync(ISession session, RoundZombiePveVictoryRanks message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspNotifyRankInfo"), PvePointsBinding.EncodeZombiePveVictoryRanks(tables, build,
            message.Rows, message.SelectedDisplayId, [message.FirstOther, message.SecondOther, message.ThirdOther]), ct);

    public ValueTask SendAsync(ISession session, RoundZombiePveVictoryOpened message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspGameAwardDialog"), ReadOnlyMemory<byte>.Empty, ct);

    public ValueTask SendAsync(ISession session, RoundZombiePveVictoryFollowUp message, CancellationToken ct) =>
        // kr-812 sub_806E20, kr-1256 sub_9E1EB0, cn-774 sub_8D7D20 and tw-454 sub_A578B0 copy all three words.
        session.SendAsync(table.S2C("gspRepAwardSelectSuccess"), PacketCodec.Write(tables, build, "HordeLotterySelected",
            new Dictionary<string, object?> { ["card"] = message.FirstWord, ["seat"] = message.SecondWord, ["second_card"] = message.ThirdWord }), ct);
}
