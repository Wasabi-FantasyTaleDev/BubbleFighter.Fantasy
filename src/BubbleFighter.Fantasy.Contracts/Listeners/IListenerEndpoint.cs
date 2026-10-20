using System.Net;

namespace BubbleFighter.Fantasy.Contracts.Listeners;

/// <summary>
/// One transport listener that is currently bound, published by the row that owns it as an
/// ordered extension (one registration per listening row). The registration exists exactly as
/// long as the socket is bound, so reading the list answers both which port serves which
/// connection protocol and whether anything is listening there.
/// </summary>
public interface IListenerEndpoint
{
    /// <summary>Gets the id of the plugin row that owns the listener.</summary>
    string RowId { get; }

    /// <summary>Gets the name of the connection protocol the listener hands connections to, for example <c>kr-1256/game</c>.</summary>
    string Protocol { get; }

    /// <summary>Gets the endpoint the socket is actually bound to, so a configured port of 0 reports the port the system chose.</summary>
    IPEndPoint LocalEndPoint { get; }
}
