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
        PacketLayoutContract.RequireFields(tables, build, "FighterInfoRequest", "Fighter info",
            ("target_user_number", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "FighterInfoReply", "Fighter info",
            ("result", "u32"), ("fighter", "FighterInfo"), ("partner", "FighterInfo"));
        PacketLayoutContract.RequireFields(tables, build, "FighterInfo", "Fighter info",
            ("nickname", "wstr"), ("level", "u32"));
        if (IsModernBlock)
            PacketLayoutContract.RequireFields(tables, build, "FighterInfo", "Fighter info",
                ("unknown_4", "u32"), ("unknown_32", "u32"), ("unknown_504", "wstr"), ("social", "SocialSystemInfo"));
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

    public ValueTask SendAsync(ISession session, FighterInfoReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepFighterInfo"), Write(message), ct);

    /// <summary>Reads a request body: the clicked player's account id and the word that follows it.</summary>
    /// <param name="body">The packet body.</param>
    internal FighterInfoRequested Read(ReadOnlySpan<byte> body) =>
        new((int)PacketCodec.ReadScalars(tables, build, "FighterInfoRequest", body)["target_user_number"]!);

    /// <summary>
    /// Writes the reply body for an answer: the player's record or the build's failure result. The modern family
    /// fills the identity, Dao character type and the +504 display name its dialog reads (kr-1256 sub_99A430,
    /// handler kr-1256 sub_9BCE60); the legacy builds keep their name/level-only block.
    /// </summary>
    /// <param name="message">The answer.</param>
    internal byte[] Write(FighterInfoReplied message)
    {
        // The partner block and both strings are read and unused by the handler (kr-812 sub_82C960); they keep their
        // constructor defaults. An empty map under a nested field stops it from inheriting the parent's nickname.
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = message.Fighter is null ? NotFound : Found,
            ["fighter"] = message.Fighter is { } fighter
                ? FighterValues(fighter)
                : new Dictionary<string, object?>(StringComparer.Ordinal),
            ["partner"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
        return PacketCodec.Write(tables, build, "FighterInfoReply", fields);
    }

    // The modern family's FighterInfo carries the identity at +4, the Dao character type at +32 and the nickname
    // again at +504; the legacy base block takes the name and level from the enclosing reply. The table says which
    // shape the build has: the modern block is the one that states +504.
    private Dictionary<string, object?> FighterValues(PlayerIdentity fighter)
    {
        if (!IsModernBlock)
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["nickname"] = fighter.Nickname, ["level"] = fighter.Level,
            };
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["unknown_4"] = fighter.UserNum,
            ["nickname"] = fighter.Nickname,
            ["unknown_32"] = 1,
            ["level"] = fighter.Level,
            ["unknown_504"] = fighter.Nickname,
            // The social block is sent at its defaults; its own unknown_4 must not take the identity above.
            ["social"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
    }

    private bool IsModernBlock => tables.Resolve(build, "FighterInfo").Any(candidate => candidate.Name == "unknown_504");
}
