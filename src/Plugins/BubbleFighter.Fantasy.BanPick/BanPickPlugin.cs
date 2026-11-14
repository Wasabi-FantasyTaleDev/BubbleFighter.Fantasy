using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.BanPick;

/// <summary>
/// The server-wide ban-pick row: the policy document, the <c>banpick</c> / <c>bp</c> room command, the administrators'
/// <c>gbanpick</c> / <c>gbp</c> command and the Nirinsou Link overlay messages. Each build's <c>banpick.room</c> row
/// does the room work; without one, nothing is checked for that build. The feature is optional: disable this row and
/// every <c>banpick.room</c> row, or keep them and leave the document empty.
/// </summary>
[Plugin("banpick", Category = "gameplay", Names = ["en=Ban rules", "zh-Hans=禁用规则", "zh-Hant=禁用規則", "ko=금지 규칙"], Description = "Server-wide ban policies and room host bans, by commands or the Nirinsou Link overlay.",
    Requires = [typeof(ICommandRegistry), typeof(IPermissionRegistry), typeof(IGameText)],
    Optional = [typeof(ICompanionHub)],
    Provides = [typeof(IBanPickPolicyStore), typeof(IBanPickHub)])]
public sealed class BanPickPlugin : IPlugin
{
    /// <summary>The room command's permission; every player may use it, and only a room host may change the list.</summary>
    internal const string RoomPermission = "game.room.banpick";

    /// <summary>The administrators' command permission.</summary>
    internal const string AdminPermission = "game.management.banpick";

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<BanPickOptions>();
        var store = new BanPickPolicyStore(options.Policy.ToContract(), options.StatePath, options.MaxStateBytes);
        context.Services.TryGet<ICompanionHub>(out var companion);
        var hub = new BanPickHub(new(options.AnnouncementItems, options.ReclickWindowMs, options.ConfirmTimeoutMs,
            options.MaxRoomEntries, options.SearchResults), store, companion);
        var texts = context.Services.Get<IGameText>();
        context.Effect(context.Services.Register<IBanPickPolicyStore>(store));
        context.Effect(context.Services.Register<IBanPickHub>(hub));

        var permissions = context.Services.Get<IPermissionRegistry>();
        var commands = context.Services.Get<ICommandRegistry>();
        const string roomDescription = "Show this room's ban list; the host adds, removes or clears bans on items, characters, pets and groups.";
        context.Effect(permissions.Register(new(RoomPermission, roomDescription, true)));
        context.Effect(commands.Register(context, new("banpick", ["bp"], RoomPermission, roomDescription,
            [new("arguments", CommandArgumentKind.RemainingText, Optional: true)]), async (invocation, ct) =>
            {
                if (invocation.Caller.Subject.Kind != PrincipalKind.Game
                    || !long.TryParse(invocation.Caller.SessionId, NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId)
                    || hub.Seated(sessionId) is not { } seated)
                    return new(CommandStatus.InvalidState, texts.Format("", "banpick.command.room_only"));
                return await seated.Scope.RoomCommandAsync(seated.Session, invocation.Caller.Language, Tokens(invocation), ct).ConfigureAwait(false);
            }));

        const string adminDescription = "Edit the server-wide ban-pick policies: groups, scopes and block, confirm or warn rules.";
        context.Effect(permissions.Register(new(AdminPermission, adminDescription)));
        var admin = new BanPickAdminCommands(hub, texts);
        context.Effect(commands.Register(context, new("gbanpick", ["gbp"], AdminPermission, adminDescription,
            [new("arguments", CommandArgumentKind.RemainingText, Optional: true)]),
            (invocation, ct) => admin.ExecuteAsync(invocation, Tokens(invocation), ct)));

        if (companion is not null)
        {
            foreach (var type in BanPickCompanion.Offered)
                context.Effect(companion.Offer(type, BanPickCompanion.Version));
            foreach (var type in BanPickCompanion.Routed)
            {
                var routed = type;
                context.Effect(companion.Route(routed, BanPickCompanion.Version, async (caller, payload, ct) =>
                {
                    var scope = hub.Seated(caller.Session.Id)?.Scope ?? hub.ForBuild(caller.Build);
                    return scope is null ? null : await scope.CompanionAsync(routed, caller, payload, ct).ConfigureAwait(false);
                }));
            }
        }
        context.Logger.LogInformation("Ban-pick is active: {Groups} group(s), {Policies} server policy(ies), revision {Revision}.",
            store.Current.Policy.Groups.Count, store.Current.Policy.Policies.Count, store.Current.Revision);
        return ValueTask.CompletedTask;
    }

    private static string[] Tokens(CommandInvocation invocation) => invocation.Arguments.Count == 0 ? []
        : invocation.Arguments[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// One build's ban-pick row: it loads the build's own item workbook, registers the room rules with the build's room
/// row and guards equipping against block rules. It needs the server-wide <c>banpick</c> row.
/// </summary>
[Plugin("banpick.room", Category = "gameplay", Names = ["en=Room ban rules", "zh-Hans=房间禁用规则", "zh-Hant=房間禁用規則", "ko=방 금지 규칙"], Description = "Applies the ban-pick policies and room bans to one client build's rooms.",
    Requires = [typeof(IBanPickHub), typeof(IClientDataStore), typeof(IGameText)],
    RequiresMany = [typeof(IRoomPolicyHost)],
    OptionalMany = [typeof(IMessageSender<ServerNotice>), typeof(IMessageSender<RoomChatReceived>)],
    Optional = [typeof(IOnlinePlayers)])]
public sealed class BanPickRoomPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<BanPickRoomOptions>();
        var hub = context.Services.Get<IBanPickHub>();
        var host = context.Services.GetAll<IRoomPolicyHost>().SingleOrDefault(candidate => candidate.Protocol == options.Protocol)
            ?? throw new ConfigException($"banpick.room needs the room row of '{options.Protocol}'; no room policy host serves it.");
        // A build that shows no server notice gets the text as a system chat line instead; one with neither cannot
        // explain a held start, so the row refuses to start there.
        var notices = context.Services.GetAll<IMessageSender<ServerNotice>>().SingleOrDefault(sender => sender.Protocol == options.Protocol);
        var chat = context.Services.GetAll<IMessageSender<RoomChatReceived>>().SingleOrDefault(sender => sender.Protocol == options.Protocol);
        if (notices is null && chat is null)
            throw new ConfigException($"banpick.room needs a server notice or room chat sender on '{options.Protocol}'.");
        var clientData = context.Services.Get<IClientDataStore>();
        var catalog = BanPickItemCatalog.Load(WorkbookFile.FromStore(clientData, options.ShopItemClientPath),
            options.MaxShopItemBytes, "banpick.room.max_shop_item_bytes");
        context.Services.TryGet<IOnlinePlayers>(out var online);
        var rules = new BanPickRoomRules(hub, host, catalog, notices, chat, context.Services.Get<IGameText>(), online, TimeProvider.System, context.Logger);
        context.Effect(host.Register(rules));
        context.Effect(hub.Register(rules));
        context.Effect(context.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(rules.GuardEquipmentAsync));
        var missing = hub.Store.Current.Policy.Groups.Where(group => !catalog.HasAny(group)).Select(group => group.Name).ToArray();
        context.Logger.LogInformation("Ban-pick rules are live on {Protocol} ({Build}): {Items} catalog items{Missing}.",
            options.Protocol, host.ClientBuild, catalog.Count,
            missing.Length == 0 ? "" : "; groups without items in this build: " + string.Join(", ", missing));
        return ValueTask.CompletedTask;
    }
}
