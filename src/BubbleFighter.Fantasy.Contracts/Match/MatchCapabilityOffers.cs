using System.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Offers a room-issued native credential and the deployment's advertised IPv4 match endpoint.</summary>
/// <param name="Address">Advertised IPv4 address, independent of the listener's bind address.</param>
/// <param name="Port">Numeric TCP port advertised to the client.</param>
/// <param name="Ticket">Owned native credential for this seat and round.</param>
public sealed record MatchCapabilityOffered(IPAddress Address, ushort Port, ReadOnlyMemory<byte> Ticket) : IMessage;

/// <summary>Encodes native offers and exposes the credential size required by its audited packet table.</summary>
public interface IMatchCapabilityOfferSender : IMessageSender<MatchCapabilityOffered>
{
    /// <summary>Gets the exact native key length derived from the dialect's packet structure.</summary>
    int TicketLength { get; }
}

/// <summary>Reports game-side match connection success without granting or replacing a relay lease.</summary>
/// <param name="Connected">Native success flag emitted after the relay acknowledgement.</param>
public sealed record MatchConnectionReported(bool Connected) : IMessage;
