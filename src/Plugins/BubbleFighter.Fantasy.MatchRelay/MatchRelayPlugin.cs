using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Dialects;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.MatchRelay;

/// <summary>
/// In-round traffic between the players of a room: movement, actions, damager spawns, lives and
/// weapon-preset swaps, on the game and match connections. The rooms themselves belong to the
/// row that publishes <see cref="IRoomRoster"/>; this row reads each room's seats from it and
/// reports team points back through <see cref="IRoundScoreboard"/>.
///
/// </summary>
[Plugin("match-relay",
    Description = "In-round movement, actions, damager spawns, lives and weapon-preset swaps.",
    SupportedBuilds = ["kr:1256"],
    Requires = [typeof(IPacketRouter), typeof(IRoomRoster), typeof(IRoundScoreboard)],
    Optional = [typeof(IPlayerInventoryState)])]
public sealed class MatchRelayPlugin : IPlugin
{
    /// <summary>Gets the round states of the last activation; the in-game tests read seat lives through it.</summary>
    internal RoundStates? Rounds { get; private set; }

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MatchRelayOptions>();
        var router = context.Services.Get<IPacketRouter>();
        var roster = context.Services.Get<IRoomRoster>();
        var scoreboard = context.Services.Get<IRoundScoreboard>();
        context.Services.TryGet<IPlayerInventoryState>(out var playerState);
        if (playerState is not null)
            context.Logger.LogInformation("Match relay persists granted weapon preset slots through a published IPlayerInventoryState.");
        var rounds = new RoundStates(roster, options.MaxRounds);
        Rounds = rounds;
        var flows = new MatchRelayFlow(options, roster, rounds, context.Logger, playerState);
        var game = options.GameProtocol;
        var relay = options.RelayProtocol;

        // C2S 32885 is the in-play movement report. The client sends it on either connection,
        // so both carry the handler.
        context.Effect(router.Register(game, InGameOpcodes.C2SMovementReport, flows.HandleMovementReportAsync));
        // C2S 832 is the in-round weapon-preset swap: the hotkeys sub_B033E0 send it for preset slots 0..4 and
        // the client applies the weapon-model swap only when the S2C 836 reply arrives with status 0, so the
        // handler parses the five-byte body and answers the sender. Registered on both listeners like the
        // combat-hit pair - the reply goes back on whichever session delivered the request.
        context.Effect(router.Register(game, InGameOpcodes.C2SWeaponPresetSwap, flows.HandleWeaponSwapAsync));
        context.Effect(router.Register(relay, InGameOpcodes.C2SWeaponPresetSwap, flows.HandleWeaponSwapAsync));
        // C2S 32923 is the client's high-cadence movement sample: variable-length, mask-gated deltas built by
        // kr-1256 sub_95C250's delta engine. With
        // broadcast_movement_samples on, the handler merges each sample into the sender's per-actor movement
        // state and rebroadcasts the merged full S2C 32885 frame to every OTHER seat; with it off the parsed
        // body is only logged.
        context.Effect(router.Register(game, InGameOpcodes.C2SWanderReport, flows.HandleMovementSampleAsync));
        var life = new MatchLifeFlow(roster, rounds, context.Logger, context.Lifetime);
        context.Effect(router.Register(game, MatchLife1256.ResetRequest, life.HandleResetAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SUnknown41, flows.HandleUnknownInSessionAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SRoomReAsk, flows.HandleUnknownInSessionAsync));
        // C2S 25 here is the weapon-preset post-swap report. sub_6DA420 sends it on the match
        // connection (dword_18E7DAC, which also carries 18-21, 26, 27, 573, 20005), and
        // it is sent only on the relay connection. The empty clan-server C2S 25
        // (sub_525170) goes out on the game connection (dword_18E7F48), so the two never
        // share a protocol. Recorded, never answered: the S2C 836 already confirmed the swap.
        context.Effect(router.Register(relay, InGameOpcodes.C2SPostSwapReport, flows.HandlePostSwapReportAsync));
        // Missing native labels do not invalidate recovered readers. Only the verified
        // allowlist is relayed; remaining layouts and response policies stay log-only.
        var actions = new MatchActionRelay(roster, scoreboard, context.Logger, context.Lifetime, life);
        foreach (var action in MatchAction1256.All)
        {
            context.Effect(router.Register(game, action.InboundOpcode, actions.HandleAsync));
            context.Effect(router.Register(relay, action.InboundOpcode, actions.HandleAsync));
        }
        foreach (var opcode in InGameOpcodes.UnlabeledInSession)
            context.Effect(router.Register(game, opcode, flows.HandleUnknownInSessionAsync));
        // The damager-spawn family: each frame mints a combat damager key in
        // the receiving client's per-slot counters, so the spawns have to be
        // relayed byte-identically and in order for any relayed 38283 hit to
        // resolve on the peers.
        foreach (var opcode in InGameOpcodes.DamagerSpawns)
        {
            context.Effect(router.Register(game, opcode, flows.HandleDamagerSpawnRelayAsync));
            context.Effect(router.Register(relay, opcode, flows.HandleDamagerSpawnRelayAsync));
        }
        // C2S 18 is the mid-round counted-record report built by sub_6CDCB0 from
        // the round object's vector at dword_18E55A0 + 3928 and sent on the match
        // connection (dword_18E7DAC); its per-record wire lives behind an element
        // vtable, so it is logged rather than parsed.
        context.Effect(router.Register(relay, InGameOpcodes.Unknown18, flows.HandleUnknownInSessionAsync));
        // C2S 518 is the in-play latency sample.
        context.Effect(router.Register(game, InGameOpcodes.C2SPingSample, flows.HandlePingSampleAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SGameplayAction, flows.HandleGameplayActionAsync));
        context.Effect(router.Register(game, InGameOpcodes.C2SActionAlternate, flows.HandleAlternateActionAsync));
        // C2S 624 is the death report: the dying client sends {1,0} back-to-back with C2S 625 and the timer
        // paths send {0,0}. The S2C census has no 624 row, so no others-facing death surface is proven; the
        // handler pins the 8-byte body and logs it with room context. Deaths do not end the round - only the
        // round clock does - so it neither relays nor answers.
        context.Effect(router.Register(game, InGameOpcodes.C2SDeathReport, flows.HandleDeathReportAsync));
        context.Effect(router.Register(relay, InGameOpcodes.RelayMotion, flows.HandleRelayMotionAsync));
        context.Effect(router.Register(relay, InGameOpcodes.RelayWander, flows.HandleRelayMotionAsync));
        // The same movement surface under the opcodes the reference's in-game
        // connection actually uses. 20003 and 20004 are a remapping of UDP 32885 and
        // 32923, and a sweep of all thirteen in-game flows finds 20003 and 20004
        // nowhere in either direction while 32923 carries 55,195 client frames and
        // 32885 63,792 server ones. Registering only the remapped pair leaves a
        // client speaking the raw dialect with no handler at all.
        context.Effect(router.Register(relay, InGameOpcodes.C2SMovementReport, flows.HandleMovementReportAsync));
        context.Effect(router.Register(relay, InGameOpcodes.C2SWanderReport, flows.HandleMovementSampleAsync));

        context.Logger.LogInformation("Match relay on {Game} and {Relay}; movement broadcast {Broadcast}, echo {Echo}.",
            game, relay, options.BroadcastMovementSamples, options.EchoMovementReports);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Configuration of the match relay row.</summary>
public sealed class MatchRelayOptions
{
    /// <summary>Gets or sets the protocol of the game connections.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string GameProtocol { get; set; } = "kr-1256/game";

    /// <summary>Gets or sets the protocol of the match connections.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string RelayProtocol { get; set; } = "kr-1256/relay";

    /// <summary>
    /// Gets or sets whether each seat's movement samples (C2S 32923) and full reports (C2S 32885)
    /// are merged into its round state and sent to the other seats as full S2C 32885 frames.
    /// Off, the bodies are only parsed.
    ///
    /// </summary>
    public bool BroadcastMovementSamples { get; set; }

    /// <summary>
    /// Gets or sets whether a C2S 32885 report is answered with the same 21 bytes back to its sender
    /// instead of being sent to the other seats. An experiment switch; keep off.
    ///
    /// </summary>
    public bool EchoMovementReports { get; set; }

    /// <summary>Gets or sets the most rooms whose round state is tracked at once.</summary>
    [Range(1, 65536)]
    public int MaxRounds { get; set; } = 1024;
}
