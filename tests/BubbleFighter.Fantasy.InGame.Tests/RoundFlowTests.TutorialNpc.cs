using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Handlers;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Solo tutorials retain their game acknowledgement even when the dialect supplies an ordinary peer form.</summary>
    [Theory]
    [InlineData(RoundNpcOperation.SpawnMember)]
    [InlineData(RoundNpcOperation.SpawnActor)]
    [InlineData(RoundNpcOperation.ReplayActor)]
    [InlineData(RoundNpcOperation.AssignSpawnGroup)]
    public async Task SoloTutorialNpcAcknowledgesTheHostWithPeerMetadata(RoundNpcOperation operation)
    {
        var rig = new Rig();
        try
        {
            await new TutorialRoomFlow(rig.Registry).OpenAsync(rig.Host,
                new TutorialRoundOpened(101, "rel_prologue_01", 300005, [300005], 1, 60000, 99999000, true)
                    { GameConnectionOnly = true }, default);
            Assert.True(rig.Rooms.PlaysSoloTutorial(rig.Host));
            var npc = new Sink<RoundNpcAcknowledged>();
            byte[] body = [1, 2, 3, 4];
            var report = new RoundNpcReported(operation, body)
                { PeerFeed = new(ushort.MaxValue, body) { OnRelay = true } };
            await rig.Rooms.HandleTutorialNpcAsync(rig.Host, report, npc, default);
            var reply = Assert.Single(npc.To(rig.Host));
            Assert.Equal(operation, reply.Operation);
            Assert.Equal(body, reply.Payload.ToArray());
            Assert.Empty(npc.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
