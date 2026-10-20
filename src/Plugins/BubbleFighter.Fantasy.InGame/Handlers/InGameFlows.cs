using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The room and round handlers of one activated row, wired to each other: room setup hands the
/// start click to the round start, and the start arms the round clock the result owns. In-round
/// traffic between the players belongs to the match-relay row.
/// </summary>
internal sealed class InGameFlows
{
    /// <summary>Builds and wires the room and round handlers.</summary>
    /// <param name="options">The row configuration.</param>
    /// <param name="registry">The room registry the handlers drive.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="characters">Optional equipped-character resolver.</param>
    /// <param name="equippedView">Optional publisher for the S2C 806 equipped view sent with the room push.</param>
    /// <param name="lifetime">Cancellation when the owning plugin unloads.</param>
    /// <param name="signatureSender">Main-connection adapter that initializes the relay registration prerequisite.</param>
    /// <param name="selection">Current persisted equipment used for every newly created scene.</param>
    /// <param name="maps">The client's map pools a round's map is drawn from.</param>
    /// <param name="progress">The level / experience store round rewards are recorded in.</param>
    /// <param name="partySender">The dialect sender that retains membership during scene appearance refreshes.</param>
    /// <param name="events">The bus a level rise is announced on.</param>
    public InGameFlows(
        InGameOptions options,
        RoomRegistry registry,
        ILogger logger,
        ISeatCharacterResolver? characters = null,
        IEquippedViewPush? equippedView = null,
        CancellationToken lifetime = default,
        IMessageSender<MatchSessionSignature>? signatureSender = null,
        IEquipmentSelection? selection = null,
        MapCatalog? maps = null,
        IPlayerProgressRepository? progress = null,
        IMessageSender<PartyUpdated>? partySender = null,
        IEventBus? events = null)
    {
        Result = new MatchResultFlow(options, registry, logger, lifetime, progress, events);
        Start = new MatchStartFlow(options, registry, logger, Result, lifetime, signatureSender, maps);
        Setup = new RoomSetupFlow(options, registry, logger, Start, characters, selection, equippedView, partySender);
    }

    /// <summary>Gets the room-setting handlers.</summary>
    public RoomSetupFlow Setup { get; }

    /// <summary>Gets the round start.</summary>
    public MatchStartFlow Start { get; }

    /// <summary>Gets the round end.</summary>
    public MatchResultFlow Result { get; }
}
