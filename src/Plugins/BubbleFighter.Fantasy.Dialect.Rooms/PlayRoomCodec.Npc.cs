using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class PlayRoomCodec
{
    /// <summary>Resets the recipient's wave actors before installing the host's native spawn table.</summary>
    public async ValueTask SendAsync(ISession session, RoundNpcAcknowledged message, CancellationToken ct)
    {
        if (message.Operation != RoundNpcOperation.SpawnTable)
            throw new InvalidOperationException("The legacy NPC acknowledgement supports only wave tables.");
        // kr-812 sub_A301C0 dispatches 908/909 on the game connection; sub_810C20's relay switch does not.
        // kr-812 sub_A12410 clears actors before sub_A07E90 installs the peer's spawn groups.
        await session.SendAsync(S2C(session, "Unknown908"), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        await session.SendAsync(S2C(session, "gspRepWaveAllSpawnMobs"), message.Payload, ct).ConfigureAwait(false);
    }
}
