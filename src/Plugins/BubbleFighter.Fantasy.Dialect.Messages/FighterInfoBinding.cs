using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The legacy player menu's "view info" packets (layouts in <c>packets/fighter-info.yaml</c>, numbers from the build's
/// opcode table by name): C2S <c>gcpReqFighterInfo</c> names the clicked player by account id, and S2C
/// <c>gspRepFighterInfo</c> answers with a result word and two <c>FighterInfo</c> blocks. The client shows its dialog
/// only for result 1 (kr-812 sub_82C960) and a notice for every other value, so an unknown player is answered with
/// result 0 and constructor-default blocks.
/// </summary>
internal sealed class FighterInfoBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<FighterInfoRequested>, IMessageSender<FighterInfoReplied>
{
    /// <summary>The result the client's handler takes as success (kr-812 sub_82C960 branches on <c>== 1</c>).</summary>
    internal const uint Found = 1;

    /// <summary>The result written for a player the server cannot show; the client treats every non-1 value alike.</summary>
    internal const uint NotFound = 0;

    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        // The legacy reply ends after the two strings and the modern family adds a four-word tail, so the layouts
        // are checked by the type of every field this binding reads or writes rather than by one signature string:
        // a field the code casts or sizes must have that type, or the row fails here instead of on the first packet.
        // cn-279's restatement replaces the partner block with an awards vector, so the partner is required only
        // where the build's resolved reply states it.
        PacketLayoutContract.RequireFields(tables, build, "FighterInfoRequest", "Fighter info",
            ("target_user_number", "u32"));
        var replyFields = new List<(string, string)> { ("result", "u32"), ("fighter", "FighterInfo") };
        if (tables.Resolve(build, "FighterInfoReply").Any(candidate => candidate.Name == "partner"))
            replyFields.Add(("partner", "FighterInfo"));
        PacketLayoutContract.RequireFields(tables, build, "FighterInfoReply", "Fighter info",
            replyFields.ToArray());
        PacketLayoutContract.RequireFields(tables, build, "FighterInfo", "Fighter info",
            ("user_number", "u32"), ("character_type", "u32"), ("nickname", "wstr"), ("level", "u32"),
            ("primary_wins", "u32"), ("primary_losses", "u32"), ("primary_draws", "u32"),
            ("primary_escapes", "u32"), ("primary_knockdowns", "u32"), ("primary_kills", "u32"), ("primary_rescues", "u32"));
        if (IsModernBlock)
            PacketLayoutContract.RequireFields(tables, build, "FighterInfo", "Fighter info",
                ("unknown_504", "wstr"), ("social", "SocialSystemInfo"));
        _ = EquipmentViewValues.Of(tables, build);
        _ = opcodes.S2C("gspInventoryViewUpdateView");
        _ = opcodes.S2C("gspRepFighterInfo");
        var requestOpcode = opcodes.C2S("gcpReqFighterInfo");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<FighterInfoRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<FighterInfoReplied>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, requestOpcode, async (session, packet, ct) =>
        {
            var request = Read(packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("gcpReqFighterInfo has no typed handler.");
        }));
    }

    public async ValueTask SendAsync(ISession session, FighterInfoReplied message, CancellationToken ct)
    {
        // kr-812 sub_87DD90 and cn-774 sub_6C8420 fetch the target's cached equipment while constructing
        // the dialog. Publish the same immutable snapshot first; no local preset packets belong to this query.
        var snapshot = Prepare(message);
        message = snapshot.Reply;
        var reply = Encode(message, snapshot.Statistics);
        var view = message.Fighter is { } fighter && message.Appearance is { } appearance
            ? EquipmentViewBinding.WriteView(tables, build, fighter.UserNum, appearance.CharacterItemId, appearance.EquippedItemIds)
            : null;
        if (view is not null)
            await session.SendAsync(opcodes.S2C("gspInventoryViewUpdateView"), view, ct).ConfigureAwait(false);
        await session.SendAsync(opcodes.S2C("gspRepFighterInfo"), reply, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a request body: the clicked player's account id and the word that follows it.</summary>
    /// <param name="body">The packet body.</param>
    internal FighterInfoRequested Read(ReadOnlySpan<byte> body) =>
        new((int)PacketCodec.ReadScalars(tables, build, "FighterInfoRequest", body)["target_user_number"]!);

    /// <summary>
    /// Writes the player's identity and actual character type in the build's own record, or its failure result.
    /// The modern family also repeats the display name at +504 (kr-1256 sub_99A430, handler kr-1256 sub_9BCE60).
    /// </summary>
    /// <param name="message">The answer.</param>
    internal byte[] Write(FighterInfoReplied message)
    {
        var snapshot = Prepare(message);
        return Encode(snapshot.Reply, snapshot.Statistics);
    }

    private static (FighterInfoReplied Reply, FighterBattleProjection? Statistics) Prepare(FighterInfoReplied message)
    {
        if ((message.Fighter is null) != (message.Appearance is null)
            || message.Fighter is null && message.BattleRecords is not null)
            throw new ArgumentException("Fighter identity and appearance must form one complete snapshot.", nameof(message));
        if (message.BattleRecords is null) return (message, null);
        var statistics = FighterBattleProjection.Create(message.BattleRecords);
        return statistics is null ? (new(null), null) : (message, statistics);
    }

    private byte[] Encode(FighterInfoReplied message, FighterBattleProjection? statistics)
    {
        // The partner block and both strings are read and unused by the handler (kr-812 sub_82C960); they keep their
        // constructor defaults. An empty map under a nested field stops it from inheriting the parent's nickname.
        // cn-279's reply has no partner row; its awards vector keeps its empty default (count 0).
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = message.Fighter is null ? NotFound : Found,
            ["fighter"] = message.Fighter is { } fighter
                ? FighterValues(fighter, message.Appearance!, statistics)
                : new Dictionary<string, object?>(StringComparer.Ordinal),
        };
        if (tables.Resolve(build, "FighterInfoReply").Any(candidate => candidate.Name == "partner"))
            fields["partner"] = new Dictionary<string, object?>(StringComparer.Ordinal);
        return PacketCodec.Write(tables, build, "FighterInfoReply", fields);
    }

    // kr-812 sub_87DD90 passes +4/+36 to its preview; cn-279 sub_83B330 uses +4/+12 and cn-774
    // sub_6C8420 uses +4/+32. Their common names resolve to those offsets through the packet tables.
    private Dictionary<string, object?> FighterValues(PlayerIdentity fighter, FighterAppearance appearance,
        FighterBattleProjection? statistics)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user_number"] = fighter.UserNum,
            ["nickname"] = fighter.Nickname,
            ["character_type"] = appearance.CharacterItemId - 300000,
            ["level"] = NativeDisplayLevel.Project(tables, build, fighter.Level),
            ["unknown_504"] = fighter.Nickname,
            // The social block is sent at its defaults; its own unknown_4 must not take the identity above.
            ["social"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
        statistics?.AddTo(fields);
        return fields;
    }

    private bool IsModernBlock => tables.Resolve(build, "FighterInfo").Any(candidate => candidate.Name == "unknown_504");
}
