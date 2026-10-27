using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The character-creation packets of the builds whose tables state them (layouts in
/// <c>packets/character-creation.yaml</c>, numbers from the build's opcode table by name): the name check, the
/// create submit and the login-after-create request in; the creation opened, name reply and create reply out.
/// The open packet and the login-after request are named differently by the modern family
/// (<c>gspNotifyCreateUserInfo</c>, <c>Unknown33</c>) and the legacy family (<c>gspNotifyAutoTutorial_</c>,
/// <c>gcpLoginAfterCreateUserInfo</c>), so each is resolved by the single name the build's table states.
/// </summary>
internal sealed class CharacterCreationBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<CharacterNameRequested>, IInboundMessageBinding<CharacterCreateRequested>,
      IInboundMessageBinding<CharacterLoginRequested>, IMessageSender<CharacterCreationOpened>,
      IMessageSender<CharacterNameReplied>, IMessageSender<CharacterCreateReplied>
{
    private readonly DynamicConfigWire _configuration = new(tables, build);

    /// <summary>The S2C creation-opened names; the client reads no field of this frame on either family.</summary>
    internal static readonly string[] OpenNames = ["gspNotifyAutoTutorial_", "gspNotifyCreateUserInfo"];

    /// <summary>The C2S login-after-create names; both bodies are empty.</summary>
    internal static readonly string[] LoginNames = ["gcpLoginAfterCreateUserInfo", "Unknown33"];

    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        ValidateLayouts();
        var nameRequest = ResolveNameRequestOpcode(opcodes);
        var openName = ResolveSingleName(OpenNames, PacketDirection.ServerToClient, "creation opened");
        var createRequest = opcodes.C2S("gcpReqCreateUserInfo");
        var loginRequest = opcodes.C2S(ResolveSingleName(LoginNames, PacketDirection.ClientToServer, "login after create"));
        _ = opcodes.S2C("gspRepCheckCreateUserName");
        _ = opcodes.S2C("gspRepCreateUserInfo");
        _ = opcodes.S2C(openName);
        if (_configuration.HasCreationContent) _ = opcodes.S2C("gspRepDynamicConfig");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CharacterNameRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CharacterCreateRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CharacterLoginRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<CharacterCreationOpened>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<CharacterNameReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<CharacterCreateReplied>>(this));
        Register(nameRequest, "CharacterNameRequest", payload =>
        {
            var values = Read("CharacterNameRequest", payload.Span);
            return new CharacterNameRequested((string)values["nickname"]);
        });
        Register(createRequest, "CharacterCreateRequest", payload =>
        {
            var values = Read("CharacterCreateRequest", payload.Span);
            return new CharacterCreateRequested((string)values["nickname"], (int)values["character_type"]);
        });
        Register(loginRequest, "CharacterCreationLogin", payload =>
        {
            // Both families send this frame without a body (the client stamps the opcode on an empty packet).
            if (!payload.IsEmpty) throw new PacketFormatException("Character login must have an empty body.", 0);
            return new CharacterLoginRequested();
        });

        void Register<T>(ushort opcode, string name, Func<ReadOnlyMemory<byte>, T> read) where T : IMessage =>
            context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, opcode, async (session, packet, ct) =>
            {
                if (!await messages.DispatchAsync(protocol, session, read(packet.Payload), ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"No character creation handler for {name}.");
            }));
    }

    /// <summary>
    /// The single opcode-table name that one of <paramref name="names"/> states for this build. Zero names or two
    /// names that disagree is a configuration error naming the table and the packet, not a build heuristic.
    /// </summary>
    internal string ResolveSingleName(IReadOnlyList<string> names, PacketDirection direction, string what)
    {
        var found = names.Where(name => opcodes.Find(direction, name) is not null).ToArray();
        return found.Length == 1 ? found[0]
            : throw new ConfigException(
                $"Opcode table '{opcodes.Profile.Id}' states {found.Length} {what} names ({string.Join(", ", names)}); exactly one is required.");
    }

    internal static ushort ResolveNameRequestOpcode(IOpcodeTable table)
    {
        // kr-812 sub_875090 and cn-279 sub_6E8880 send the same single-string name request.
        var requests = table.All.Where(entry => entry.Direction == PacketDirection.ClientToServer).ToArray();
        var labelled = requests.FirstOrDefault(entry => entry.Name == "gcpReqCheckCreateUserName")?.Value;
        var coined = requests.FirstOrDefault(entry => entry.Name == "gcpReqCheckCreateUserName_")?.Value;
        if (labelled is null && coined is null)
        {
            labelled = table.Find(PacketDirection.ClientToServer, "gcpReqCheckCreateUserName");
            coined = table.Find(PacketDirection.ClientToServer, "gcpReqCheckCreateUserName_");
        }
        if (labelled.HasValue && coined.HasValue && labelled != coined)
            throw new ConfigException($"Opcode table '{table.Profile.Id}' assigns conflicting character name request aliases.");
        return labelled ?? coined ?? throw new ConfigException($"Opcode table '{table.Profile.Id}' has no character name request.");
    }

    // kr-812 sub_837170 sets the creation flag on S2C 12; cn-279 sub_6FFDE0 uses S2C 11. Neither client reads a
    // field, so both families send the zero-length body the table states, not a code-side assumption.
    public async ValueTask SendAsync(ISession session, CharacterCreationOpened message, CancellationToken ct)
    {
        // kr-1256 sub_53D410 reads the content when constructing the stage; a later reply cannot repair its form.
        if (_configuration.HasCreationContent)
            await session.SendAsync(opcodes.S2C("gspRepDynamicConfig"), _configuration.CreationSnapshot(), ct).ConfigureAwait(false);
        await session.SendAsync(opcodes.S2C(ResolveSingleName(OpenNames, PacketDirection.ServerToClient, "creation opened")),
            Write("CharacterCreationOpened", new Dictionary<string, object?>(StringComparer.Ordinal)), ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, CharacterNameReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepCheckCreateUserName"),
            Write("CharacterNameReply", new() { ["result"] = message.Result, ["nickname"] = message.Nickname }), ct);

    public ValueTask SendAsync(ISession session, CharacterCreateReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepCreateUserInfo"),
            Write("CharacterCreateReply", new() { ["result"] = message.Result }), ct);

    internal Dictionary<string, object> Read(string structure, ReadOnlySpan<byte> payload) =>
        PacketCodec.ReadScalars(tables, build, structure, payload);

    internal byte[] Write(string structure, Dictionary<string, object?> values) =>
        PacketCodec.Write(tables, build, structure, values);

    private void ValidateLayouts()
    {
        // The two empty bodies are stated in the tables, so a build that puts a field in them fails here.
        PacketLayoutContract.RequireEmpty(tables, build, "CharacterCreationOpened", "Character creation");
        PacketLayoutContract.RequireEmpty(tables, build, "CharacterCreationLogin", "Character creation");
        // The reads cast nickname to a string and character_type to an int, and the writers size result and nickname,
        // so each consumed field must have that type, not only that name.
        PacketLayoutContract.RequireFields(tables, build, "CharacterNameRequest", "Character creation",
            ("nickname", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "CharacterNameReply", "Character creation",
            ("result", "u32"), ("nickname", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "CharacterCreateRequest", "Character creation",
            ("nickname", "wstr"), ("character_type", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "CharacterCreateReply", "Character creation",
            ("result", "u32"));
    }
}
