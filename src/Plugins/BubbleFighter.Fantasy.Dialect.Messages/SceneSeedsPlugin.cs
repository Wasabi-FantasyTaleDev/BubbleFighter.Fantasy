using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The native seed authority of a legacy relay: issues each seat's critical, defence and cancellation tables when its
/// round starts, with the critical chance drawn from the build's own CriticalHitTable, and keeps the tables a client
/// reports back for diagnostics only. The appliers drop a table whose actor does not exist yet, so the round flow
/// asks for the tables after the countdown, never at the scene handshake.
/// </summary>
[Plugin("dialect.scene-seeds", Description = "Native legacy relay seed authority.",
    Requires = [typeof(IPacketRouter), typeof(IOpcodeTableRegistry), typeof(IEventBus), typeof(IClientDataStore)],
    OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(INativeSceneSeedStore), typeof(IMessageSender<MatchSeedTablesRequested>)])]
public sealed class SceneSeedsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<SceneSeedsOptions>();
        var identity = context.Row.ClientBuild
            ?? throw new ConfigException("dialect.scene-seeds requires an explicit client_build.");
        var build = $"{identity.Region}-{identity.Major}";
        if (!string.Equals(options.Protocol, build + "/relay", StringComparison.Ordinal))
            throw new ConfigException("Native scene seeds require the owning client build's relay protocol.");

        var tables = PacketTables.Load(options.PacketsDirectory);
        if (!tables.CapabilityOf(build, "native_scene_seed_tables"))
            throw new ConfigException($"Build '{build}' does not support native scene seed tables.");
        PacketLayoutContract.RequireFields(tables, build, "RelayCriticalSeedTable", "Native scene seeds",
            ("entity_id", "u32"));
        if (tables.Resolve(build, "RelayCriticalSeedTable").Count(field => field.Type == "list<u32>") != 2)
            throw new ConfigException($"Build '{build}' must state two critical seed word vectors.");
        PacketLayoutContract.RequireFields(tables, build, "RelayDefenceSeedTable", "Native scene seeds",
            ("entity_id", "u32"), ("unknown_8", "list<u8>"));
        PacketLayoutContract.RequireFields(tables, build, "RelayCriticalSeedCancelTable", "Native scene seeds",
            ("entity_id", "u32"), ("unknown_8", "list<u32>"));
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Relay || opcodes.Profile.Id != build)
            throw new ConfigException("Native scene seeds require an opcode table for the owning relay connection.");
        var chances = CriticalHitChances.Load(context.Services.Get<IClientDataStore>(), options.CriticalHitTableClientPath,
            options.CriticalHitTableSheet, options.CriticalHitTableMaxBytes);

        var store = new SceneSeedStore(options.MaxSessions);
        var authority = new SceneSeedAuthority(options.Protocol, build, tables, opcodes.S2C("rspSendCrtSeedTable"),
            opcodes.S2C("rspSendDefSeedTable"), opcodes.S2C("rspSendCrtCancelSeedTable"), chances, options.MaxSessions,
            Random.Shared);
        context.Effect(context.Services.RegisterMany<INativeSceneSeedStore>(store));
        context.Effect(context.Services.RegisterMany<IMessageSender<MatchSeedTablesRequested>>(authority));
        context.Effect(context.Events.On<ConnectionClosedEvent>(eventArgs =>
        {
            store.Remove(eventArgs.Connection.Id);
            authority.Remove(eventArgs.Connection.Id);
        }));
        var router = context.Services.Get<IPacketRouter>();
        Register(NativeSceneSeedKind.Critical, "rcpCrtSeedTable");
        Register(NativeSceneSeedKind.Defence, "rcpDefSeedTable");
        Register(NativeSceneSeedKind.CriticalCancellation, "rcpCrtCnclSeedTable");
        return ValueTask.CompletedTask;

        // The client repacks what it was sent only when the relay asks (cn-324 sub_6FA120 cases 10/11/12); a report
        // must name the entity issued to that relay session and is never fed back into the tables.
        void Register(NativeSceneSeedKind kind, string name)
        {
            var opcode = opcodes.C2S(name);
            context.Effect(router.Register(options.Protocol, opcode, (session, packet, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (session.Profile.Id != build)
                    throw new InvalidOperationException("Native scene seed belongs to another client profile.");
                var table = SceneSeedWire.Read(tables, build, kind, packet.Payload.Span, options.ReadLimits);
                if (!authority.Issued(session.Id, table.EntityId, out var generation))
                    throw new PacketFormatException($"Native scene seed entity {table.EntityId} was not issued to relay session {session.Id}.", 0);
                store.Set(session.Id, generation, table);
                context.Logger.LogDebug("Session {SessionId} reported native {Kind} seed table for entity {EntityId} ({Words} words, {Bytes} bytes).",
                    session.Id, kind, table.EntityId, table.WordValues.Count, table.ByteValues.Count);
                return ValueTask.CompletedTask;
            }));
        }
    }
}

/// <summary>Encodes a native seed table without choosing or changing its values.</summary>
internal static class SceneSeedSender
{
    /// <summary>Encodes one seed table according to the selected build's audited field order.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The client build receiving the table.</param>
    /// <param name="table">The values to encode.</param>
    /// <param name="structure">The packet structure selected for the table kind.</param>
    /// <returns>The packet body without its framing header.</returns>
    internal static byte[] Encode(PacketTables tables, string build, NativeSceneSeedTable table, string structure)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entity_id"] = table.EntityId,
        };
        var wordIndex = 0;
        var byteAssigned = false;
        foreach (var field in tables.Resolve(build, structure))
        {
            if (field.Type == "list<u8>")
            {
                if (byteAssigned) throw new InvalidOperationException($"'{structure}' has multiple byte vectors.");
                values[field.Name] = table.ByteValues.ToArray();
                byteAssigned = true;
            }
            else if (field.Type == "list<u32>")
            {
                if (wordIndex >= table.WordVectors.Count)
                    throw new InvalidOperationException($"Native seed table '{structure}' is missing vector {wordIndex}.");
                values[field.Name] = table.WordVectors[wordIndex++].ToArray();
            }
        }
        if (wordIndex != table.WordVectors.Count || (table.ByteValues.Count > 0 && !byteAssigned))
            throw new InvalidOperationException($"Native seed table '{structure}' has values for a different build shape.");
        return PacketCodec.Write(tables, build, structure, values);
    }
}

/// <summary>Bounds the native seed authority of one legacy relay protocol and names its client data.</summary>
public sealed class SceneSeedsOptions
{
    /// <summary>Gets or sets the owning build's relay protocol.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/relay";

    /// <summary>Gets or sets the directory containing the native seed packet layouts.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";

    /// <summary>Gets or sets the maximum number of simultaneous relay sessions retaining seed state.</summary>
    [Range(1, 65536)]
    public int MaxSessions { get; set; } = 1024;

    /// <summary>Gets or sets the maximum number of values accepted in one native seed vector.</summary>
    [Range(1, 4096)]
    public int MaxElements { get; set; } = 1000;

    /// <summary>Gets or sets the client path of the workbook whose category chances set the critical bits.</summary>
    [Required, StringLength(260, MinimumLength = 1)]
    public string CriticalHitTableClientPath { get; set; } = "Config/CriticalHitTable.xml";

    /// <summary>Gets or sets the worksheet of that workbook holding the category rows.</summary>
    [Required, StringLength(64, MinimumLength = 1)]
    public string CriticalHitTableSheet { get; set; } = "CriticalHitTable";

    /// <summary>Gets or sets the inclusive size cap of that workbook in bytes.</summary>
    [Range(1, 16777216)]
    public int CriticalHitTableMaxBytes { get; set; } = 1048576;

    /// <summary>Gets the bounded packet-tree limits used for all three native seed structures.</summary>
    internal PacketReadLimits ReadLimits => new(MaxCollectionElements: MaxElements,
        MaxValues: checked(MaxElements * 2 + 8));
}
