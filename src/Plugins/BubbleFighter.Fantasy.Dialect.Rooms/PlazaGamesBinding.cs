using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The plaza booths (basketball, fishing, fortune) of <c>packets/lobby-minigame.yaml</c>: the request and animation
/// packets in, the reply, the other players' animation steps and the day's record out. The request and the reply
/// start with the client's u16 subcommand (kr-1256 <c>sub_853CF0</c>, <c>sub_841B40</c>; kr-812 <c>sub_677210</c>
/// through <c>sub_67D950</c>); the reply's result codes
/// are the build's <c>plaza_game_results</c> in <c>packets/builds.yaml</c>. Each request carries the price the build
/// publishes for it in the mini-game configuration (dynamic-config slot 3), the price its client shows; what a booth
/// pays out is the round flow's decision.
/// </summary>
internal sealed class PlazaGamesBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<PlazaGameRequested>, IInboundMessageBinding<PlazaGameSyncRequested>,
      IInboundMessageBinding<PlazaGameRecordRequested>, IMessageSender<PlazaGameReplied>, IMessageSender<PlazaGameSyncNotified>, IMessageSender<PlazaGameRecordPublished>
{
    private const string RequestName = "gcpLmgRequest_";
    private const string ReplyName = "gspLmgReply_";
    private const string SyncName = "gcpLmgSync_";
    private const string SyncNoticeName = "gspLmgSyncState_";
    private const string RecordName = "gspRepLmgUserRecord";
    private const string RecordRequestName = "gcpReqLmgUserRecord";

    // The @LmgSync keys the client registers (kr-1256 sub_83E1C0): ShootFire, ChangeState, CastFishing, LiftFishing.
    private static readonly int[] SyncKeys = [100, 101, 200, 201];

    private IReadOnlyDictionary<PlazaGameOutcome, uint> _results = new Dictionary<PlazaGameOutcome, uint>();
    private PlazaGamePrices _prices = new(0, 0, 0, 0);

    /// <summary>Gets the game connection whose opcode table this binding uses.</summary>
    public string Protocol => protocol;

    /// <summary>Validates the layouts and result codes, then registers the routes and senders.</summary>
    internal void Mount(IPluginContext context)
    {
        RequireLayouts(packets, build);
        _results = ReadResults(packets, build);
        _prices = ReadPrices(packets, build);
        _ = table.S2C(ReplyName);
        _ = table.S2C(SyncNoticeName);
        _ = table.S2C(RecordName);
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        var router = services.Get<IPacketRouter>();
        context.Effect(router.Register(protocol, table.C2S(RequestName), async (session, packet, ct) =>
        {
            var request = ReadRequest(packets, build, _prices, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No plaza game handler is installed.");
        }));
        context.Effect(router.Register(protocol, table.C2S(SyncName), async (session, packet, ct) =>
        {
            var request = ReadSync(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No plaza game animation handler is installed.");
        }));
        // kr-812 asks for the day's record itself (kr-812 sub_471530); the modern builds have no such request and are
        // sent the record on plaza entry.
        if (table.Find(PacketDirection.ClientToServer, RecordRequestName) is { } recordRequest)
        {
            context.Effect(router.Register(protocol, recordRequest, async (session, packet, ct) =>
            {
                if (!packet.Payload.IsEmpty)
                    throw new PacketFormatException("A plaza booth record request has no body.", packet.Payload.Length);
                if (!await messages.DispatchAsync(protocol, session, new PlazaGameRecordRequested(), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No plaza game record handler is installed.");
            }));
            context.Effect(services.RegisterMany<IInboundMessageBinding<PlazaGameRecordRequested>>(this));
        }
        context.Effect(services.RegisterMany<IInboundMessageBinding<PlazaGameRequested>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<PlazaGameSyncRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<PlazaGameReplied>>(this));
        context.Effect(services.RegisterMany<IMessageSender<PlazaGameSyncNotified>>(this));
        context.Effect(services.RegisterMany<IMessageSender<PlazaGameRecordPublished>>(this));
        logger.LogInformation("dialect.rooms: plaza booths of {Build} mounted for {Protocol}.", build, protocol);
    }

    /// <summary>Writes a booth reply to the requesting player.</summary>
    public ValueTask SendAsync(ISession session, PlazaGameReplied message, CancellationToken ct) =>
        session.SendAsync(table.S2C(ReplyName), WriteReply(packets, build, _results, message), ct);

    /// <summary>Writes another player's animation step.</summary>
    public ValueTask SendAsync(ISession session, PlazaGameSyncNotified message, CancellationToken ct) =>
        session.SendAsync(table.S2C(SyncNoticeName), PacketCodec.Write(packets, build, "LmgSyncNotice", new Dictionary<string, object?>
        {
            ["gamer"] = message.UserNumber, ["subcmd"] = RequireSyncKey(message.Subcommand), ["value"] = message.Value,
        }), ct);

    /// <summary>Writes the day's record.</summary>
    public ValueTask SendAsync(ISession session, PlazaGameRecordPublished message, CancellationToken ct) =>
        session.SendAsync(table.S2C(RecordName), WriteRecord(packets, build, message), ct);

    /// <summary>Requires every structure the booths use to exist for the build with its audited fields.</summary>
    internal static void RequireLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireFields(packets, build, "LmgBasketballRequest", "Plaza basketball",
            ("subcmd", "u16"), ("displayed_cost", "u16"), ("hit", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "LmgFishingStartRequest", "Plaza fishing",
            ("subcmd", "u16"), ("second_price", "u32"), ("displayed_cost", "u16"), ("completed_count", "u16"));
        PacketLayoutContract.RequireFields(packets, build, "LmgRodLiftRequest", "Plaza fishing",
            ("subcmd", "u16"), ("second_price", "u32"), ("displayed_cost", "u16"), ("completed_count", "u16"),
            ("hooked", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "LmgFortuneRequest", "Plaza fortune",
            ("subcmd", "u16"), ("displayed_cost", "u16"), ("paper", "u64"));
        PacketLayoutContract.RequireFields(packets, build, "LmgSyncAction", "Plaza animation", ("subcmd", "u16"), ("unknown_6", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "LmgSyncNotice", "Plaza animation",
            ("gamer", "u32"), ("subcmd", "u16"), ("value", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "LmgReplyResult", "Plaza reply", ("subcmd", "u16"), ("result", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "LmgUserRecord", "Plaza record", ("fishing_count", "u16"), ("paper", "u64"));
    }

    /// <summary>Reads the build's reply result code of every outcome; a missing or negative code fails the mount.</summary>
    internal static IReadOnlyDictionary<PlazaGameOutcome, uint> ReadResults(PacketTables packets, string build)
    {
        if (packets.ValuesOf(build).GetValueOrDefault("plaza_game_results") is not IReadOnlyDictionary<string, object?> values)
            throw new ConfigException($"Build '{build}' needs plaza_game_results for its plaza booths.");
        var results = new Dictionary<PlazaGameOutcome, uint>();
        foreach (var outcome in Enum.GetValues<PlazaGameOutcome>())
        {
            var key = JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.ToString());
            if (!values.TryGetValue(key, out var raw) || !uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                throw new ConfigException($"Build '{build}' plaza_game_results.{key} must be an unsigned result code.");
            results.Add(outcome, code);
        }
        return results;
    }

    /// <summary>
    /// Reads the booth prices the build publishes in dynamic-config slot 3; a build with booths but no published
    /// prices fails the mount, since its clients would show prices the server cannot know.
    /// </summary>
    internal static PlazaGamePrices ReadPrices(PacketTables packets, string build)
    {
        var content = DynamicConfigContent.PublishedSlot(packets, build, 3, "LobbyMinigameContext")
            ?? throw new ConfigException($"Build '{build}' publishes no plaza mini-game configuration (dynamic_config slot 3), so its booths have no prices.");
        int Price(string key) => content.GetValueOrDefault(key) is { } raw
            && int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var price)
            && price <= ushort.MaxValue
                ? price : throw new ConfigException($"Build '{build}' dynamic_config slot 3 needs {key} as a u16 price.");
        return new(Price("basketball_cost"), Price("fishing_cost"), Price("fishing_second_cost"), Price("fortune_cost"));
    }

    /// <summary>Reads a booth request: the subcommand selects the body (kr-1256 <c>sub_841B40</c>'s keys).</summary>
    internal static PlazaGameRequested ReadRequest(PacketTables packets, string build, PlazaGamePrices prices, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            throw new PacketFormatException("A plaza booth request needs its subcommand.", payload.Length);
        var subcommand = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        switch ((PlazaGame)subcommand)
        {
            case PlazaGame.Basketball:
            {
                var values = PacketCodec.ReadScalars(packets, build, "LmgBasketballRequest", payload);
                return new(PlazaGame.Basketball, Convert.ToInt32(values["displayed_cost"], CultureInfo.InvariantCulture))
                {
                    Hit = Convert.ToUInt32(values["hit"], CultureInfo.InvariantCulture) != 0,
                    PublishedCost = prices.Basketball,
                };
            }
            case PlazaGame.FishingStart:
            {
                var values = PacketCodec.ReadScalars(packets, build, "LmgFishingStartRequest", payload);
                return new(PlazaGame.FishingStart, Convert.ToInt32(values["displayed_cost"], CultureInfo.InvariantCulture))
                {
                    PublishedCost = Convert.ToUInt32(values["second_price"], CultureInfo.InvariantCulture) != 0
                        ? prices.FishingSecond : prices.Fishing,
                };
            }
            case PlazaGame.RodLift:
            {
                var values = PacketCodec.ReadScalars(packets, build, "LmgRodLiftRequest", payload);
                return new(PlazaGame.RodLift, Convert.ToInt32(values["displayed_cost"], CultureInfo.InvariantCulture))
                {
                    Hooked = Convert.ToUInt32(values["hooked"], CultureInfo.InvariantCulture) != 0,
                };
            }
            case PlazaGame.Fortune:
            {
                var values = PacketCodec.ReadScalars(packets, build, "LmgFortuneRequest", payload);
                return new(PlazaGame.Fortune, Convert.ToInt32(values["displayed_cost"], CultureInfo.InvariantCulture))
                {
                    Paper = Convert.ToUInt64(values["paper"], CultureInfo.InvariantCulture),
                    PublishedCost = prices.Fortune,
                };
            }
            default:
                throw new PacketFormatException($"Plaza booth subcommand {subcommand} is not one the client registers.", 0);
        }
    }

    /// <summary>Reads an animation step; only the client's four <c>@LmgSync</c> keys are accepted.</summary>
    internal static PlazaGameSyncRequested ReadSync(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(packets, build, "LmgSyncAction", payload);
        var key = Convert.ToInt32(values["subcmd"], CultureInfo.InvariantCulture);
        if (!SyncKeys.Contains(key))
            throw new PacketFormatException($"Plaza animation subcommand {key} is not one the client registers.", 0);
        return new(key, Convert.ToUInt32(values["unknown_6"], CultureInfo.InvariantCulture));
    }

    /// <summary>Writes the reply branch the outcome needs, with the build's result code.</summary>
    internal static byte[] WriteReply(PacketTables packets, string build, IReadOnlyDictionary<PlazaGameOutcome, uint> results,
        PlazaGameReplied message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!results.TryGetValue(message.Outcome, out var code))
            throw new InvalidDataException($"Plaza outcome {message.Outcome} has no result code for {build}.");
        var expectedItems = message.Outcome switch
        {
            PlazaGameOutcome.BasketballItem or PlazaGameOutcome.CatchOneItem or PlazaGameOutcome.FortuneItem => 1,
            PlazaGameOutcome.CatchTwoItems => 2,
            _ => 0,
        };
        if (message.Items.Count != expectedItems || message.Items.Any(item => item <= 0))
            throw new InvalidDataException($"Plaza outcome {message.Outcome} carries {expectedItems} positive item id(s).");
        if (!Allowed(message.Game, message.Outcome))
            throw new InvalidDataException($"Plaza booth {message.Game} has no {message.Outcome} reply.");
        var wallet = (uint)Math.Clamp(message.Wallet, 0, uint.MaxValue);
        var fields = new Dictionary<string, object?> { ["subcmd"] = (int)message.Game, ["result"] = code };
        string structure;
        switch (message.Outcome)
        {
            case PlazaGameOutcome.Refused or PlazaGameOutcome.FishingStarted when message.Game != PlazaGame.Fortune:
                structure = "LmgReplyResult";
                break;
            case PlazaGameOutcome.Refused or PlazaGameOutcome.FortunePaper:
                structure = "LmgFortuneReply";
                fields["paper"] = message.Paper;
                break;
            case PlazaGameOutcome.FortuneItem:
                structure = "LmgFortuneReplyItem";
                fields["paper"] = message.Paper;
                fields["wallet"] = wallet;
                fields["item_id"] = (uint)message.Items[0];
                break;
            case PlazaGameOutcome.BasketballMiss or PlazaGameOutcome.BasketballHit:
                structure = "LmgBasketballReplyWallet";
                fields["wallet"] = wallet;
                break;
            case PlazaGameOutcome.BasketballItem:
                structure = "LmgBasketballReplyItem";
                fields["wallet"] = wallet;
                fields["item_id"] = (uint)message.Items[0];
                break;
            default:
                structure = message.Outcome switch
                {
                    PlazaGameOutcome.Catch => "LmgRodLiftReplyCatch",
                    PlazaGameOutcome.CatchOneItem => "LmgRodLiftReplyOneItem",
                    _ => "LmgRodLiftReplyTwoItems",
                };
                fields["paid"] = (uint)Math.Max(0, message.Paid);
                fields["wallet"] = wallet;
                fields["fishing_count"] = Math.Clamp(message.FishingCount, 0, ushort.MaxValue);
                if (expectedItems > 0) fields["item_id"] = (uint)message.Items[0];
                if (expectedItems > 1) fields["item_id_2"] = (uint)message.Items[1];
                break;
        }
        return PacketCodec.Write(packets, build, structure, fields);
    }

    /// <summary>Writes the day's record body.</summary>
    internal static byte[] WriteRecord(PacketTables packets, string build, PlazaGameRecordPublished message) =>
        PacketCodec.Write(packets, build, "LmgUserRecord", new Dictionary<string, object?>
        {
            ["fishing_count"] = Math.Clamp(message.FishingCount, 0, ushort.MaxValue), ["paper"] = message.Paper,
        });

    // Each booth's reader understands only its own branches (kr-1256 sub_849EC0, sub_84A990, sub_84AD60, sub_84B7D0).
    private static bool Allowed(PlazaGame game, PlazaGameOutcome outcome) => outcome == PlazaGameOutcome.Refused || game switch
    {
        PlazaGame.Basketball => outcome is PlazaGameOutcome.BasketballMiss or PlazaGameOutcome.BasketballHit or PlazaGameOutcome.BasketballItem,
        PlazaGame.FishingStart => outcome is PlazaGameOutcome.FishingStarted,
        PlazaGame.RodLift => outcome is PlazaGameOutcome.Catch or PlazaGameOutcome.CatchOneItem or PlazaGameOutcome.CatchTwoItems,
        PlazaGame.Fortune => outcome is PlazaGameOutcome.FortunePaper or PlazaGameOutcome.FortuneItem,
        _ => false,
    };

    private static int RequireSyncKey(int key) => SyncKeys.Contains(key) ? key
        : throw new InvalidDataException($"Plaza animation subcommand {key} is not one the client registers.");
}

/// <summary>The booth prices a build publishes in its mini-game configuration (dynamic-config slot 3).</summary>
/// <param name="Basketball">One shot (<c>basketball_cost</c>).</param>
/// <param name="Fishing">One cast's base price (<c>fishing_cost</c>).</param>
/// <param name="FishingSecond">One cast's base price when the request sets <c>second_price</c>.</param>
/// <param name="Fortune">One fortune paper (<c>fortune_cost</c>).</param>
internal sealed record PlazaGamePrices(int Basketball, int Fishing, int FishingSecond, int Fortune);
