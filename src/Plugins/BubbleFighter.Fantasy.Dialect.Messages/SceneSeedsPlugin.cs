using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Receives audited legacy relay seed tables and retains them per connection. This row is
/// deliberately opt-in: it supplies scene state to a future initializer but does not broadcast
/// guessed values or enable a relay listener by itself.
/// </summary>
[Plugin("dialect.scene-seeds", Description = "Native legacy relay scene seed state.",
    Requires = [typeof(IPacketRouter), typeof(IOpcodeTableRegistry), typeof(IEventBus), typeof(IRoomRoster)],
    ProvidesMany = [typeof(INativeSceneSeedStore), typeof(IMessageSender<NativeSceneSeedTable>)])]
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
        _ = tables.Resolve(build, "RelayCriticalSeedTable");
        _ = tables.Resolve(build, "RelayDefenceSeedTable");
        _ = tables.Resolve(build, "RelayCriticalSeedCancelTable");
        // A build that states a seed table uses the actor id and the vector shapes the encoder writes; cn-279
        // states the tables empty and writes none.
        if (tables.Resolve(build, "RelayCriticalSeedTable").Count > 0)
            PacketLayoutContract.RequireFields(tables, build, "RelayCriticalSeedTable", "Native scene seeds",
                ("entity_id", "u32"), ("unknown_8", "list<u32>"), ("unknown_24", "list<u32>"));
        if (tables.Resolve(build, "RelayDefenceSeedTable").Count > 0)
            PacketLayoutContract.RequireFields(tables, build, "RelayDefenceSeedTable", "Native scene seeds",
                ("entity_id", "u32"), ("unknown_8", "list<u8>"));
        if (tables.Resolve(build, "RelayCriticalSeedCancelTable").Count > 0)
            PacketLayoutContract.RequireFields(tables, build, "RelayCriticalSeedCancelTable", "Native scene seeds",
                ("entity_id", "u32"), ("unknown_8", "list<u32>"));
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Relay || opcodes.Profile.Id != build)
            throw new ConfigException("Native scene seeds require an opcode table for the owning relay connection.");

        var store = new SceneSeedStore(options.MaxSessions);
        var roster = context.Services.Get<IRoomRoster>();
        context.Effect(context.Services.RegisterMany<INativeSceneSeedStore>(store));
        var sender = new SceneSeedSender(options.Protocol, build, tables, opcodes, roster);
        context.Effect(context.Services.RegisterMany<IMessageSender<NativeSceneSeedTable>>(sender));
        context.Effect(context.Events.On<ConnectionClosedEvent>(eventArgs => store.Remove(eventArgs.Connection.Id)));
        var router = context.Services.Get<IPacketRouter>();
        Register(NativeSceneSeedKind.Critical, "rcpCrtSeedTable");
        Register(NativeSceneSeedKind.Defence, "rcpDefSeedTable");
        Register(NativeSceneSeedKind.CriticalCancellation, "rcpCrtCnclSeedTable");
        return ValueTask.CompletedTask;

        void Register(NativeSceneSeedKind kind, string name)
        {
            var opcode = opcodes.C2S(name);
            context.Effect(router.Register(options.Protocol, opcode, (session, packet, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (session.Profile.Id != build)
                    throw new InvalidOperationException("Native scene seed belongs to another client profile.");
                var view = roster.FindBySession(session.Id)
                    ?? throw new InvalidOperationException("Native scene seed requires a relay session bound to a room.");
                var seat = view.Seats.FirstOrDefault(candidate => candidate.Relay?.Id == session.Id)
                    ?? throw new InvalidOperationException("Native scene seed relay is not bound to a room seat.");
                var table = SceneSeedWire.Read(tables, build, kind, packet.Payload.Span, options.ReadLimits);
                var expectedEntity = checked(1_000_000 + 100_000 * seat.Slot);
                if (table.EntityId != expectedEntity)
                    throw new PacketFormatException($"Native scene seed entity {table.EntityId} does not belong to relay seat {seat.Slot} (expected {expectedEntity}).", 0);
                store.Set(session.Id, view.Generation, table);
                context.Logger.LogDebug("Session {SessionId} reported native {Kind} seed table for entity {EntityId} ({Words} words, {Bytes} bytes).",
                    session.Id, kind, table.EntityId, table.WordValues.Count, table.ByteValues.Count);
                return ValueTask.CompletedTask;
            }));
        }
    }
}

/// <summary>Encodes a validated native seed table without choosing or changing its values.</summary>
internal sealed class SceneSeedSender(string protocol, string build, PacketTables tables, IOpcodeTable opcodes,
    IRoomRoster roster)
    : IMessageSender<NativeSceneSeedTable>
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, NativeSceneSeedTable table, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.Profile.Id != build)
            throw new InvalidOperationException("Native scene seed belongs to another client profile.");
        var view = roster.FindBySession(session.Id)
            ?? throw new InvalidOperationException("Native scene seed requires a relay session bound to a room.");
        var seat = view.Seats.FirstOrDefault(candidate => candidate.Relay?.Id == session.Id)
            ?? throw new InvalidOperationException("Native scene seed relay is not bound to a room seat.");
        var expectedEntity = checked(1_000_000 + 100_000 * seat.Slot);
        if (table.EntityId != expectedEntity)
            throw new InvalidOperationException($"Native scene seed entity {table.EntityId} does not belong to relay seat {seat.Slot} (expected {expectedEntity}).");
        var (structure, name) = table.Kind switch
        {
            NativeSceneSeedKind.Critical => ("RelayCriticalSeedTable", "rspSendCrtSeedTable"),
            NativeSceneSeedKind.Defence => ("RelayDefenceSeedTable", "rspSendDefSeedTable"),
            NativeSceneSeedKind.CriticalCancellation => ("RelayCriticalSeedCancelTable", "rspSendCrtCancelSeedTable"),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        var body = Encode(tables, build, table, structure);
        return session.SendAsync(opcodes.S2C(name), body, ct);
    }

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

/// <summary>Bounds the opt-in native scene seed receiver for one legacy relay protocol.</summary>
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

    /// <summary>Gets the bounded packet-tree limits used for all three native seed structures.</summary>
    internal PacketReadLimits ReadLimits => new(MaxCollectionElements: MaxElements,
        MaxValues: checked(MaxElements * 2 + 8));
}
