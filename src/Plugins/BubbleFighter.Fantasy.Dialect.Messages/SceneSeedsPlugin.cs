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
/// The native seed authority of a relay: issues each seat's seed tables when its round starts and keeps the tables a
/// client reports back for diagnostics only. A build with the full seed exchange takes its critical, defence and
/// cancellation tables from here, the critical chance drawn from its own CriticalHitTable; a build with only the
/// ordinary-defence capability takes only the defence tables. The appliers drop a table whose actor does not
/// exist yet, so the round flow asks for the tables after the countdown, never at the scene handshake.
/// </summary>
[Plugin("dialect.scene-seeds", Description = "Native relay seed authority.",
    Requires = [typeof(IPacketRouter), typeof(IOpcodeTableRegistry), typeof(IEventBus), typeof(IClientDataStore)],
    OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(INativeSceneSeedStore), typeof(IMessageSender<MatchSeedTablesRequested>), typeof(IMatchSeedInputValidator)],
    ConditionalProvidesMany = [typeof(IMatchCriticalSeedAuthority)])]
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
        // The full exchange or independently audited ordinary-defence tables (cn-705 sub_50C020,
        // cn-706 sub_50CAF0 and kr-1256 sub_483620) can mount without the other seed families.
        var seeded = tables.CapabilityOf(build, "native_scene_seed_tables");
        var defended = tables.CapabilityOf(build, "native_defence_seed_tables");
        if (!seeded && !(defended && options.DefenceSeeds))
            throw new ConfigException($"Build '{build}' takes no seed tables from this authority; its defence needs defence_seeds.");
        if (options.DefenceSeeds && !defended)
            throw new ConfigException($"Build '{build}' does not read its ordinary defence from server tables.");
        if (options.WeaponCriticalBonuses && !seeded)
            throw new ConfigException($"Build '{build}' takes no critical tables, so weapon_critical_bonuses cannot apply.");
        PacketLayoutContract.RequireFields(tables, build, "RelayDefenceSeedTable", "Native scene seeds",
            ("entity_id", "u32"), ("unknown_8", "list<u8>"));
        if (seeded)
        {
            PacketLayoutContract.RequireFields(tables, build, "RelayCriticalSeedTable", "Native scene seeds",
                ("entity_id", "u32"));
            if (tables.Resolve(build, "RelayCriticalSeedTable").Count(field => field.Type == "list<u32>") != 2)
                throw new ConfigException($"Build '{build}' must state two critical seed word vectors.");
            PacketLayoutContract.RequireFields(tables, build, "RelayCriticalSeedCancelTable", "Native scene seeds",
                ("entity_id", "u32"), ("unknown_8", "list<u32>"));
        }
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Relay || opcodes.Profile.Id != build)
            throw new ConfigException("Native scene seeds require an opcode table for the owning relay connection.");
        var data = context.Services.Get<IClientDataStore>();
        var chances = seeded
            ? CriticalHitChances.Load(data, options.CriticalHitTableClientPath, options.CriticalHitTableSheet,
                options.CriticalHitTableMaxBytes)
            : null;
        var weapons = options.WeaponCriticalBonuses ? WeaponCriticalCatalog.Load(data, tables, build, options) : null;
        var defences = options.DefenceSeeds
            ? DefenceSeedCatalog.Load(data, tables, build, options.DefenceItemsClientPath, options.DefenceWorkbookMaxBytes,
                "defence_workbook_max_bytes")
            : null;

        var store = new SceneSeedStore(options.MaxSessions);
        var authority = new SceneSeedAuthority(options.Protocol, build, tables,
            seeded ? opcodes.S2C("rspSendCrtSeedTable") : null, opcodes.S2C("rspSendDefSeedTable"),
            seeded ? opcodes.S2C("rspSendCrtCancelSeedTable") : null, chances, options.MaxSessions, Random.Shared,
            weapons, defences);
        context.Effect(context.Services.RegisterMany<INativeSceneSeedStore>(store));
        context.Effect(context.Services.RegisterMany<IMessageSender<MatchSeedTablesRequested>>(authority));
        context.Effect(context.Services.RegisterMany<IMatchSeedInputValidator>(authority));
        if (weapons is not null)
            context.Effect(context.Services.RegisterMany<IMatchCriticalSeedAuthority>(authority));
        context.Effect(context.Events.On<ConnectionClosedEvent>(eventArgs =>
        {
            store.Remove(eventArgs.Connection.Id);
            authority.Remove(eventArgs.Connection.Id);
        }));
        var router = context.Services.Get<IPacketRouter>();
        // Only builds audited for the full exchange mount the seed report routes.
        if (seeded)
        {
            Register(NativeSceneSeedKind.Critical, "rcpCrtSeedTable");
            Register(NativeSceneSeedKind.Defence, "rcpDefSeedTable");
            Register(NativeSceneSeedKind.CriticalCancellation, "rcpCrtCnclSeedTable");
        }
        if (tables.CapabilityOf(build, "native_relay_hp_reports"))
            RelayHpReports.Mount(context, router, options.Protocol, build, tables, opcodes);
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
                if (!authority.Issued(session.Id, kind, table.EntityId, out var generation))
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

    /// <summary>Gets or sets whether audited seeded clients include native weapon and equipment contributions and accept live critical revisions.</summary>
    public bool WeaponCriticalBonuses { get; set; }

    /// <summary>Gets or sets the owning client's category-master workbook used by the optional weapon resolver.</summary>
    [Required, StringLength(260, MinimumLength = 1)]
    public string WeaponMasterClientPath { get; set; } = "Config/WeaponSpecMaster.xml";

    /// <summary>Gets or sets the owning client's specification-detail workbook used by the optional weapon resolver.</summary>
    [Required, StringLength(260, MinimumLength = 1)]
    public string WeaponDetailClientPath { get; set; } = "Config/WeaponSpecDetail.xml";

    /// <summary>Gets or sets the item workbook whose native slotLocation selects primary and sidearm items.</summary>
    [Required, StringLength(260, MinimumLength = 1)]
    public string WeaponItemsClientPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the inclusive byte and XML-character bound for each weapon workbook.</summary>
    [Range(1, 67108864)]
    public int WeaponWorkbookMaxBytes { get; set; } = 16777216;

    /// <summary>Gets or sets the maximum native cell position, including sparse and merged columns.</summary>
    [Range(2, 65536)]
    public int WeaponWorkbookMaxColumns { get; set; } = 16384;

    /// <summary>Gets or sets the maximum rows per selected worksheet and retained weapon specifications.</summary>
    [Range(1, 65536)]
    public int WeaponCatalogMaxRecords { get; set; } = 32768;

    /// <summary>Gets or sets the largest accepted worn set when validating a seed-equipment snapshot.</summary>
    [Range(1, 4096)]
    public int WeaponMaxWornItems { get; set; } = 64;

    /// <summary>
    /// Gets or sets whether each round's defence tables carry every occupant's local equipment roll instead of zeros.
    /// Requires the build's audited <c>defence_seed</c> rules in <c>packets/builds.yaml</c>.
    /// </summary>
    public bool DefenceSeeds { get; set; }

    /// <summary>Gets or sets the item workbook whose ShopItemSpec rows hold the defence effects.</summary>
    [Required, StringLength(260, MinimumLength = 1)]
    public string DefenceItemsClientPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the inclusive byte bound of that workbook; the shipped builds' workbooks reach about 100 MB.</summary>
    [Range(1, int.MaxValue)]
    public int DefenceWorkbookMaxBytes { get; set; } = 268435456;

    /// <summary>Gets the bounded packet-tree limits used for all three native seed structures.</summary>
    internal PacketReadLimits ReadLimits => new(MaxCollectionElements: MaxElements,
        MaxValues: checked(MaxElements * 2 + 8));
}
