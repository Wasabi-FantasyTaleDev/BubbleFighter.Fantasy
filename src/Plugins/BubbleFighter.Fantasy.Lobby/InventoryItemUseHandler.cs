using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Lobby.Data;

namespace BubbleFighter.Fantasy.Lobby;

internal sealed class InventoryItemUseHandler(IPlayerRepository players, IMessageSender<InventoryItemUseReplied> replies,
    Func<ISession, long, CancellationToken, ValueTask>? pushInventory, IEventBus events, int renameItemId,
    IPaintItemCatalog? paints = null, Func<ISession, PlayerRecord, CancellationToken, ValueTask>? pushStat = null)
{
    internal async ValueTask HandleAsync(ISession session, InventoryItemUseRequested request, CancellationToken ct)
    {
        if (!session.TryFeature<IPlayerSession>(out var player))
            throw new InvalidOperationException("Inventory use requires an authenticated player.");
        if (paints is not null && paints.TryGetColorIndex(request.ItemId, out var color))
        {
            await ApplyPaintAsync(session, player, request, color, ct).ConfigureAwait(false);
            return;
        }
        const string prefix = "changeUserName=";
        var result = 1;
        var nickname = "";
        if (request.ItemId == renameItemId && request.Quantity == 1 && request.Parameters.StartsWith(prefix, StringComparison.Ordinal))
        {
            nickname = request.Parameters[prefix.Length..];
            // cn-324 sub_70B770 reports native length error 11; names remain SQL parameters.
            result = nickname.Length is < 2 or > 12 ? 11
                : await players.RenameWithItemAsync(player.Identity.UserNum, nickname, request.ItemId, ct).ConfigureAwait(false);
        }
        await replies.SendAsync(session, new(result, result == 0 ? prefix + nickname : "") { ItemId = request.ItemId }, ct).ConfigureAwait(false);
        if (result == 0)
        {
            player.ChangeNickname(nickname);
            await events.SerialAsync(new PlayerNicknameChanged(player.Identity.UserNum, nickname), ct).ConfigureAwait(false);
            if (pushInventory is not null)
                await pushInventory(session, player.Identity.UserNum, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyPaintAsync(ISession session, IPlayerSession player, InventoryItemUseRequested request,
        ushort color, CancellationToken ct)
    {
        if (pushStat is null || pushInventory is null)
            throw new InvalidOperationException("Paint use requires inventory and local-state publications.");
        var accepted = request.Quantity == 1 && request.Parameters.Length == 0
            && player.Phase is SessionPhase.MainMenu or SessionPhase.Room or SessionPhase.Plaza
            && await players.ApplyPaintWithItemAsync(player.Identity.UserNum, request.ItemId, color, ct).ConfigureAwait(false);
        await replies.SendAsync(session, new(accepted ? 0 : 1, "") { ItemId = request.ItemId }, ct).ConfigureAwait(false);
        if (!accepted) return;
        var committed = await players.FindByUsernameAsync(player.Identity.Username, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The painted player no longer exists.");
        if (committed.UserNum != player.Identity.UserNum || committed.ColorIndex != color)
            throw new InvalidOperationException("The paint commit does not match the authenticated player state.");
        player.ChangeColorIndex(color);
        await pushInventory(session, player.Identity.UserNum, ct).ConfigureAwait(false);
        await pushStat(session, committed, ct).ConfigureAwait(false);
        await events.SerialAsync(new PlayerColorChanged(player.Identity.UserNum, color), ct).ConfigureAwait(false);
    }
}
