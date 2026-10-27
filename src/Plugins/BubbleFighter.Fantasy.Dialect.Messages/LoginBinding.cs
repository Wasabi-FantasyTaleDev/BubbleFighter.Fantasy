using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The one login adapter every build shares: it decodes <c>gcpReqLogin</c> with the build's shape flags and
/// writes the whole post-login sequence as the build's <c>login.burst</c> list states it
/// (<c>packets/builds.yaml</c>). The packets, their order and their values differ per build only as data; the
/// code switches on the named burst steps and on the named reply and main-menu values.
/// </summary>
internal sealed class LoginBinding : IInboundMessageBinding<LoginRequested>, IMessageSender<LoginAccepted>,
    IMessageSender<LoginRejected>, IMessageSender<LoginSucceeded>
{
    private readonly string _protocol;
    private readonly LoginValues _values;
    private readonly ushort _replyOpcode;
    private readonly IOpcodeTable _opcodes;
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly IServiceRegistry _services;
    private readonly IEventBus _events;
    private readonly InventoryBinding? _inventory;
    private readonly ExpiredItemsBinding? _expired;
    private readonly ILogger _logger;
    private readonly ushort? _statOpcode;
    private readonly ushort? _moneyOpcode;
    private readonly ushort? _pcroomOpcode;
    private readonly ushort? _centerListOpcode;
    private readonly ushort? _updatePartyOpcode;
    private readonly ushort? _optionOpcode;
    private readonly ushort? _firstMoveChannelOpcode;
    private readonly ushort? _gotoRoomOpcode;

    /// <summary>
    /// Resolves every packet the build's burst names and checks its structures at activation, so a row that
    /// cannot carry out its stated sequence fails at load instead of midway through a login.
    /// </summary>
    /// <param name="protocol">The game connection protocol.</param>
    /// <param name="values">The build's login values.</param>
    /// <param name="replyOpcode">The profile's <c>gspRepLogin</c> number.</param>
    /// <param name="opcodes">The profile's opcode table (game connection).</param>
    /// <param name="tables">The loaded packet tables.</param>
    /// <param name="build">The build the tables resolve structures for.</param>
    /// <param name="services">The row's services, for the optional main-menu room adapter.</param>
    /// <param name="events">The row's event bus, for the login hooks.</param>
    /// <param name="inventory">The bag publication when the row carries the inventory option.</param>
    /// <param name="expired">The expired-item notices when the row publishes them.</param>
    /// <param name="logger">The row's logger.</param>
    /// <exception cref="ConfigException">A burst step cannot be carried out for this build.</exception>
    internal LoginBinding(string protocol, LoginValues values, ushort replyOpcode, IOpcodeTable opcodes,
        PacketTables tables, string build, IServiceRegistry services, IEventBus events, InventoryBinding? inventory,
        ExpiredItemsBinding? expired, ILogger logger)
    {
        _protocol = protocol;
        _values = values;
        _replyOpcode = replyOpcode;
        _opcodes = opcodes;
        _tables = tables;
        _build = build;
        _services = services;
        _events = events;
        _inventory = inventory;
        _expired = expired;
        _logger = logger;
        LoginMessageCodec.RequireLayouts(tables, build, values);
        foreach (var step in values.Burst)
        {
            switch (step)
            {
                case LoginBurstStep.UserStat:
                    if (values.Shape.LoginResponse)
                        throw new ConfigException(
                            $"'{protocol}' states a response-pair login and lists user_stat; that reply already carries the user state.");
                    _statOpcode = opcodes.S2C("gspUserStat");
                    break;
                case LoginBurstStep.UserMoneyStat:
                    _ = tables.Resolve(build, "UserMoneyStat");
                    _moneyOpcode = opcodes.S2C("gspUserMoneyStat");
                    break;
                case LoginBurstStep.UserPcroomStat:
                    _ = tables.Resolve(build, "UserPcroomStat");
                    _pcroomOpcode = opcodes.S2C("gspUserPcroomStat");
                    break;
                case LoginBurstStep.CenterList:
                    if (tables.Resolve(build, "gspCenterList").Count == 0)
                        throw new ConfigException(
                            $"'{protocol}' lists the center list but build '{build}' has no count-first gspCenterList layout.");
                    _centerListOpcode = opcodes.S2C("gspCenterList");
                    break;
                case LoginBurstStep.UpdateParty:
                    LoginPartySnapshot.RequireLayouts(tables, build);
                    _updatePartyOpcode = opcodes.S2C("gspUpdateParty");
                    break;
                case LoginBurstStep.OptionSync:
                    _optionOpcode = opcodes.S2C("gspRepOption");
                    break;
                case LoginBurstStep.Inventory:
                    // The bag publication is the row's inventory option; without it the step is skipped at send.
                    break;
                case LoginBurstStep.MainMenu:
                    if (values.MainMenu == LoginValues.TableMainMenu)
                    {
                        MainMenuInstall.RequireLayouts(tables, build);
                        _gotoRoomOpcode = opcodes.S2C("gspGotoRoom");
                    }
                    break;
                case LoginBurstStep.FirstMoveChannel:
                    _firstMoveChannelOpcode = opcodes.S2C("gspNotifyFirstMoveChannel");
                    break;
                case LoginBurstStep.LoginReply:
                case LoginBurstStep.EquipmentView:
                case LoginBurstStep.ExpiredItems:
                    break;
            }
        }
    }

    /// <inheritdoc />
    public string Protocol => _protocol;

    /// <summary>Writes the accepted reply with the build's reply values (table form or response pair).</summary>
    public ValueTask SendAsync(ISession session, LoginAccepted message, CancellationToken ct) =>
        session.SendAsync(_replyOpcode, AcceptedBody(session, message), ct);

    /// <summary>
    /// Writes the complete login sequence the build's burst list states: reply and user state first, then the
    /// wallet and room counters, the main menu and its supporting publications. The last publication is the
    /// <see cref="LoginMenuInstalled"/> hook, which a rejected or faulted login never reaches.
    /// </summary>
    public async ValueTask SendAsync(ISession session, LoginSucceeded login, CancellationToken ct)
    {
        if (login.Wallet is not null && login.Wallet.UserNum != login.UserNumber)
            throw new InvalidOperationException("Login publication contains another player's wallet.");
        var accepted = new LoginAccepted(login.UserNumber, login.Account, login.Nickname, login.ServerTime, login.CharacterItemId);
        var lucci = login.Wallet?.Lucci ?? 0;
        var cash = login.Wallet?.Cash ?? 0;
        foreach (var step in _values.Burst)
        {
            switch (step)
            {
                case LoginBurstStep.LoginReply:
                    await SendAsync(session, accepted, ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.UserStat:
                    await session.SendAsync(_statOpcode!.Value, UserStatWire.Write(_tables, _build, login.Nickname,
                        login.Level, lucci, cash, login.ColorIndex, login.UserNumber), ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.UserMoneyStat:
                    await session.SendAsync(_moneyOpcode!.Value,
                        LoginBurstWire.WriteUserMoneyStat(_tables, _build, lucci, cash), ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.UserPcroomStat:
                    await session.SendAsync(_pcroomOpcode!.Value,
                        LoginBurstWire.WriteUserPcroomStat(_tables, _build), ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.CenterList:
                    await session.SendAsync(_centerListOpcode!.Value, LoginBurstWire.WriteCenterListEmpty(_tables, _build), ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.UpdateParty:
                    await session.SendAsync(_updatePartyOpcode!.Value, LoginPartySnapshot.Write(_tables, _build,
                        login.UserNumber, login.Nickname, login.CharacterItemId), ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.OptionSync:
                    await session.SendAsync(_optionOpcode!.Value, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.Inventory:
                    await SendInventoryAsync(session, login, ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.MainMenu:
                    await SendMainMenuAsync(session, login, ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.EquipmentView:
                    // The inventory-view row publishes the equipped view through this awaited scene boundary,
                    // after the menu install and before the appearance publications.
                    await _events.WaterfallAsync<LoginEquipmentRequested, bool>(
                        new LoginEquipmentRequested(_protocol, session, login, login.CharacterItemId),
                        static _ => ValueTask.FromResult(true), ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.FirstMoveChannel:
                    // The client reads nothing from S2C 163: dispatch case 163 in kr-1256 sub_9D2FF0 stores
                    // the label and sets the session flag byte_18E55CF, so the frame is sent empty.
                    await session.SendAsync(_firstMoveChannelOpcode!.Value, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    break;
                case LoginBurstStep.ExpiredItems:
                    await SendExpiredAsync(session, login, ct).ConfigureAwait(false);
                    break;
            }
        }
        await _events.SerialAsync(new LoginMenuInstalled(_protocol, session, login), ct).ConfigureAwait(false);
    }

    /// <summary>Writes the refused reply with the build's refusal values (table form or response pair).</summary>
    public ValueTask SendAsync(ISession session, LoginRejected message, CancellationToken ct) =>
        session.SendAsync(_replyOpcode, _values.Shape.LoginResponse
            ? LoginMessageCodec.WriteModernRejected(message)
            : _values.Reply == LoginValues.ModernReply
                ? LoginMessageCodec.WriteTableModernRejected(message, _tables, _build)
                : LoginMessageCodec.WriteRejected(message, _values.Shape, _tables, _build), ct);

    private byte[] AcceptedBody(ISession session, LoginAccepted accepted) => _values.Reply switch
    {
        LoginValues.ModernReply => LoginMessageCodec.WriteTableModernAccepted(accepted, $"bff-{session.Id}",
            _tables, _build, _values.ChannelId),
        _ => _values.Shape.LoginResponse
            ? LoginMessageCodec.WriteModernAccepted(accepted)
            : LoginMessageCodec.WriteAccepted(accepted, _values.Shape, _tables, _build),
    };

    // The bag publication walks the build's inventory.replaced list. A modern row that has no stored bag
    // publishes the starter rows, as the modern login codec did; a legacy row without a store sends nothing.
    private async ValueTask SendInventoryAsync(ISession session, LoginSucceeded login, CancellationToken ct)
    {
        if (_inventory is null)
        {
            _logger.LogInformation("Session {Id}: '{Protocol}' has no bag publication; the login burst skips the inventory step.",
                session.Id, _protocol);
            return;
        }
        var character = login.CharacterBag;
        var equipped = login.EquippedBag;
        if (character is null || equipped is null)
        {
            if (_values.Reply != LoginValues.ModernReply)
                return;
            var now = DateTimeOffset.UtcNow;
            character ??= StarterInventory.Slots(0, InventoryBag.CharacterBag, now);
            equipped ??= StarterInventory.Slots(0, InventoryBag.EquippedBag, now);
        }
        await _inventory.SendAsync(session, new InventoryReplaced(character, equipped), ct).ConfigureAwait(false);
    }

    // The main menu install is data-selected: the modern family writes it from the packet tables (its readers
    // are kr-1256 sub_10D4520, cn-774 sub_BAB9F0 and tw-454 sub_A89100), the legacy builds ask their room
    // adapter, which may activate after this row. A room adapter that is not mounted leaves the login ending at
    // the reply, as before.
    private async ValueTask SendMainMenuAsync(ISession session, LoginSucceeded login, CancellationToken ct)
    {
        if (_values.MainMenu == LoginValues.TableMainMenu)
        {
            await session.SendAsync(_gotoRoomOpcode!.Value, MainMenuInstall.Write(_tables, _build, login.UserNumber,
                login.Nickname, login.Level, login.CharacterItemId), ct).ConfigureAwait(false);
            return;
        }
        var senders = _services.GetAll<IMessageSender<MainMenuRoomOpened>>()
            .Where(sender => sender.Protocol == _protocol).ToArray();
        if (senders.Length > 1)
            throw new ConfigException($"'{_protocol}' has {senders.Length} IMessageSender<MainMenuRoomOpened> rows; mount one.");
        if (senders.Length == 0)
        {
            _logger.LogInformation("Session {Id}: '{Protocol}' has no main-menu room codec; login ends at the reply.",
                session.Id, _protocol);
            return;
        }
        await senders[0].SendAsync(session, new MainMenuRoomOpened(login.UserNumber, login.Nickname,
            login.CharacterItemId, login.EquippedItemIds) { OfferTutorial = login.OfferTutorial, ColorIndex = login.ColorIndex }, ct)
            .ConfigureAwait(false);
    }

    // The notice follows the menu; the bag above already lacks the expired rows.
    private async ValueTask SendExpiredAsync(ISession session, LoginSucceeded login, CancellationToken ct)
    {
        if (_expired is null || login.ExpiredItemIds.Count == 0)
            return;
        await _expired.SendAsync(session, new ItemsExpired(login.ExpiredItemIds), ct).ConfigureAwait(false);
    }
}
