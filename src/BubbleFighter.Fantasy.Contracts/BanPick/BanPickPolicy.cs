namespace BubbleFighter.Fantasy.Contracts.BanPick;

/// <summary>
/// The server-wide ban-pick document: named item groups and any number of policies. It has the same shape as the client
/// policy: each policy holds typed rules, rule types state their own conflicts, and the whole document is validated
/// before it is saved or used, so the management interface and the game commands edit one structure.
/// </summary>
/// <param name="Groups">Named item selections that rules and room bans refer to.</param>
/// <param name="Policies">The server-wide policies; each applies to its own builds, modes and rooms.</param>
public sealed record BanPickPolicy(IReadOnlyList<BanPickGroup> Groups, IReadOnlyList<BanPickPolicyEntry> Policies)
{
    /// <summary>The document format number; it changes only when an older reader must refuse the document.</summary>
    public const int FormatVersion = 1;

    /// <summary>A document without groups or policies: nothing is banned.</summary>
    public static BanPickPolicy Empty { get; } = new([], []);
}

/// <summary>
/// A named item selection, resolved separately against each build's own <c>ShopItem2</c> workbook so one name (for
/// example <c>sniper</c>) selects the right items in every client. An item belongs to the group when it matches any of
/// the listed columns.
/// </summary>
/// <param name="Name">The lowercase name used in rules and commands.</param>
/// <param name="MainCategories"><c>ShopItem.mainCategory</c> ids (the <c>MainCategory</c> sheet's <c>id</c>).</param>
/// <param name="SubCategories"><c>ShopItem.subCategory</c> ids (the <c>SubCategory</c> sheet's <c>id</c>).</param>
/// <param name="Functions"><c>ShopItem.function</c> values, such as the pet function.</param>
/// <param name="Items">Exact item ids.</param>
public sealed record BanPickGroup(string Name, IReadOnlyList<int> MainCategories, IReadOnlyList<int> SubCategories,
    IReadOnlyList<int> Functions, IReadOnlyList<int> Items);

/// <summary>One server-wide policy: where it applies and what it bans there.</summary>
/// <param name="Id">A stable identity used by commands and the management interface.</param>
/// <param name="Name">An optional label shown in management views and announcements.</param>
/// <param name="Description">An optional note shown only in management views.</param>
/// <param name="Scope">The builds, modes and rooms the policy applies to.</param>
/// <param name="Rules">The policy's rules; each one is checked against the others for conflicts.</param>
public sealed record BanPickPolicyEntry(string Id, string? Name, string? Description, BanPickScope Scope, IReadOnlyList<BanPickRule> Rules);

/// <summary>Where a policy applies. An empty list means every build or every mode.</summary>
/// <param name="Builds">Exact build ids.</param>
/// <param name="Modes">Room mode ids.</param>
/// <param name="Rooms">Which rooms by password: see <see cref="BanPickRoomScopes"/>.</param>
public sealed record BanPickScope(IReadOnlyList<string> Builds, IReadOnlyList<int> Modes, string Rooms = BanPickRoomScopes.All)
{
    /// <summary>Gets a scope that applies to every build, mode and room.</summary>
    public static BanPickScope Everywhere { get; } = new([], []);

    /// <summary>Gets whether the scope applies outside rooms too, which lets a block rule refuse equipping at once.</summary>
    public bool Unconditional => Modes.Count == 0 && Rooms == BanPickRoomScopes.All;
}

/// <summary>The room selections a policy scope can name.</summary>
public static class BanPickRoomScopes
{
    /// <summary>Every room.</summary>
    public const string All = "all";
    /// <summary>Only rooms without a password; password rooms are exempt.</summary>
    public const string Public = "public";
    /// <summary>Only rooms with a password.</summary>
    public const string Password = "password";

    /// <summary>Whether the value names a room selection.</summary>
    /// <param name="value">The configured value.</param>
    public static bool IsKnown(string? value) => value is All or Public or Password;

    /// <summary>Whether a room with or without a password is selected.</summary>
    /// <param name="scope">The room selection.</param>
    /// <param name="hasPassword">Whether the room has a password.</param>
    public static bool Matches(string scope, bool hasPassword) => scope switch
    {
        All => true,
        Public => !hasPassword,
        Password => hasPassword,
        _ => false,
    };
}

/// <summary>One typed rule: the items and groups it bans and how. Its type is one of <see cref="BanPickRuleTypes"/>.</summary>
/// <param name="Id">A stable identity within its policy.</param>
/// <param name="Type">The rule type name, such as <c>block</c>.</param>
/// <param name="Items">Exact item ids.</param>
/// <param name="Groups">Group names declared in the document.</param>
public sealed record BanPickRule(string Id, string Type, IReadOnlyList<int> Items, IReadOnlyList<string> Groups);

/// <summary>
/// One rule type. Each type states how strongly it applies and which other types it cannot share a target with, so a
/// new type is one subclass here with no shared switch to extend.
/// </summary>
public abstract class BanPickRuleType
{
    /// <summary>Gets the name used in configuration, commands and the companion channel.</summary>
    public abstract string Name { get; }

    /// <summary>Gets the strength: a higher value wins when several rules name the same item.</summary>
    public abstract int Strength { get; }

    /// <summary>Gets whether a matching item stops the round from starting until it is removed.</summary>
    public virtual bool StopsStart => false;

    /// <summary>Gets whether the room host may start anyway after a warning.</summary>
    public virtual bool HostMayConfirm => false;

    /// <summary>Gets whether equipping a matching item is refused while the rule applies.</summary>
    public virtual bool RefusesEquip => false;

    /// <summary>Gets the types that may not name the same item or group in one policy.</summary>
    public virtual IReadOnlyList<string> Conflicts => [];
}

/// <summary>The rule types of document format 1.</summary>
public static class BanPickRuleTypes
{
    /// <summary>Cannot be equipped where the rule applies; a round with it worn cannot start.</summary>
    public const string Block = "block";
    /// <summary>The host is warned and must confirm before the round starts; the same strength as a room ban.</summary>
    public const string Confirm = "confirm";
    /// <summary>Allowed, but the room is told who wears it.</summary>
    public const string Warn = "warn";

    /// <summary>Gets every known type, strongest first.</summary>
    public static IReadOnlyList<BanPickRuleType> All { get; } = [new BlockType(), new ConfirmType(), new WarnType()];

    /// <summary>Finds a type by name.</summary>
    /// <param name="name">The configured type name.</param>
    /// <param name="type">The type, when it is known.</param>
    public static bool TryGet(string? name, out BanPickRuleType type)
    {
        type = All.FirstOrDefault(candidate => candidate.Name == name)!;
        return type is not null;
    }

    /// <summary>Gets a known type.</summary>
    /// <param name="name">The type name.</param>
    /// <exception cref="BanPickPolicyException">The type is unknown.</exception>
    public static BanPickRuleType Get(string name) =>
        TryGet(name, out var type) ? type : throw new BanPickPolicyException("ban_pick_unknown_type");

    private sealed class BlockType : BanPickRuleType
    {
        public override string Name => Block;
        public override int Strength => 3;
        public override bool StopsStart => true;
        public override bool RefusesEquip => true;
        public override IReadOnlyList<string> Conflicts => [Confirm, Warn];
    }

    private sealed class ConfirmType : BanPickRuleType
    {
        public override string Name => Confirm;
        public override int Strength => 2;
        public override bool StopsStart => true;
        public override bool HostMayConfirm => true;
        public override IReadOnlyList<string> Conflicts => [Block, Warn];
    }

    private sealed class WarnType : BanPickRuleType
    {
        public override string Name => Warn;
        public override int Strength => 1;
        public override IReadOnlyList<string> Conflicts => [Block, Confirm];
    }
}

/// <summary>A rejected ban-pick document, with a stable code the management interface and commands map to a message.</summary>
public sealed class BanPickPolicyException : ArgumentException
{
    /// <summary>Creates a rejection.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="policy">The policy the problem is in, when it is in one.</param>
    /// <param name="rule">The first rule or group involved.</param>
    /// <param name="other">The second rule, for conflicts.</param>
    public BanPickPolicyException(string code, string? policy = null, string? rule = null, string? other = null) : base(code)
    {
        Code = code; Policy = policy; Rule = rule; Other = other;
    }

    /// <summary>Gets the stable code: <c>ban_pick_invalid</c>, <c>ban_pick_unknown_type</c>, <c>ban_pick_unknown_group</c>,
    /// <c>ban_pick_conflict</c>, <c>ban_pick_revision_conflict</c>, <c>ban_pick_too_large</c>, or for a command edit
    /// <c>ban_pick_unknown_target</c>, <c>ban_pick_unknown_policy</c> and <c>ban_pick_group_in_use</c>.</summary>
    public string Code { get; }
    /// <summary>Gets the policy identity involved, if any.</summary>
    public string? Policy { get; }
    /// <summary>Gets the first rule or group identity involved, if any.</summary>
    public string? Rule { get; }
    /// <summary>Gets the conflicting rule identity, if any.</summary>
    public string? Other { get; }
}

/// <summary>Validates a ban-pick document before it is saved or a row starts with it.</summary>
public static class BanPickPolicyRules
{
    /// <summary>Rejects malformed entries, unknown types or groups, and rules of conflicting types naming one target.</summary>
    /// <param name="policy">The complete candidate.</param>
    /// <exception cref="BanPickPolicyException">The document is rejected; the code says why.</exception>
    public static void Validate(BanPickPolicy policy)
    {
        if (policy?.Groups is null || policy.Policies is null)
            throw new BanPickPolicyException("ban_pick_invalid");
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in policy.Groups)
        {
            if (group is null || !Name(group.Name) || !groups.Add(group.Name) || group.MainCategories is null
                || group.SubCategories is null || group.Functions is null || group.Items is null
                || group.MainCategories.Count + group.SubCategories.Count + group.Functions.Count + group.Items.Count == 0
                || group.Items.Any(item => item <= 0))
                throw new BanPickPolicyException("ban_pick_invalid", rule: group?.Name);
        }
        var policies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in policy.Policies)
        {
            if (entry is null || !Identity(entry.Id) || !policies.Add(entry.Id) || entry.Rules is null || entry.Scope is null
                || !Text(entry.Name, false) || !Text(entry.Description, true) || !ValidScope(entry.Scope))
                throw new BanPickPolicyException("ban_pick_invalid", entry?.Id);
            var rules = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in entry.Rules)
            {
                if (rule is null || !Identity(rule.Id) || !rules.Add(rule.Id) || rule.Items is null || rule.Groups is null
                    || rule.Items.Count + rule.Groups.Count == 0 || rule.Items.Any(item => item <= 0)
                    || rule.Items.Distinct().Count() != rule.Items.Count
                    || rule.Groups.Distinct(StringComparer.Ordinal).Count() != rule.Groups.Count)
                    throw new BanPickPolicyException("ban_pick_invalid", entry.Id, rule?.Id);
                if (!BanPickRuleTypes.TryGet(rule.Type, out _))
                    throw new BanPickPolicyException("ban_pick_unknown_type", entry.Id, rule.Id);
                if (rule.Groups.FirstOrDefault(name => !groups.Contains(name)) is not null)
                    throw new BanPickPolicyException("ban_pick_unknown_group", entry.Id, rule.Id);
            }
            for (var i = 0; i < entry.Rules.Count; i++)
            for (var j = i + 1; j < entry.Rules.Count; j++)
            {
                var a = entry.Rules[i]; var b = entry.Rules[j];
                if (!SharesTarget(a, b)) continue;
                if (a.Type == b.Type || BanPickRuleTypes.Get(a.Type).Conflicts.Contains(b.Type, StringComparer.Ordinal))
                    throw new BanPickPolicyException("ban_pick_conflict", entry.Id, a.Id, b.Id);
            }
        }
    }

    /// <summary>Whether a scope names a valid set of builds, modes and rooms.</summary>
    /// <param name="scope">The candidate scope.</param>
    public static bool ValidScope(BanPickScope scope) => scope.Builds is not null && scope.Modes is not null
        && scope.Builds.All(BuildId) && scope.Builds.Distinct(StringComparer.Ordinal).Count() == scope.Builds.Count
        && scope.Modes.All(mode => mode >= 0) && scope.Modes.Distinct().Count() == scope.Modes.Count
        && BanPickRoomScopes.IsKnown(scope.Rooms);

    /// <summary>Whether a group or command name is a valid lowercase identifier.</summary>
    /// <param name="name">The candidate name.</param>
    public static bool Name(string? name) => name is { Length: > 0 and <= 64 } && char.IsAsciiLetterLower(name[0]) &&
        name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    /// <summary>Whether a policy or rule identity is valid.</summary>
    /// <param name="id">The candidate identity.</param>
    public static bool Identity(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool SharesTarget(BanPickRule a, BanPickRule b) =>
        a.Items.Intersect(b.Items).Any() || a.Groups.Intersect(b.Groups, StringComparer.Ordinal).Any();

    private static bool BuildId(string? id) => id is { Length: > 0 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool Text(string? value, bool multiline) => value is null ||
        !value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'));
}

/// <summary>A saved ban-pick document and the revision that edits must name.</summary>
/// <param name="Revision">Increases by one with every save.</param>
/// <param name="Policy">The validated document.</param>
public sealed record BanPickPolicyState(long Revision, BanPickPolicy Policy);

/// <summary>
/// The server-wide ban-pick document, readable by every build's room rules and editable by administrators. Every save
/// names the revision it edited, so two editors cannot silently overwrite each other.
/// </summary>
public interface IBanPickPolicyStore
{
    /// <summary>Gets the current document.</summary>
    BanPickPolicyState Current { get; }

    /// <summary>Validates and saves a complete document when <paramref name="expectedRevision"/> is still current.</summary>
    /// <param name="expectedRevision">The revision the edit started from.</param>
    /// <param name="policy">The complete new document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="BanPickPolicyException">The document is invalid, or <c>ban_pick_revision_conflict</c>.</exception>
    /// <exception cref="NotSupportedException">The deployment configured no state file, so the document is read-only.</exception>
    ValueTask<BanPickPolicyState> SaveAsync(long expectedRevision, BanPickPolicy policy, CancellationToken cancellationToken);
}
