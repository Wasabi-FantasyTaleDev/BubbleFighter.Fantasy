using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The one translation of a respawn request (C2S <c>gcpRespawn</c>) and its grant (S2C <c>gspRespawn</c>) for every
/// build. The build's <c>respawn</c> block in <c>packets/builds.yaml</c> says which body the pair carries and the rules
/// the round grants it by; the request carries those rules to the shared round flow. The family's room binding owns the
/// request route, so a malformed body keeps that family's existing handling.
/// </summary>
internal sealed class RoundRespawnBinding(string protocol, IOpcodeTable table, PacketTables packets, string build,
    IOpcodeTableRegistry? sessionTables = null)
    : IInboundMessageBinding<RoundSpawnAccepted>, IMessageSender<RoundPlayerRespawned>
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal) { "body", "audience", "completion", "admission" };

    private ushort? _grant;
    private RespawnBody _body;
    private RoundRespawnRules _rules = RoundRespawnRules.RoomGrant;

    public string Protocol => protocol;

    /// <summary>Validates the build's respawn data and layouts, then registers the request binding and the grant sender.</summary>
    /// <param name="context">The owning row's context.</param>
    internal void Mount(IPluginContext context)
    {
        (_body, _rules) = RulesOf(packets, build);
        // A spawn-index build resolves the grant when it sends, as its room rows always have; an echo build needs it now.
        _grant = table.Find(PacketDirection.ServerToClient, "gspRespawn");
        if (_grant is null && _body == RespawnBody.Echo)
            throw new ConfigException($"{build} respawn requires a named S2C gspRespawn grant.");
        if (_body == RespawnBody.SpawnIndex)
        {
            if (sessionTables is null)
                throw new ConfigException("Spawn-index respawns require the session's opcode table registry.");
            PacketLayoutContract.RequireFields(packets, build, "RoundSpawnAcceptance", "Respawn requests", ("actor", "u32"), ("spawn_index", "u32"));
            PacketLayoutContract.RequireFields(packets, build, "RoundSpawnConfirmation", "Respawn grants", ("actor", "u32"), ("spawn_index", "u32"));
        }
        else
        {
            // cn-774 sub_8B5670 and tw-454 sub_729D30 read three plain words and a trailing seat word;
            // kr-1256 sub_6F5E50 instead reads two tagged values. The tables own that distinction.
            PacketLayoutContract.RequireFields(packets, build, "RoundResetRequest", "Native life resets", ("actor", "u32"));
            var request = packets.Resolve(build, "RoundResetRequest");
            var reply = packets.Resolve(build, "RoundResetReply");
            if (reply.Count != request.Count + 1 || reply[^1].Type != "u32" || reply[^1].Default != 0
                || !request.Select(field => (field.Name, field.Type)).SequenceEqual(reply.Take(request.Count).Select(field => (field.Name, field.Type))))
                throw new ConfigException("RoundResetReply must echo RoundResetRequest and append the default-zero seat word.");
        }
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundSpawnAccepted>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundPlayerRespawned>>(this));
    }

    /// <summary>Reads a complete native respawn request in the build's body, stamped with the build's rules.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>The typed request.</returns>
    internal RoundSpawnAccepted Read(ReadOnlySpan<byte> body)
    {
        if (_body == RespawnBody.SpawnIndex)
            return ReadSpawnAcceptance(packets, build, body) with { Rules = _rules };
        var values = ReadTagged(packets, build, "RoundResetRequest", body);
        return new RoundSpawnAccepted((int)values["actor"]!, 0) { Request = body.ToArray(), Rules = _rules };
    }

    public ValueTask SendAsync(ISession session, RoundPlayerRespawned message, CancellationToken ct) =>
        session.SendAsync(_body == RespawnBody.SpawnIndex ? sessionTables!.Get(session.Profile.Id).S2C("gspRespawn") : _grant!.Value, _body == RespawnBody.SpawnIndex
            ? WriteRespawn(packets, build, message.Actor, message.SpawnIndex)
            : PacketCodec.Write(packets, build, "RoundResetReply", ReadTagged(packets, build, "RoundResetRequest", message.Request.Span)), ct);

    /// <summary>
    /// Reads the build's <c>respawn</c> block: <c>body</c> (<c>spawn_index</c> or <c>echo</c>), <c>audience</c>
    /// (<c>room</c> or <c>requester</c>), <c>completion</c> (<c>grant</c> or <c>match_confirmation</c>) and
    /// <c>admission</c> (<c>owner</c> or <c>cleaned_death</c>), all required and nothing else allowed.
    /// </summary>
    /// <param name="packets">The packet tables the build is named in.</param>
    /// <param name="build">The build.</param>
    /// <returns>The body kind and the grant rules.</returns>
    /// <exception cref="ConfigException">Thrown when the block is missing, has an unknown key or a value outside its names.</exception>
    internal static (RespawnBody Body, RoundRespawnRules Rules) RulesOf(PacketTables packets, string build)
    {
        var where = $"'{packets.Directory}' build '{build}' respawn";
        if (packets.ValuesOf(build).GetValueOrDefault("respawn") is not Dictionary<string, object?> block)
            throw new ConfigException($"{where}: the build must state the respawn block.");
        if (block.Keys.FirstOrDefault(key => !Keys.Contains(key)) is { } unknown)
            throw new ConfigException($"{where} has the unknown key '{unknown}'.");
        return (
            Choice(block, "body", where, ("spawn_index", RespawnBody.SpawnIndex), ("echo", RespawnBody.Echo)),
            new(Choice(block, "audience", where, ("room", RoundRespawnAudience.Room), ("requester", RoundRespawnAudience.Requester)),
                Choice(block, "completion", where, ("grant", RoundRespawnCompletion.Grant), ("match_confirmation", RoundRespawnCompletion.MatchConfirmation)),
                Choice(block, "admission", where, ("owner", RoundRespawnAdmission.Owner), ("cleaned_death", RoundRespawnAdmission.CleanedDeath))));
    }

    private static T Choice<T>(Dictionary<string, object?> block, string key, string where, params (string Name, T Value)[] names)
    {
        if (!block.TryGetValue(key, out var raw))
            throw new ConfigException($"{where}.{key} is missing.");
        foreach (var (name, value) in names)
            if (raw is string text && text == name)
                return value;
        throw new ConfigException($"{where}.{key} must be one of {string.Join(", ", names.Select(entry => entry.Name))}; got '{raw}'.");
    }

    /// <summary>
    /// Writes the build's actor/index confirmation and any native revival fields. <c>gspRespawn</c> confirms the
    /// actor's revival (kr-812 sub_503530 / kr-812 sub_51E2C0); the native peer consumer completes its local state
    /// transition (kr-812 sub_51E440). CN2011 revives without a packet flag (cn-279 sub_81B470); the table supplies
    /// that difference.
    /// </summary>
    internal static byte[] WriteRespawn(PacketTables tables, string build, int handle, int spawnIndex) =>
        PacketCodec.Write(tables, build, "RoundSpawnConfirmation", new Dictionary<string, object?>
        {
            ["actor"] = handle, ["spawn_index"] = spawnIndex,
        });

    /// <summary>Reads a complete native spawn acceptance, preserving signed actor and index bits.</summary>
    internal static RoundSpawnAccepted ReadSpawnAcceptance(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RoundSpawnAcceptance", body);
        var handle = (int)fields["actor"];
        return new RoundSpawnAccepted(handle, (int)fields["spawn_index"]) { ActorSeat = PlayRoomCodec.PlayerSeatOf(handle) };
    }

    /// <summary>Reads a structure whose optional values must carry the present tag or the absent default tag.</summary>
    internal static Dictionary<string, object?> ReadTagged(PacketTables packets, string build, string structure, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.Read(packets, build, structure, body);
        // kr-1256 sub_10C04D0 / kr-1256 sub_10C0640 accept 0x3A with a word or the absent 0xBA marker.
        foreach (var field in packets.Resolve(build, structure))
            if (field.Type.StartsWith("optional<", StringComparison.Ordinal)
                && values[field.Name] is byte marker && marker != field.Default)
                throw new PacketFormatException($"{structure}.{field.Name} has an invalid absent-value tag.", 0);
        return values;
    }
}

/// <summary>What a build's respawn request and grant carry.</summary>
internal enum RespawnBody
{
    /// <summary>The character handle and a spawn index (<c>RoundSpawnAcceptance</c> / <c>RoundSpawnConfirmation</c>).</summary>
    SpawnIndex,

    /// <summary>A request the grant repeats with a trailing word (<c>RoundResetRequest</c> / <c>RoundResetReply</c>).</summary>
    Echo,
}
