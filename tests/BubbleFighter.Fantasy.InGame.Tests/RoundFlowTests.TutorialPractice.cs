using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    [Fact]
    public async Task PracticeRestoresOnlyTheAuthenticatedPlayersSuccessfulMap()
    {
        var rig = new Rig(timeLimitMs: 5);
        var store = new PracticeStore();
        var sender = new PracticeSender();
        var flow = new TutorialPracticeFlow(store, sender, "cn-279", "rel_Tutorial_01");
        try
        {
            await flow.PublishAsync(rig.Host, default);
            Assert.False(Assert.Single(sender.Sent).Cleared);
            await flow.ClearAsync(rig.Host, new("rel_prologue_01"), default);
            Assert.Empty(store.Cleared);
            await flow.ClearAsync(rig.Host, new("REL_TUTORIAL_01"), default);
            Assert.True(sender.Sent[^1].Cleared);
            await flow.PublishAsync(rig.Guest, default);
            Assert.False(sender.Sent[^1].Cleared);
            var restarted = new TutorialPracticeFlow(store, sender, "cn-279", "rel_Tutorial_01");
            await restarted.PublishAsync(rig.Host, default);
            Assert.True(sender.Sent[^1].Cleared);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private sealed class PracticeSender : IMessageSender<TutorialPracticeProgress>
    {
        public string Protocol => "cn-279/game";
        public List<TutorialPracticeProgress> Sent { get; } = [];
        public ValueTask SendAsync(ISession session, TutorialPracticeProgress message, CancellationToken ct)
        {
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PracticeStore : ITutorialPracticeStore
    {
        public HashSet<TutorialPracticeKey> Cleared { get; } = [];
        public ValueTask<bool> HasClearedAsync(TutorialPracticeKey key, CancellationToken ct) => ValueTask.FromResult(Cleared.Contains(key));
        public ValueTask<bool> RecordClearAsync(TutorialPracticeKey key, CancellationToken ct) => ValueTask.FromResult(Cleared.Add(key));
    }
}
