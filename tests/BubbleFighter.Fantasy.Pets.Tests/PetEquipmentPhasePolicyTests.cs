using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Guards pet equipment at the decision boundary before an allowed selection can reach persistence.</summary>
public sealed class PetEquipmentPhasePolicyTests
{
    private const string Protocol = "kr-1256/game";
    private const int Pet = 301001;
    private const int Ordinary = 190100;

    /// <summary>Pet wear and removal are refused before any downstream decision in unavailable scenes.</summary>
    [Theory]
    [InlineData(SessionPhase.Authenticated, true)]
    [InlineData(SessionPhase.Authenticated, false)]
    [InlineData(SessionPhase.Match, true)]
    [InlineData(SessionPhase.Match, false)]
    [InlineData(SessionPhase.Migrating, true)]
    [InlineData(SessionPhase.Migrating, false)]
    public async Task UnavailablePhaseNeverReachesSelection(SessionPhase phase, bool worn)
    {
        var rig = new Rig(phase);
        var result = await rig.SelectAsync(new(Protocol, 1, Pet, worn));
        Assert.False(result.Allowed);
        Assert.Equal(Pet, result.ItemId);
        Assert.Equal(worn, result.Worn);
        Assert.Equal(0, rig.DownstreamCalls);
        Assert.Equal(0, rig.Writes);
    }

    /// <summary>Both directions retain the downstream decision in each supported equipment scene.</summary>
    [Theory]
    [InlineData(SessionPhase.MainMenu, true)]
    [InlineData(SessionPhase.MainMenu, false)]
    [InlineData(SessionPhase.Plaza, true)]
    [InlineData(SessionPhase.Plaza, false)]
    [InlineData(SessionPhase.Room, true)]
    [InlineData(SessionPhase.Room, false)]
    public async Task SupportedPhasePreservesPetSelection(SessionPhase phase, bool worn)
    {
        var rig = new Rig(phase);
        var expected = new EquipmentSelectionDecision(true, Pet, worn, "accepted by downstream");
        rig.Decide = _ => ValueTask.FromResult(expected);
        Assert.Same(expected, await rig.SelectAsync(new(Protocol, 1, Pet, worn)));
        Assert.Equal(1, rig.DownstreamCalls);
        Assert.Equal(1, rig.Writes);
    }

    /// <summary>A match that starts while another policy is suspended prevents the pending pet mutation.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RechecksPhaseAfterAnAwaitedPolicy(bool worn)
    {
        var rig = new Rig(SessionPhase.Room);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Decide = async ct =>
        {
            entered.SetResult();
            await resume.Task.WaitAsync(ct);
            return new(true, Pet, worn);
        };
        var selection = rig.SelectAsync(new(Protocol, 1, Pet, worn)).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Session.Player.MoveTo(SessionPhase.Match);
        resume.SetResult();
        Assert.False((await selection.WaitAsync(TimeSpan.FromSeconds(5))).Allowed);
        Assert.Equal(0, rig.Writes);
    }

    /// <summary>Retargeting an ordinary item to a pet cannot bypass the match phase check.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetargetedPetCannotCommitInMatch(bool worn)
    {
        var rig = new Rig(SessionPhase.Match);
        rig.Decide = _ => ValueTask.FromResult(new EquipmentSelectionDecision(true, Pet, worn));
        var result = await rig.SelectAsync(new(Protocol, 1, Ordinary, !worn));
        Assert.False(result.Allowed);
        Assert.Equal(Pet, result.ItemId);
        Assert.Equal(worn, result.Worn);
        Assert.Equal(1, rig.DownstreamCalls);
        Assert.Equal(0, rig.Writes);
    }

    /// <summary>The owning session is looked up again after an awaited policy rather than retaining a disconnected owner.</summary>
    [Fact]
    public async Task OwnerRemovedByDownstreamCannotCommitPet()
    {
        var rig = new Rig(SessionPhase.Room);
        rig.Decide = _ =>
        {
            rig.Online.Current = null;
            return ValueTask.FromResult(new EquipmentSelectionDecision(true, Pet, true));
        };
        Assert.False((await rig.SelectAsync(new(Protocol, 1, Pet, true))).Allowed);
        Assert.Equal(0, rig.Writes);
    }

    /// <summary>Missing, closed, mismatched and unaddressable owners cannot authorize pet equipment.</summary>
    [Theory]
    [InlineData("missing", 1L)]
    [InlineData("closed", 1L)]
    [InlineData("identity", 1L)]
    [InlineData("build", 1L)]
    [InlineData("valid", 0L)]
    [InlineData("valid", 2147483648L)]
    public async Task InvalidOwnerCannotReachSelection(string owner, long userNum)
    {
        var rig = new Rig(SessionPhase.MainMenu);
        if (owner == "missing") rig.Online.Current = null;
        if (owner == "closed") rig.Session.Wire.Closed = true;
        if (owner == "identity") rig.Online.Current = new TestSession(SessionPhase.MainMenu, userNum: 2);
        if (owner == "build") rig.Online.Current = new TestSession(SessionPhase.MainMenu, build: "kr-812");
        Assert.False((await rig.SelectAsync(new(Protocol, userNum, Pet, true))).Allowed);
        Assert.Equal(0, rig.DownstreamCalls);
        Assert.Equal(0, rig.Writes);
    }

    /// <summary>An existing downstream refusal keeps its exact diagnostics and target.</summary>
    [Fact]
    public async Task PreservesDownstreamRefusal()
    {
        var rig = new Rig(SessionPhase.Room);
        var expected = new EquipmentSelectionDecision(false, Pet, false, "another policy refused");
        rig.Decide = _ => ValueTask.FromResult(expected);
        Assert.Same(expected, await rig.SelectAsync(new(Protocol, 1, Pet, true)));
        Assert.Equal(0, rig.Writes);
    }

    /// <summary>Ordinary changes retain their existing policy behavior even when no owner is available.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinaryEquipmentDoesNotAcquireAPhaseRestriction(bool worn)
    {
        var rig = new Rig(SessionPhase.Match);
        rig.Online.Current = null;
        var expected = new EquipmentSelectionDecision(true, Ordinary, worn);
        rig.Decide = _ => ValueTask.FromResult(expected);
        Assert.Same(expected, await rig.SelectAsync(new(Protocol, 1, Ordinary, worn)));
        Assert.Equal(0, rig.Online.Lookups);
        Assert.Equal(1, rig.Writes);
    }

    /// <summary>Another protocol's decisions do not consult this build's pet catalog or online directory.</summary>
    [Fact]
    public async Task OtherProtocolPassesThroughUnchanged()
    {
        var rig = new Rig(SessionPhase.Match);
        var expected = new EquipmentSelectionDecision(true, Pet, true);
        rig.Decide = _ => ValueTask.FromResult(expected);
        Assert.Same(expected, await rig.SelectAsync(new("cn-774/game", 1, Pet, true)));
        Assert.Equal(0, rig.Online.Lookups);
        Assert.Equal(0, rig.Entitlements.CatalogReads);
        Assert.Equal(1, rig.Writes);
    }

    private sealed class Rig
    {
        private readonly EventBus _events = new(NullLogger.Instance);
        internal Rig(SessionPhase phase)
        {
            Session = new(phase);
            Online = new() { Current = Session };
            var policy = new PetEquipmentPhasePolicy(Protocol, Online, Entitlements);
            _events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(policy.ApplyAsync);
        }
        internal TestSession Session { get; }
        internal OnlinePlayers Online { get; }
        internal Entitlements Entitlements { get; } = new();
        internal Func<CancellationToken, ValueTask<EquipmentSelectionDecision>>? Decide { get; set; }
        internal int DownstreamCalls { get; private set; }
        internal int Writes { get; private set; }

        internal async ValueTask<EquipmentSelectionDecision> SelectAsync(EquipmentSelectionApplying request)
        {
            var decision = await _events.WaterfallAsync<EquipmentSelectionApplying, EquipmentSelectionDecision>(request, ct =>
            {
                DownstreamCalls++;
                return Decide?.Invoke(ct) ?? ValueTask.FromResult(new EquipmentSelectionDecision(true, request.ItemId, request.Worn));
            }, default);
            if (decision.Allowed) Writes++;
            return decision;
        }
    }

    private sealed class OnlinePlayers : IOnlinePlayers
    {
        internal ISession? Current { get; set; }
        internal int Lookups { get; private set; }
        /// <summary>Counts account lookups and returns the owner currently selected by the phase-policy test.</summary>
        public ISession? FindByUserNum(int userNum) { Lookups++; return Current; }
        /// <summary>Rejects nickname lookup because equipment authorization must locate its owner by account number.</summary>
        public ISession? FindByNickname(string nickname) => throw new NotSupportedException();
    }

    private sealed class Entitlements : IPetEntitlements
    {
        /// <summary>Associates the fixture pet classifier with the Korean build guarded by these phase checks.</summary>
        public string BuildKey => "kr-1256";
        internal int CatalogReads { get; private set; }
        /// <summary>Counts catalog checks and recognizes only the fixture's pet item identifier.</summary>
        public bool IsPetItem(int itemId) { CatalogReads++; return itemId == Pet; }
        /// <summary>Rejects initialization planning because the phase-policy fixture only classifies pet items.</summary>
        public PetInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) => throw new NotSupportedException();
        /// <summary>Rejects inventory preparation so a phase decision cannot perform pet persistence through this fixture.</summary>
        public ValueTask<PetInventorySnapshot> PrepareAsync(long userNum, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>Rejects article resolution because these tests exercise item classification at the equipment policy boundary.</summary>
        public ValueTask<PetEquipmentTarget?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>Rejects article selection so the phase-policy fixture cannot write a pet loadout.</summary>
        public ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>Rejects item selection so the phase-policy fixture cannot write a pet loadout.</summary>
        public ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TestSession : ISessionWithFeatures
    {
        internal TestSession(SessionPhase phase, int userNum = 1, string build = "kr-1256")
        {
            Profile = new(build, build[..2], 0, 0, 0, build);
            Player = new(new(userNum, "player", "player", 1));
            Features = new TestSessionFeatures();
            Features.Register<IPlayerSession>(Player);
            if (phase == SessionPhase.Authenticated) return;
            Player.MoveTo(SessionPhase.MainMenu);
            if (phase == SessionPhase.Match) Player.MoveTo(SessionPhase.Room);
            Player.MoveTo(phase);
        }
        internal PlayerSession Player { get; }
        internal Connection Wire { get; } = new();
        /// <summary>Identifies the fixture's single game session independently of its configurable account identity.</summary>
        public long Id => 1;
        /// <summary>Exposes the connection whose closed state can be changed by ownership tests.</summary>
        public IConnection Connection => Wire;
        /// <summary>Retains the requested build profile so tests can detect a mismatched pet owner.</summary>
        public ClientProfile Profile { get; }
        /// <summary>Exposes the player identity and phase registered when the fixture session was created.</summary>
        public ISessionFeatures Features { get; }
        /// <summary>Provides session-local storage for equipment-policy execution.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Rejects packet sends because a phase decision has no wire publication responsibility.</summary>
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection : IConnection
    {
        internal bool Closed { get; set; }
        /// <summary>Identifies the fixture connection used by the equipment owner checks.</summary>
        public long Id => 1;
        /// <summary>Supplies a loopback endpoint without creating a transport.</summary>
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        /// <summary>Rejects input-pipe access because the tests invoke equipment decisions directly.</summary>
        public PipeReader Input => throw new NotSupportedException();
        /// <summary>Rejects output-pipe access because the tests observe decisions and write counts.</summary>
        public PipeWriter Output => throw new NotSupportedException();
        /// <summary>Provides connection-local storage for the configurable owner connection.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Exposes the closed state that ownership checks must revalidate.</summary>
        public bool IsClosed => Closed;
        /// <summary>Marks the fixture connection closed so subsequent equipment authorization can reject its owner.</summary>
        public ValueTask CloseAsync(string reason) { Closed = true; return ValueTask.CompletedTask; }
    }
}
