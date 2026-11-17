using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The clan requests of a logged-in clan connection that read or found a clan: home, profile and create. Every
/// handler follows the same steps, which the other clan handler classes copy: resolve who is asking from the
/// connection's login, read the directory, answer with the one frame the client's reader expects, and let the
/// client ask for what it wants next (it re-requests its home after every change, kr-1256 sub_526E90).
/// </summary>
internal sealed class ClanDirectoryHandlers(
    IClanDirectory clans,
    IClanCreationCards cards,
    IInventoryRepository inventory,
    IMessageSender<ClanHomeSent> home,
    IMessageSender<ClanProfileSent> profile,
    IMessageSender<ClanCreateAnswered> created,
    ILogger logger)
{
    // The create dialog's own limits, checked before it sends (kr-1256 sub_5C3740): a name of 4 to 12 characters
    // and an introduction of 1 to 96.
    internal const int MinNameLength = 4;
    internal const int MaxNameLength = 12;
    internal const int MinIntroductionLength = 1;
    internal const int MaxIntroductionLength = 96;

    /// <summary>Answers the player's own clan state.</summary>
    public async ValueTask HomeAsync(ISession session, ClanHomeRequested _, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var member = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        var clan = member is null ? null : await clans.FindAsync(member.ClanId, ct).ConfigureAwait(false);
        if (member is null || clan is null)
        {
            await home.SendAsync(session, new ClanHomeSent(false, 0, 0, "", null), ct).ConfigureAwait(false);
            return;
        }
        // The roster shows the name and level the row carries, so each home request brings them up to date.
        if (member.Nickname != caller.Nickname || member.Level != caller.Level)
            await clans.RefreshMemberAsync(caller.UserNum, caller.Nickname, caller.Level, ct).ConfigureAwait(false);
        // Only a seated member has a profile to show; an applicant's home states the clan applied to and stops.
        await home.SendAsync(session,
            new ClanHomeSent(ClanRole.IsMember(member.Role), clan.ClanId, member.Role, clan.Name, clan.DisbandAt), ct).ConfigureAwait(false);
    }

    /// <summary>Answers the profile of the clan the player is seated in. A player without a seat is sent nothing.</summary>
    public async ValueTask ProfileAsync(ISession session, ClanProfileRequested _, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var member = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        var clan = member is null || !ClanRole.IsMember(member.Role) ? null : await clans.FindAsync(member.ClanId, ct).ConfigureAwait(false);
        if (clan is null)
        {
            // The client asks only after a home that told it to (kr-1256 sub_526330), so this is a member who
            // left between the two frames. Its next home request states the new truth.
            logger.LogDebug("Clan connection {Id} user={User} asked for a clan profile without a seat; nothing sent.", session.Id, caller.UserNum);
            return;
        }
        var members = await clans.ListMembersAsync(clan.ClanId, ct).ConfigureAwait(false);
        var master = members.FirstOrDefault(row => row.Role == ClanRole.Master);
        var deputy = members.FirstOrDefault(row => row.Role == ClanRole.Deputy);
        await profile.SendAsync(session,
            new ClanProfileSent(clan, master?.Nickname ?? "", deputy?.UserNum ?? 0, deputy?.Nickname ?? ""), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Founds a clan with the caller as master, with the creation card the request names. The checks the client
    /// has a notice for come first, each answered with its own result; the directory then decides the two races
    /// (a taken name, a founder who already has a row) in one step. The card is consumed after the clan exists,
    /// and a card that is no longer there undoes the clan, so a refusal never leaves a clan or takes a card.
    /// </summary>
    public async ValueTask CreateAsync(ISession session, ClanCreateRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var name = request.Name.Trim();
        var refusal = await RefusalAsync(session, caller, name, request, ct).ConfigureAwait(false);
        if (refusal is null)
        {
            cards.TryGetCapacity(request.Item, out var capacity);
            var clan = await clans.CreateAsync(name, request.Introduction, capacity, caller.UserNum, caller.Nickname, caller.Level, ct)
                .ConfigureAwait(false);
            if (clan is null)
                refusal = await ConflictAsync(caller.UserNum, name, ct).ConfigureAwait(false) ?? ClanCreateAnswered.Failed;
            else if (await inventory.ConsumeItemAsync(caller.UserNum, request.Item, ct).ConfigureAwait(false) is null)
            {
                await clans.DeleteAsync(clan.ClanId, ct).ConfigureAwait(false);
                logger.LogInformation("Clan connection {Id} user={User} has no usable creation card {Item}; clan '{Name}' was not founded.",
                    session.Id, caller.UserNum, request.Item, name);
                refusal = ClanCreateAnswered.Failed;
            }
            else
            {
                logger.LogInformation("Clan {Clan} '{Name}' founded by user={User} with card {Item} (capacity {Capacity}).",
                    clan.ClanId, clan.Name, caller.UserNum, request.Item, capacity);
                // The client takes the card out of its own inventory view and looks it up in its item table
                // (kr-1256 sub_517FE0 case 1), so the success names the card it was made with.
                await created.SendAsync(session, new ClanCreateAnswered(ClanCreateAnswered.Created, request.Item, capacity), ct)
                    .ConfigureAwait(false);
                return;
            }
        }
        logger.LogInformation("Clan connection {Id} user={User} could not found clan '{Name}': result {Result}.",
            session.Id, caller.UserNum, name, refusal);
        await created.SendAsync(session, new ClanCreateAnswered(refusal.Value, 0, 0), ct).ConfigureAwait(false);
    }

    // The refusal of a request that cannot found a clan as it stands, or null when it may.
    private async ValueTask<int?> RefusalAsync(ISession session, ClanCaller caller, string name, ClanCreateRequested request, CancellationToken ct)
    {
        if (name.Length is < MinNameLength or > MaxNameLength)
            return ClanCreateAnswered.NameLength;
        if (request.Introduction.Length is < MinIntroductionLength or > MaxIntroductionLength)
            return ClanCreateAnswered.IntroductionLength;
        // A build that sends the card's own capacity sends exactly that word, so another word is not the dialog's request.
        if (!cards.TryGetCapacity(request.Item, out var capacity) || (request.Capacity is { } sent && sent != capacity))
        {
            logger.LogWarning("Clan connection {Id} user={User} asked to found a clan with item {Item} and capacity {Capacity}, which is not a creation card of this build.",
                session.Id, caller.UserNum, request.Item, request.Capacity);
            return ClanCreateAnswered.Failed;
        }
        return await ConflictAsync(caller.UserNum, name, ct).ConfigureAwait(false);
    }

    // The notice for a founder who already has a row or a name that is taken; null when neither holds.
    private async ValueTask<int?> ConflictAsync(long userNum, string name, CancellationToken ct)
    {
        if (await clans.FindMemberAsync(userNum, ct).ConfigureAwait(false) is { } member)
            return ClanRole.IsMember(member.Role) ? ClanCreateAnswered.AlreadyMember : ClanCreateAnswered.AlreadyApplied;
        return await clans.FindByNameAsync(name, ct).ConfigureAwait(false) is null ? null : ClanCreateAnswered.NameTaken;
    }
}

/// <summary>Who a clan request is from: the account of the clan connection's login and its game session's current name and level.</summary>
/// <param name="UserNum">The player id.</param>
/// <param name="Nickname">The player's current nickname.</param>
/// <param name="Level">The player's current level.</param>
internal readonly record struct ClanCaller(long UserNum, string Nickname, int Level)
{
    /// <summary>
    /// Resolves the caller of a clan request. A request on a connection that has not logged in is a protocol
    /// error: the client sends none before its login is accepted (kr-1256 sub_525170 case 0x3EE).
    /// </summary>
    public static ClanCaller Require(ISession session)
    {
        if (!session.TryFeature<ClanConnectionIdentity>(out var identity))
            throw new InvalidOperationException($"Clan connection {session.Id} sent a clan request before logging in.");
        // The game session knows the player's level and a nickname changed since the passport was issued.
        return identity.Game.TryFeature<IPlayerSession>(out var player)
            ? new ClanCaller(identity.UserNumber, player.Identity.Nickname, player.Identity.Level)
            : new ClanCaller(identity.UserNumber, identity.Nickname, 0);
    }
}
