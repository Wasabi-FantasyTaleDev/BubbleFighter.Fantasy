using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// One build's <c>login</c> block of <c>packets/builds.yaml</c>: the request/reply layouts
/// (<see cref="LoginShape"/>), how the accepted reply is written, how the main menu is installed and the
/// ordered list of publications a successful login sends. The list is the build's own; a build that states
/// no list keeps the legacy baseline below.
/// </summary>
/// <param name="Shape">The request and reply shape flags the message codec reads.</param>
/// <param name="Reply">
/// How <c>gspRepLogin</c> is written: <c>legacy</c> is the kr-812 table form (the login account and an OLE
/// server time), <c>modern</c> is the table form of the kr-1256 family (a server session id, the initial
/// channel and a zero time). A build with the response pair states <c>login_response: true</c> instead and
/// ignores this value.
/// </param>
/// <param name="MainMenu">
/// How the main-menu install is sent: <c>sender</c> asks the build's room adapter for the menu (the legacy
/// row), <c>table</c> writes the gspGotoRoom install from <c>packets/room.yaml</c> (the modern family).
/// </param>
/// <param name="ChannelId">
/// The nonzero channel the accepted reply stores at <c>ServerConnection+2508</c> (the modern table form).
/// Zero would leave the client's room-list polling unarmed (<c>kr-1256 sub_8530C0</c>).
/// </param>
/// <param name="Burst">The ordered post-login publications, each named by its <see cref="LoginBurstStep"/>.</param>
internal sealed record LoginValues(LoginShape Shape, string Reply, string MainMenu, int ChannelId, IReadOnlyList<LoginBurstStep> Burst)
{
    /// <summary>The channel the kr-1256 family's login reply has always carried.</summary>
    internal const int InitialChannelId = 99;
    /// <summary>The legacy reply table form.</summary>
    internal const string LegacyReply = "legacy";

    /// <summary>The kr-1256 family reply table form.</summary>
    internal const string ModernReply = "modern";

    /// <summary>The main menu is written by the build's room adapter.</summary>
    internal const string SenderMainMenu = "sender";

    /// <summary>The main menu install is written from the packet tables.</summary>
    internal const string TableMainMenu = "table";

    /// <summary>
    /// The legacy login order: reply, user stat, bag, equipped view, main menu, expired-item notices. It is
    /// the order every kr-812-based row sent before the burst became data.
    /// </summary>
    private static readonly LoginBurstStep[] LegacyBurst =
    [
        LoginBurstStep.LoginReply, LoginBurstStep.UserStat, LoginBurstStep.Inventory,
        LoginBurstStep.EquipmentView, LoginBurstStep.MainMenu, LoginBurstStep.ExpiredItems,
    ];

    /// <summary>Reads one build's login values, refusing anything the login path cannot carry out.</summary>
    /// <param name="build">The build, for messages.</param>
    /// <param name="values">The build's values laid over its bases'.</param>
    /// <returns>The resolved values.</returns>
    /// <exception cref="ConfigException">Thrown for a missing or malformed block, an unknown value or an unknown step name.</exception>
    internal static LoginValues From(string build, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var shape = LoginShape.From(build, values);
        var login = LoginMap(build, values);
        var where = $"Build '{build}' login";
        var reply = Text(login, "reply", where, LegacyReply);
        if (reply is not (LegacyReply or ModernReply))
            throw new ConfigException($"{where}.reply must be '{LegacyReply}' or '{ModernReply}'.");
        if (shape.LoginResponse && reply != LegacyReply)
            throw new ConfigException($"{where} states login_response: true and reply: {reply}; the response pair chooses its own writer.");
        var mainMenu = Text(login, "main_menu", where, SenderMainMenu);
        if (mainMenu is not (SenderMainMenu or TableMainMenu))
            throw new ConfigException($"{where}.main_menu must be '{SenderMainMenu}' or '{TableMainMenu}'.");
        var channelId = Integer(login, "channel_id", where, InitialChannelId);
        if (channelId == 0)
            throw new ConfigException($"{where}.channel_id must be nonzero so the client arms its room-list polling.");
        var burst = Steps(login, where);
        return new(shape, reply, mainMenu, channelId, burst);
    }

    private static Dictionary<string, object?> LoginMap(string build, IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("login", out var raw) || raw is not Dictionary<string, object?> login)
            throw new ConfigException($"Build '{build}' must define login values in packets/builds.yaml.");
        if (login.Keys.FirstOrDefault(key => key is not ("request_trailer" or "request_token" or "request_extra_wstr"
                or "login_response" or "reply" or "main_menu" or "channel_id" or "burst")) is { } unknown)
            throw new ConfigException($"Build '{build}' login has the unknown value '{unknown}'.");
        return login;
    }

    private static string Text(Dictionary<string, object?> login, string key, string where, string fallback) =>
        !login.TryGetValue(key, out var raw) || raw is null
            ? fallback
            : raw as string ?? throw new ConfigException($"{where}.{key} must be a value.");

    private static int Integer(Dictionary<string, object?> login, string key, string where, int fallback) =>
        !login.TryGetValue(key, out var raw) || raw is null
            ? fallback
            : raw as string is { } text && int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ConfigException($"{where}.{key} must be a whole number.");

    private static IReadOnlyList<LoginBurstStep> Steps(Dictionary<string, object?> login, string where)
    {
        var raw = login.TryGetValue("burst", out var listed) && listed is not null
            ? listed as string[] ?? throw new ConfigException($"{where}.burst must be a list of step names.")
            : null;
        if (raw is null)
            return LegacyBurst;
        var steps = new LoginBurstStep[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            steps[i] = raw[i] switch
            {
                "login_reply" => LoginBurstStep.LoginReply,
                "user_stat" => LoginBurstStep.UserStat,
                "user_money_stat" => LoginBurstStep.UserMoneyStat,
                "user_pcroom_stat" => LoginBurstStep.UserPcroomStat,
                "center_list" => LoginBurstStep.CenterList,
                "update_party" => LoginBurstStep.UpdateParty,
                "option_sync" => LoginBurstStep.OptionSync,
                "inventory" => LoginBurstStep.Inventory,
                "main_menu" => LoginBurstStep.MainMenu,
                "equipment_view" => LoginBurstStep.EquipmentView,
                "first_move_channel" => LoginBurstStep.FirstMoveChannel,
                "expired_items" => LoginBurstStep.ExpiredItems,
                _ => throw new ConfigException($"{where}.burst names the unknown step '{raw[i]}'."),
            };
        }
        var expected = raw.Distinct(StringComparer.Ordinal).Count();
        if (expected != raw.Length)
            throw new ConfigException($"{where}.burst names a step twice.");
        return steps;
    }
}

/// <summary>One ordered publication of a build's login burst (<c>login.burst</c> in packets/builds.yaml).</summary>
internal enum LoginBurstStep
{
    /// <summary>The accepted <c>gspRepLogin</c> reply.</summary>
    LoginReply,

    /// <summary>The <c>gspUserStat</c> user state (nickname, level, wallet, avatar color).</summary>
    UserStat,

    /// <summary>The <c>gspUserMoneyStat</c> wallet counters.</summary>
    UserMoneyStat,

    /// <summary>The <c>gspUserPcroomStat</c> room counters.</summary>
    UserPcroomStat,

    /// <summary>The <c>gspCenterList</c> gate table (empty in every profile today).</summary>
    CenterList,

    /// <summary>The solo idle <c>gspUpdateParty</c> so the main menu's plaza gates can pass.</summary>
    UpdateParty,

    /// <summary>The empty <c>gspRepOption</c> option sync.</summary>
    OptionSync,

    /// <summary>The bag family publication (the build's <c>inventory.replaced</c> list).</summary>
    Inventory,

    /// <summary>The main-menu <c>gspGotoRoom</c> install, through the room adapter or the packet tables.</summary>
    MainMenu,

    /// <summary>The equipped view and weapon presets through the <c>LoginEquipmentRequested</c> waterfall.</summary>
    EquipmentView,

    /// <summary>The empty <c>gspNotifyFirstMoveChannel</c>.</summary>
    FirstMoveChannel,

    /// <summary>The expired-item notices, when the row publishes them and the login removed anything.</summary>
    ExpiredItems,
}
