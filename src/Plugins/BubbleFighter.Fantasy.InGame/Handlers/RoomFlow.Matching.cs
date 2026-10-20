using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal async ValueTask<bool> TryMatchRoomAsync(ISession session, PlayRoom target, int modeId, CancellationToken ct)
    {
        var joined = false;
        await RunSceneAsync(session, async (group, token) =>
        {
            joined = await TryMatchSceneAsync(session, target, modeId, group, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return joined;
    }

    private async ValueTask<bool> TryMatchSceneAsync(ISession session, PlayRoom target, int modeId, SceneGroup? group, CancellationToken ct)
    {
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
        var equipment = await SeatCharacter.ResolveEquipmentAsync(session, userNum, _selection, _characters, ct).ConfigureAwait(false);
        try
        {
            await target.Executor.ExecuteAsync(async operationCt =>
            {
                if (!ReferenceEquals(_registry.FindJoinable(target.Id), target) || _registry.TryGet(session.Id) is not null)
                    return;
                lock (target.Gate)
                    if (target.ModeId != modeId || target.Password.Length != 0)
                        return;
                await JoinCoreAsync(session, new RoomJoinRequested(target.Id, ""), target, userNum, nickname, equipment,
                    operationCt, notifyRefusal: false, partyTransition: group is not null).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_registry.Find(target.Id) is null)
        {
            return false;
        }
        if (_registry.TryGet(session.Id) is null)
            return false;
        if (PlazaBound)
            await LeavePlazaForRoomAsync(session, ct).ConfigureAwait(false);
        await FollowRoomAsync(group, target, "", ct).ConfigureAwait(false);
        return true;
    }
}
