using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Abstractions.Net;
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Lobby.Data;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>What the lobby does when an account logs in while another live connection holds it.</summary>
public enum DuplicateLoginBehavior
{
    /// <summary>The older connection is closed and the new login takes the account.</summary>
    KickOld,

    /// <summary>The new login is refused and the older connection keeps the account.</summary>
    RejectNew,
}

/// <summary>Configuration for the <see cref="LobbyPlugin"/> row.</summary>
public sealed class LobbyOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether marked launcher tickets resolve native login accounts through the launcher service.</summary>
    public bool LauncherTickets { get; set; }

    /// <summary>
    /// Gets or sets how a game login proves its account. <c>ticket</c> accepts only launch tickets redeemed through the
    /// launcher service and needs an active <c>launcher.http</c> row; <c>open</c> accepts whatever account name the client
    /// sends, so anyone who can reach the port can play as any account. The shipped profiles default to <c>ticket</c>;
    /// a row built without the key keeps <c>open</c>.
    /// </summary>
    public string Authentication { get; set; } = "open";

    /// <summary>Gets whether <see cref="Authentication"/> requires a launch ticket for every login.</summary>
    public bool RequiresTicket => Authentication == "ticket";

    /// <summary>Gets or sets whether new accounts use the native creation page with process-local character choices.</summary>
    public bool CharacterCreationPreview { get; set; }

    /// <summary>Gets or sets the supported rename consumable item; zero disables its use handler.</summary>
    [Range(0, int.MaxValue)]
    public int RenameItemId { get; set; }

    /// <summary>Gets or sets whether native catalog sprays can atomically recolor the player.</summary>
    public bool PaintItems { get; set; }

    /// <summary>
    /// Gets or sets whether the row publishes <see cref="IOnlinePlayers"/> for party invites and player lookups. The
    /// row must sit in a service realm of its own build (the legacy bundle's inventoryview subtree), because every
    /// build's lobby publishes the same contract.
    /// </summary>
    public bool OnlinePlayers { get; set; }

    /// <summary>
    /// Gets or sets whether the row answers <see cref="FighterInfoRequested"/>, the player menu's "view info" item, from
    /// the logged-in players of its own build. It needs <see cref="OnlinePlayers"/> and the build's codec bindings for
    /// the request and the reply.
    /// </summary>
    public bool FighterInfo { get; set; }

    /// <summary>Gets or sets the retained legacy notice template; native item-use replies now provide rename feedback.</summary>
    [Required, MaxLength(256)]
    public string RenameSuccessNotice { get; set; } = "Your fighter name has been changed to {nickname}.";

    /// <summary>Gets or sets whether new accounts use native creation backed by the selected player and inventory stores.</summary>
    public bool CharacterCreation { get; set; }

    /// <summary>
    /// Gets or sets whether the row's client build supplies its lobby facts from <c>packets/builds.yaml</c>, inherited
    /// through each build's <c>base</c>: the <c>character_creation</c> capability gates <see cref="CharacterCreation"/>,
    /// and the <c>lobby</c> values replace <see cref="CreationCharacterTypes"/>, <see cref="StarterItemIds"/> and
    /// <see cref="StarterWornIds"/>. The legacy bundle sets it; a build without packet tables leaves it off.
    /// </summary>
    public bool BuildValues { get; set; }

    /// <summary>Gets or sets the packet-table directory read when <see cref="BuildValues"/> is set.</summary>
    public string PacketsDirectory { get; set; } = "packets";

    /// <summary>
    /// Gets or sets the character types the build's native creation stage offers; the created
    /// character is item 300000 + type. Dao (1) and Dizni (2) by default; the Indonesian clients
    /// offer Leo (11) and Lavi (12).
    /// </summary>
    public int[] CreationCharacterTypes { get; set; } = [1, 2];

    /// <summary>Gets or sets whether every login repeats native creation for video recording, including existing accounts.</summary>
    public bool RecordingAlwaysCreateCharacter { get; set; }

    /// <summary>Gets or sets the maximum pending sessions and created choices held by the recording preview.</summary>
    [Range(1, 100000)]
    public int MaxCreationPlayers { get; set; } = 1024;
    /// <summary>Gets or sets the connection protocol name whose packets this plugin handles.</summary>
    [Required]
    public string Protocol { get; set; } = "kr-1256/game";

    /// <summary>
    /// Gets or sets whether an unknown login name creates a player on the fly. This is a
    /// development convenience; production deployments should disable it.
    /// </summary>
    public bool AutoCreatePlayers { get; set; } = true;

    /// <summary>Gets or sets the login name used when the client sends an empty session string.</summary>
    [Required]
    public string FallbackUsername { get; set; } = "guest";

    /// <summary>
    /// Gets or sets what happens when an account logs in while another live connection already
    /// holds it: <c>kick_old</c> closes the older connection and lets the new login take the
    /// account (default), <c>reject_new</c> leaves the older connection untouched and refuses
    /// the new login. Under both values two live sessions never share one account.
    /// </summary>
    public string DuplicateLogin { get; set; } = "kick_old";

    /// <summary>Gets the parsed <see cref="DuplicateLogin"/> policy.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the value is neither
    /// <c>kick_old</c> nor <c>reject_new</c>; <see cref="Validate"/> reports the same value as a
    /// configuration error before the row activates.</exception>
    public DuplicateLoginBehavior DuplicateLoginPolicy => ParseDuplicateLogin(DuplicateLogin)
        ?? throw new InvalidOperationException(
            $"duplicate_login must be 'kick_old' or 'reject_new'; got '{DuplicateLogin}'.");

    /// <summary>Parses one <c>duplicate_login</c> value.</summary>
    /// <param name="value">The configured value.</param>
    /// <returns>The behavior, or null when the value is neither accepted value.</returns>
    internal static DuplicateLoginBehavior? ParseDuplicateLogin(string? value) => value switch
    {
        "kick_old" => DuplicateLoginBehavior.KickOld,
        "reject_new" => DuplicateLoginBehavior.RejectNew,
        _ => null,
    };

    /// <summary>
    /// Gets or sets where the login inventory burst (S2C 802 / 803 / 810) reads its rows.
    /// <c>reference</c> keeps the hardcoded starter bag on the wire and is the default; <c>mysql</c> loads each
    /// player's rows from <see cref="IInventoryRepository"/> and requires the persistence row, failing
    /// activation when it is absent. First login under <c>mysql</c> seeds the starter bag when <see
    /// cref="SeedStarterInventory"/> is set, so the burst is byte-identical to the starter-row builders until
    /// something changes the bag.
    /// </summary>
    public string InventorySource { get; set; } = "reference";

    /// <summary>
    /// Gets or sets whether a player with no stored inventory rows is seeded
    /// with the starter bag under <c>mysql</c>. Disabling it sends an empty
    /// bag instead; only useful for tests.
    /// </summary>
    public bool SeedStarterInventory { get; set; } = true;

    /// <summary>
    /// Gets or sets whether login removes the player's expired time-limited items (unwearing them, and
    /// wearing the default character again when the worn one ended) and announces them with the build's
    /// native expiry notice. It needs <c>inventory_source: mysql</c>. YAML key <c>expire_items</c>.
    /// </summary>
    public bool ExpireItems { get; set; }

    /// <summary>
    /// Gets or sets whether login applies the daily life rule to the player's life-counted items: one life
    /// lost per whole UTC day since the last login, an item at zero is removed like an expired one. It needs
    /// <c>expire_items: true</c> and, with <c>build_values</c>, the build's <c>life_items</c> capability.
    /// YAML key <c>life_items</c>.
    /// </summary>
    public bool LifeItems { get; set; }

    /// <summary>
    /// Gets or sets the starter item ids seeded on first login, in bag order. The
    /// default is the kr-1256 set; a build whose client item table lacks
    /// some of those ids carries its own proven subset so the seeded bag never
    /// names an item the client cannot render.
    /// </summary>
    public int[] StarterItemIds { get; set; } =
    [
        300001,
        201001, 201003, 201004, 201005,
        202001, 202003, 202004, 202006,
        203001, 203003, 203004, 203005,
        204001, 204003, 204004, 204005,
        205001, 207001,
    ];

    /// <summary>
    /// Gets or sets the starter ids that begin worn: one character and one weapon per
    /// equipped position. Every entry must also appear in <see cref="StarterItemIds"/>.
    /// </summary>
    public int[] StarterWornIds { get; set; } = [300001, 202001, 201001, 205001, 207001];

    /// <summary>Builds the validated starter set this row seeds and advertises as worn.</summary>
    /// <returns>The kit the login resolvers and the reference path use.</returns>
    /// <summary>
    /// Gets or sets the starter ids that begin with a limited lifetime, as an
    /// id-to-days mapping written in YAML as a list of "id:days" strings. The
    /// client accepts both permanent slots (the 2958101-day
    /// sentinel) and limited ones, and a limited seed exercises the duration
    /// label the bag cells render.
    /// </summary>
    public string[] StarterExpiryDays { get; set; } = [];

    /// <summary>Builds the validated starter set this row seeds and advertises as worn.</summary>
    /// <returns>The kit the login resolvers and the reference path use.</returns>
    public StarterKit BuildStarterKit()
    {
        if (StarterExpiryDays.Length == 0)
            return new(StarterItemIds, StarterWornIds);
        var expiries = new Dictionary<int, double>();
        foreach (var entry in StarterExpiryDays)
        {
            var parts = entry.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var id)
                || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var days))
                throw new ConfigException(
                    $"starter_expiry_days entry '{entry}' must be 'itemId:days'.");
            expiries[id] = days;
        }
        return new(StarterItemIds, StarterWornIds, expiries);
    }

    /// <summary>
    /// Gets or sets where the login money snapshot (S2C 106) reads its six
    /// integers. <c>reference</c> keeps the all-zero form; <c>mysql</c> loads
    /// <see cref="IPlayerWalletRepository"/> and requires the persistence row.
    /// </summary>
    public string MoneySource { get; set; } = "reference";

    /// <summary>
    /// Gets or sets the lucci a first-seen player is seeded with under the
    /// <c>mysql</c> money source (S2C 106 field 5, client wallet
    /// <c>dword_18E56E8+0x4C</c>). Zero keeps the login burst all-zero;
    /// a deployment that wants a playable shop sets
    /// a positive value so the client's buy pre-check passes.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int StartingLucci { get; set; }

    /// <summary>
    /// Gets or sets the cash a first-seen player is seeded with under the
    /// <c>mysql</c> money source (S2C 106 field 6, client wallet
    /// <c>dword_18E56E8+0x50</c>).
    /// </summary>
    [Range(0, int.MaxValue)]
    public int StartingCash { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (RecordingAlwaysCreateCharacter && !CharacterCreationPreview)
            yield return new ValidationResult("recording_always_create_character requires character_creation_preview.",
                [nameof(RecordingAlwaysCreateCharacter)]);
        if (CharacterCreation && CharacterCreationPreview)
            yield return new ValidationResult("character_creation and character_creation_preview are mutually exclusive.");
        if (CreationCharacterTypes is null || CreationCharacterTypes.Length is 0 or > 8
                || CreationCharacterTypes.Any(type => type is < 1 or > 31)
                || CreationCharacterTypes.Distinct().Count() != CreationCharacterTypes.Length)
            yield return new ValidationResult("creation_character_types requires one to eight distinct types in [1, 31].",
                [nameof(CreationCharacterTypes)]);
        if (CharacterCreation && InventorySource != "mysql")
            yield return new ValidationResult("character_creation requires the store-backed inventory_source mysql.");
        if (InventorySource is not ("reference" or "mysql"))
        {
            yield return new ValidationResult(
                $"inventory_source must be 'reference' or 'mysql'; got '{InventorySource}'.",
                [nameof(InventorySource)]);
        }

        if (MoneySource is not ("reference" or "mysql"))
        {
            yield return new ValidationResult(
                $"money_source must be 'reference' or 'mysql'; got '{MoneySource}'.",
                [nameof(MoneySource)]);
        }

        if (Authentication is not ("ticket" or "open"))
            yield return new ValidationResult($"authentication must be 'ticket' or 'open'; got '{Authentication}'.",
                [nameof(Authentication)]);

        if (ParseDuplicateLogin(DuplicateLogin) is null)
        {
            yield return new ValidationResult(
                $"duplicate_login must be 'kick_old' or 'reject_new'; got '{DuplicateLogin}'.",
                [nameof(DuplicateLogin)]);
        }

        if (StarterItemIds is null || StarterItemIds.Length == 0 || StarterItemIds.Length > InventoryLimits.MaxInventorySlotsPerBag)
            yield return new ValidationResult(
                $"starter_item_ids must list 1..{InventoryLimits.MaxInventorySlotsPerBag} items.",
                [nameof(StarterItemIds)]);
        else if (StarterItemIds.Any(id => id <= 0) || StarterItemIds.Distinct().Count() != StarterItemIds.Length)
            yield return new ValidationResult(
                "starter_item_ids entries must be positive and unique.",
                [nameof(StarterItemIds)]);
        foreach (var entry in StarterExpiryDays)
        {
            var parts = entry.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var id)
                || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var days)
                || days <= 0
                || (StarterItemIds is not null && !StarterItemIds.Contains(id)))
            {
                yield return new ValidationResult(
                    $"starter_expiry_days entry '{entry}' must be 'itemId:days' with a positive day count for a starter item.",
                    [nameof(StarterExpiryDays)]);
            }
        }
        if (StarterWornIds is null
            || StarterWornIds.Distinct().Count() != StarterWornIds.Length
            || (StarterItemIds is not null && StarterWornIds.Any(id => !StarterItemIds.Contains(id))))
        {
            yield return new ValidationResult(
                "starter_worn_ids entries must be unique and present in starter_item_ids.",
                [nameof(StarterWornIds)]);
        }
    }
}

/// <summary>
/// Login logic for every build: a <see cref="LoginRequested"/> from the build's codec row is
/// authenticated, the player's inventory, equipment and wallet are loaded, and one
/// <see cref="LoginSucceeded"/> goes back to the codec, which writes that build's login
/// sequence. With a stored inventory the row also publishes <see cref="IInventoryPushService"/>,
/// which republishes inventory through the build's inventory codec. The row holds no opcode or
/// layout of any build. Plaza enter (C2S 106) is owned by <c>ingame</c> after the main menu.
/// </summary>
[Plugin("lobby", Description = "Login and main-menu logic shared by every client build.",
    Requires = [typeof(IMessageRouter)],
    Optional = [typeof(IPlayerRepository), typeof(IPaintItemCatalog), typeof(ILauncherTicketService), typeof(IEquipmentSelection), typeof(IDbConnectionFactory), typeof(IInventoryRepository), typeof(IPlayerWalletRepository)], OptionalMany = [typeof(IMessageSender<ServerNotice>), typeof(IInboundMessageBinding<InventoryItemUseRequested>), typeof(IMessageSender<InventoryItemUseReplied>), typeof(IMessageSender<UserStatUpdated>), typeof(IInboundMessageBinding<CharacterNameRequested>), typeof(IInboundMessageBinding<CharacterCreateRequested>), typeof(IInboundMessageBinding<CharacterLoginRequested>), typeof(IMessageSender<CharacterCreationOpened>), typeof(IMessageSender<CharacterNameReplied>), typeof(IMessageSender<CharacterCreateReplied>), typeof(IInboundMessageBinding<BubbleFighter.Fantasy.Contracts.Lobby.MainMenuInitRequest>), typeof(IMessageSender<EmptySessionSignature>), typeof(IInboundMessageBinding<LoginRequested>), typeof(IMessageSender<LoginSucceeded>), typeof(IMessageSender<LoginRejected>), typeof(IMessageSender<InventoryReplaced>), typeof(IMessageSender<InventoryPurchaseApplied>), typeof(IMessageSender<InventorySlotsUpdated>), typeof(IMessageSender<ItemsExpired>), typeof(IInboundMessageBinding<FighterInfoRequested>), typeof(IMessageSender<FighterInfoReplied>)],
    ConditionalProvides = [typeof(IInventoryPushService), typeof(IInventoryExpiry), typeof(IInventoryItemRemoval), typeof(IOnlinePlayers)])]
public sealed class LobbyPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<LobbyOptions>();
        if (options.BuildValues)
        {
            var build = LobbyBuildValues.Apply(options, context.Row.LineageOrBuild());
            context.Logger.LogInformation(
                "Lobby values of build {Build}: character_creation={Creation}, creation_character_types=[{Types}], starter_item_ids=[{Items}], starter_worn_ids=[{Worn}].",
                build, options.CharacterCreation, string.Join(", ", options.CreationCharacterTypes),
                string.Join(", ", options.StarterItemIds), string.Join(", ", options.StarterWornIds));
        }
        var p = options.Protocol;
        if (options.ExpireItems && options.InventorySource != "mysql")
            throw new ConfigException("expire_items requires inventory_source: mysql.");
        if (options.LifeItems && !options.ExpireItems)
            throw new ConfigException("life_items requires expire_items: true, because an item with no life left is removed by the expiry sweep.");
        var messages = context.Services.Get<IMessageRouter>();
        var starter = options.BuildStarterKit();

        IPlayerRepository players = ResolvePlayerRepository(context.Services);
        context.Logger.LogInformation("Lobby using {Repository} for players.", players.GetType().Name);

        LoginInventoryResolver? inventory = null;
        InventoryExpirySweep? expiry = null;
        LoginLifeRule? lifeRule = null;
        if (options.InventorySource == "mysql")
        {
            if (!context.Services.TryGet<IInventoryRepository>(out var store) || store is null)
            {
                throw new InvalidOperationException(
                    "LobbyOptions.inventory_source 'mysql' requires a persistence row publishing IInventoryRepository.");
            }

            context.Services.TryGet<IEquipmentSelection>(out var selection);
            inventory = new LoginInventoryResolver(store, options.SeedStarterInventory, selection, starter);
            var removal = new InventoryExpirySweep(store, selection, starter, TimeProvider.System);
            context.Effect(context.Services.Register<IInventoryItemRemoval>(removal));
            if (options.ExpireItems)
            {
                expiry = removal;
                context.Effect(context.Services.Register<IInventoryExpiry>(expiry));
                if (options.LifeItems)
                    lifeRule = new LoginLifeRule(store, TimeProvider.System);
            }
            // A build whose codec row writes no inventory family gets no push service, so rows
            // that need one fail at their own activation rather than sending another build's bytes.
            var replaced = OptionalSender<InventoryReplaced>(context, p);
            var purchase = OptionalSender<InventoryPurchaseApplied>(context, p);
            var slots = OptionalSender<InventorySlotsUpdated>(context, p);
            if (replaced is not null && purchase is not null && slots is not null)
            {
                context.Effect(context.Services.Register<IInventoryPushService>(new InventoryPush(inventory, replaced, purchase, slots)));
                context.Logger.LogInformation("Lobby inventory source mysql (seed starter: {Seed}); inventory push service published.", options.SeedStarterInventory);
            }
            else
            {
                context.Logger.LogInformation("Lobby inventory source mysql (seed starter: {Seed}); '{Protocol}' has no inventory codec, no push service.", options.SeedStarterInventory, p);
            }
        }

        LoginMoneyResolver? money = null;
        if (options.MoneySource == "mysql")
        {
            if (!context.Services.TryGet<IPlayerWalletRepository>(out var moneyStore) || moneyStore is null)
            {
                throw new InvalidOperationException(
                    "LobbyOptions.money_source 'mysql' requires a persistence row publishing IPlayerWalletRepository.");
            }

            money = new LoginMoneyResolver(moneyStore, options.StartingLucci, options.StartingCash);
            context.Logger.LogInformation("Lobby money source mysql.");
        }

        var playerSessions = new PlayerSessionScope();
        context.Effect(playerSessions);
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => playerSessions.Remove(e.Connection.Id)));
        if (options.OnlinePlayers)
            context.Effect(context.Services.Register<IOnlinePlayers>(playerSessions));

        if (options.FighterInfo)
        {
            if (!options.OnlinePlayers)
                throw new ConfigException("fighter_info requires online_players: true, because the target is found among the logged-in players.");
            RequireBinding<FighterInfoRequested>(context, p);
            var fighterReplies = RequireSender<FighterInfoReplied>(context, p);
            context.Effect(messages.For(context).On<FighterInfoRequested>(p,
                (session, request, ct) => FighterInfoHandler.HandleAsync(playerSessions, fighterReplies, session, request, ct)));
        }

        // A build whose codec decodes the main-menu probe gets its empty signature reply.
        if (context.Services.GetAll<IInboundMessageBinding<BubbleFighter.Fantasy.Contracts.Lobby.MainMenuInitRequest>>().Any(b => b.Protocol == p))
        {
            var signatureSender = RequireSender<EmptySessionSignature>(context, p);
            context.Effect(messages.For(context).On<BubbleFighter.Fantasy.Contracts.Lobby.MainMenuInitRequest>(p,
                (session, _, ct) => signatureSender.SendAsync(session, new EmptySessionSignature(), ct)));
        }

        // One login path for every build: the build's codec decodes the request and writes the
        // whole login sequence from LoginSucceeded.
        RequireBinding<LoginRequested>(context, p);
        var succeeded = RequireSender<LoginSucceeded>(context, p);
        var rejected = RequireSender<LoginRejected>(context, p);
        CharacterCreationPreview? creation = null;
        IMessageSender<CharacterCreationOpened>? creationOpened = null;
        if (options.CharacterCreationPreview || options.CharacterCreation)
        {
            if (options.CharacterCreationPreview && players.PersistsState)
                throw new ConfigException("Character creation preview requires process-local player storage.");
            creation = new(options.MaxCreationPlayers, players, starter, inventory, options.CharacterCreation,
                options.CharacterCreation && players.CreatesStarterInventoryAtomically, options.CreationCharacterTypes);
            context.Effect(creation);
            context.Effect(context.Events.On<ConnectionClosedEvent>(e => creation.Remove(e.Connection.Id)));
            RequireBinding<CharacterNameRequested>(context, p);
            RequireBinding<CharacterCreateRequested>(context, p);
            RequireBinding<CharacterLoginRequested>(context, p);
            creationOpened = RequireSender<CharacterCreationOpened>(context, p);
            var names = RequireSender<CharacterNameReplied>(context, p);
            var created = RequireSender<CharacterCreateReplied>(context, p);
            context.Effect(messages.For(context).On<CharacterNameRequested>(p,
                (session, request, ct) => creation.CheckNameAsync(session, request, names, ct)));
            context.Effect(messages.For(context).On<CharacterCreateRequested>(p, async (session, request, ct) =>
            {
                // The offered-type check lives in the preview; logging the request here is what lets a refusal
                // (and the unproven second word the client sent) be read back from a real session.
                context.Logger.LogInformation(
                    "Session {Id} C2S 32 create nickname={Nickname} characterType={Type}.",
                    session.Id, request.Nickname, request.CharacterType);
                await creation.CreateAsync(session, request, created, ct).ConfigureAwait(false);
            }));
        }
        if (options.RenameItemId != 0 || options.PaintItems)
        {
            RequireBinding<InventoryItemUseRequested>(context, p);
            var useReplies = RequireSender<InventoryItemUseReplied>(context, p);
            var inventoryReplies = OptionalSender<InventoryReplaced>(context, p);
            Func<ISession, long, CancellationToken, ValueTask>? pushAfterUse = inventory is null || inventoryReplies is null
                ? null : async (session, userNum, ct) =>
                {
                    var snapshot = await inventory.ResolveAsync(userNum, ct).ConfigureAwait(false);
                    await inventoryReplies.SendAsync(session, new(snapshot.CharacterBag, snapshot.EquippedBag), ct).ConfigureAwait(false);
                };
            IPaintItemCatalog? paints = null;
            Func<ISession, PlayerRecord, CancellationToken, ValueTask>? pushStat = null;
            if (options.PaintItems)
            {
                paints = context.Services.Get<IPaintItemCatalog>();
                if (!players.SupportsAtomicPaintApplication || pushAfterUse is null || money is null)
                    throw new ConfigException("paint_items requires atomic player storage, inventory publication and a wallet source.");
                var stat = RequireSender<UserStatUpdated>(context, p);
                var walletSource = money;
                pushStat = async (session, player, ct) =>
                {
                    var wallet = await walletSource.ResolveAsync(player.UserNum, ct).ConfigureAwait(false);
                    await stat.SendAsync(session, new(player, wallet), ct).ConfigureAwait(false);
                };
            }
            var use = new InventoryItemUseHandler(players, useReplies, pushAfterUse, context.Events, options.RenameItemId, paints, pushStat);
            context.Effect(messages.For(context).On<InventoryItemUseRequested>(p, use.HandleAsync));
        }
        context.Services.TryGet<ILauncherTicketService>(out var tickets);
        if (options.RequiresTicket && tickets is null)
            throw new ConfigException("authentication: ticket requires an active launcher.http row, which publishes "
                + "ILauncherTicketService. Enable the launcher, or set BFF_LOGIN_AUTHENTICATION=open for a trusted LAN.");
        if (options.LauncherTickets && tickets is null)
            throw new ConfigException("launcher_tickets requires a launcher row publishing ILauncherTicketService.");
        if (!options.RequiresTicket)
            context.Logger.LogWarning("Lobby {Protocol} runs with authentication: open. Any client that reaches this port "
                + "can log in as any account; use it only on a trusted network.", options.Protocol);
        var handlers = new Handlers(options, players, inventory, money, playerSessions, starter, context.Logger, creation, creationOpened, tickets, expiry, lifeRule);
        context.Effect(messages.For(context).On<InitialTutorialCompleted>(p, (session, _, ct) =>
        {
            if (!session.TryFeature<IPlayerSession>(out var player))
                throw new InvalidOperationException("Tutorial completion requires an authenticated player.");
            return players.CompleteTutorialAsync(player.Identity.UserNum, ct);
        }));
        if (creation is not null)
            context.Effect(messages.For(context).On<CharacterLoginRequested>(p, async (session, _, ct) =>
            {
                string account;
                try
                {
                    account = creation.Finish(session);
                }
                catch (InvalidOperationException ex)
                {
                    // The client sends this frame after any S2C 14 result, including a refusal, so a session with no
                    // created character cannot continue; close it with a clear reason instead of faulting the row.
                    context.Logger.LogError(
                        "Session {Id} C2S 33 arrived without a completed creation ({Reason}); closing.", session.Id, ex.Message);
                    await session.Connection.CloseAsync("character creation not completed").ConfigureAwait(false);
                    return;
                }
                await handlers.HandleLoginRequestedAsync(session, new(account), succeeded, rejected, ct, afterCreation: true)
                    .ConfigureAwait(false);
            }));
        context.Effect(messages.For(context).On<LoginRequested>(p,
            (session, request, ct) => handlers.HandleLoginRequestedAsync(session, request, succeeded, rejected, ct)));
        return ValueTask.CompletedTask;
    }

    internal static IPlayerRepository ResolvePlayerRepository(IServiceRegistry services)
    {
        if (services.TryGet<IPlayerRepository>(out var supplied) && supplied is not null) return supplied;
        return services.TryGet<IDbConnectionFactory>(out var database) && database is not null
            ? new MySqlPlayerRepository(database) : new InMemoryPlayerRepository();
    }

    private static void RequireBinding<T>(IPluginContext context, string protocol) where T : IMessage
    {
        if (context.Services.GetAll<IInboundMessageBinding<T>>().Count(binding => binding.Protocol == protocol) != 1)
            throw new ConfigException($"Lobby requires exactly one IInboundMessageBinding<{typeof(T).Name}> for '{protocol}'.");
    }

    private static IMessageSender<T> RequireSender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        return senders.Length == 1 ? senders[0]
            : throw new ConfigException($"Lobby requires exactly one IMessageSender<{typeof(T).Name}> for '{protocol}'.");
    }

    private static IMessageSender<T>? OptionalSender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        return senders.Length <= 1 ? senders.SingleOrDefault()
            : throw new ConfigException($"Lobby allows at most one IMessageSender<{typeof(T).Name}> for '{protocol}'.");
    }

    private sealed class Handlers(
        LobbyOptions options,
        IPlayerRepository players,
        LoginInventoryResolver? inventory,
        LoginMoneyResolver? money,
        PlayerSessionScope playerSessions,
        StarterKit starter,
        ILogger logger,
        CharacterCreationPreview? creation,
        IMessageSender<CharacterCreationOpened>? creationOpened,
        ILauncherTicketService? tickets,
        InventoryExpirySweep? expiry = null,
        LoginLifeRule? lifeRule = null)
    {
        private readonly DuplicateLoginGuard _duplicateLogins = new(playerSessions, options.DuplicateLoginPolicy, logger);

        /// <summary>Authenticates the account, loads the player's state, and hands it to the build's codec.</summary>
        public async ValueTask HandleLoginRequestedAsync(ISession session, LoginRequested request,
            IMessageSender<LoginSucceeded> succeeded, IMessageSender<LoginRejected> rejected, CancellationToken ct,
            bool afterCreation = false)
        {
            var nativeAccount = LauncherLoginAccount.Resolve(request.Account, session.Profile.Id,
                options.LauncherTickets || options.RequiresTicket, tickets, options.RequiresTicket);
            if (nativeAccount is null)
            {
                // Never log the refused value: it may be a password typed into an in-game login box.
                var reason = LauncherLoginAccount.IsTicketForm(request.Account) ? "invalid launcher ticket" : "launcher ticket required";
                logger.LogWarning("Session {Id} login refused for build {Build}: {Reason}.", session.Id, session.Profile.Id, reason);
                await session.Connection.CloseAsync(reason).ConfigureAwait(false);
                return;
            }
            var username = string.IsNullOrWhiteSpace(nativeAccount) ? options.FallbackUsername : nativeAccount.Trim();
            logger.LogInformation("Session {Id} requested login as '{User}'.", session.Id, username);
            var needsCreation = false;
            if (creation is not null && !afterCreation)
            {
                var existing = await players.FindByUsernameAsync(username, ct).ConfigureAwait(false);
                var hasCreatedCharacter = existing is not null
                    && await players.HasCreatedCharacterAsync(existing.UserNum, ct).ConfigureAwait(false);
                needsCreation = options.RecordingAlwaysCreateCharacter || existing is null
                    || (options.CharacterCreation && !hasCreatedCharacter);
                logger.LogInformation(
                    "Session {Id} login lookup for '{User}': player_exists={PlayerExists}, character_created={CharacterCreated}, recording_always_create={RecordingAlwaysCreate}, needs_creation={NeedsCreation}.",
                    session.Id, username, existing is not null, hasCreatedCharacter,
                    options.RecordingAlwaysCreateCharacter, needsCreation);
            }
            if (needsCreation && creation is not null)
            {
                creation.Open(session, username);
                await creationOpened!.SendAsync(session, new(), ct).ConfigureAwait(false);
                return;
            }
            var account = await AuthenticateAsync(session, username, ct).ConfigureAwait(false);
            if (account.Outcome != AccountOutcome.Attached)
            {
                // LoginRejection has no duplicate-login member, so a refused second login sends the
                // build's only refusal code; the warning above names the real reason.
                await rejected.SendAsync(session, new LoginRejected(LoginRejection.AccountNotFound), ct).ConfigureAwait(false);
                return;
            }
            var player = account.Player!;
            var playerSession = account.Session!;
            IReadOnlyList<InventorySlotRecord>? characterBag = null, equippedBag = null;
            int[] equipped;
            IReadOnlyList<int> expiredItemIds = [];
            if (inventory is null)
            {
                equipped = [.. (creation?.KitFor(username) ?? starter).WornItemIds];
            }
            else
            {
                // Expired rows go before the bag is read so the login publication never carries them.
                // Life first: an item whose last life went today is then removed and announced by the same sweep.
                if (lifeRule is not null)
                    _ = await lifeRule.ApplyAsync(player.UserNum, ct).ConfigureAwait(false);
                if (expiry is not null)
                    expiredItemIds = await expiry.SweepAsync(player.UserNum, ct).ConfigureAwait(false);
                var snapshot = await inventory.ResolveAsync(player.UserNum, ct).ConfigureAwait(false);
                (characterBag, equippedBag) = (snapshot.CharacterBag, snapshot.EquippedBag);
                equipped = snapshot.CharacterBag.Where(row => (row.Flags & 1) != 0).Select(row => row.ItemId).Distinct().ToArray();
            }
            var character = equipped.FirstOrDefault(id => id is >= 300001 and <= 300031, StarterInventory.DaoCharacterItemId);
            var wallet = money is null ? null : await money.ResolveAsync(player.UserNum, ct).ConfigureAwait(false);
            var offerTutorial = options.CharacterCreation
                ? await players.NeedsTutorialAsync(player.UserNum, ct).ConfigureAwait(false) : afterCreation;
            await succeeded.SendAsync(session, new LoginSucceeded(
                checked((int)player.UserNum), player.Username, player.Nickname, Math.Max(1, player.Level), DateTime.Now,
                character, equipped, wallet, characterBag, equippedBag)
                { OfferTutorial = creation is not null ? offerTutorial : null, ExpiredItemIds = expiredItemIds, ColorIndex = player.ColorIndex }, ct).ConfigureAwait(false);
            playerSession.MoveTo(SessionPhase.MainMenu);
        }

        /// <summary>
        /// Finds or creates the account's player and claims the account for the session. A second
        /// login for an account that is live on another connection is settled by
        /// <see cref="DuplicateLoginGuard"/>: the older connection loses it and is closed, or this
        /// login is refused.
        /// </summary>
        /// <param name="session">The session the login arrived on.</param>
        /// <param name="username">The trimmed account name.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The account phase result.</returns>
        private async ValueTask<AccountResult> AuthenticateAsync(ISession session, string username, CancellationToken ct)
        {
            var player = await players.FindByUsernameAsync(username, ct).ConfigureAwait(false);
            if (player is null)
            {
                if (!options.AutoCreatePlayers)
                {
                    logger.LogWarning("Session {Id} login rejected: unknown player '{User}'.", session.Id, username);
                    return new AccountResult(AccountOutcome.AccountNotFound, null, null);
                }
                player = await players.CreateAsync(username, username, ct).ConfigureAwait(false);
                logger.LogInformation("Created player {UserNum} '{User}'.", player.UserNum, username);
            }
            if (creation?.NicknameFor(username) is { } nickname)
                player = player with { Nickname = nickname };

            // Legacy clients render level zero as an uninitialised icon during the first menu
            // snapshot. Keep the persisted value untouched, but publish the same level floor
            // used by room seats so login, plaza and room projections cannot disagree.
            var identity = new PlayerIdentity(
                checked((int)player.UserNum), player.Username, player.Nickname, Math.Max(1, player.Level)) { ColorIndex = player.ColorIndex };
            var claim = await _duplicateLogins.ClaimAsync(session, identity).ConfigureAwait(false);
            if (claim.Status == LoginClaimStatus.RefusedDuplicate)
                return new AccountResult(AccountOutcome.DuplicateRefused, player, null);

            logger.LogInformation("Session {Id} authenticated as {UserNum} '{Nick}'.", session.Id, player.UserNum, player.Nickname);
            return new AccountResult(AccountOutcome.Attached, player, claim.Session);
        }

        /// <summary>How the account phase of a login ended.</summary>
        private enum AccountOutcome
        {
            /// <summary>The session holds the account and the login may continue.</summary>
            Attached,

            /// <summary>No player exists for the account and players are not created on demand.</summary>
            AccountNotFound,

            /// <summary>The account is live on another connection and the row refuses second logins.</summary>
            DuplicateRefused,
        }

        /// <summary>The account phase result.</summary>
        /// <param name="Outcome">How the phase ended.</param>
        /// <param name="Player">The resolved player, when one exists.</param>
        /// <param name="Session">The player state the session holds, when the login was attached.</param>
        private readonly record struct AccountResult(AccountOutcome Outcome, PlayerRecord? Player, IPlayerSession? Session);
    }
}
