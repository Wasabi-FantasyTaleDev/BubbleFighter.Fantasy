using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Opens a pending native game connection while the client retains its current connection.</summary>
/// <param name="Ipv4">The prepared destination's IPv4 listener address.</param>
/// <param name="Port">The prepared destination's nonzero game listener port.</param>
public sealed record ChannelMoveOffered(System.Net.IPAddress Ipv4, ushort Port) : IMessage;

/// <summary>A native connection asks to retain an existing identity; this message alone grants no account authority.</summary>
/// <param name="RetainedPlayerId">The unsigned client account word widened without changing its bits.</param>
/// <param name="StageWord">The client's boolean stage condition, validated as zero or one by the dialect.</param>
public sealed record MoveIntoRequested(long RetainedPlayerId, uint StageWord) : IMessage;

/// <summary>Reports whether destination authorization and player-state installation succeeded.</summary>
/// <param name="Accepted">Whether the destination admitted this migration.</param>
/// <param name="Channel">The channel selected according to the client's consuming path.</param>
public sealed record MoveIntoReplied(bool Accepted, int Channel) : IMessage;

/// <summary>Promotes the client's pending connection after successful destination admission.</summary>
public sealed record PendingConnectionPromoted : IMessage;

/// <summary>The client acknowledged promotion on the connection that received its promotion frame.</summary>
public sealed record PendingPromotionAcknowledged : IMessage;

/// <summary>Releases the client's retained source connection after its promotion acknowledgement.</summary>
public sealed record PendingPromotionCompleted : IMessage;
