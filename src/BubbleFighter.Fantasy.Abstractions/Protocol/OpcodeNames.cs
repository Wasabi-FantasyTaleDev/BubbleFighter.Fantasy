namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>
/// Resolves packets by the name their build's opcode table gives them. Builds renumber the
/// packet enums, so logic that runs on several builds names its packets and lets each
/// profile's <c>opcodes.yaml</c> supply the numbers.
/// </summary>
public static class OpcodeNames
{
    /// <summary>Gets the opcode table view of the profile and connection a protocol names.</summary>
    /// <param name="tables">The opcode table registry.</param>
    /// <param name="protocol">A connection protocol named <c>&lt;profile&gt;/&lt;connection&gt;</c>, such as <c>kr-1256/game</c> or <c>kr-1256/relay</c>.</param>
    /// <returns>The view that resolves only that connection's rows.</returns>
    /// <exception cref="ConfigException">Thrown when the protocol name has no profile part, names an unknown connection, or no table is registered for it.</exception>
    public static IOpcodeTable ForProtocol(this IOpcodeTableRegistry tables, string protocol)
    {
        var (profile, connection) = ParseProtocol(protocol);
        return tables.TryGet(profile, out var table) && table is not null
            ? table.ForConnection(connection)
            : throw new ConfigException($"No opcode table is registered for profile '{profile}' (protocol '{protocol}'); mount its protocol row.");
    }

    /// <summary>Attempts to get a protocol's opcode table view without throwing.</summary>
    /// <param name="tables">The opcode table registry.</param>
    /// <param name="protocol">A connection protocol named <c>&lt;profile&gt;/&lt;connection&gt;</c>.</param>
    /// <param name="table">Receives the view, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the protocol is well formed and its profile has a table.</returns>
    public static bool TryForProtocol(this IOpcodeTableRegistry tables, string protocol, out IOpcodeTable? table)
    {
        table = null;
        var slash = protocol.IndexOf('/');
        if (slash <= 0 || !TryParseConnection(protocol[(slash + 1)..], out var connection)
            || !tables.TryGet(protocol[..slash], out var full) || full is null)
            return false;
        table = full.ForConnection(connection);
        return true;
    }

    /// <summary>Splits a protocol name into its profile id and connection.</summary>
    /// <param name="protocol">A connection protocol named <c>&lt;profile&gt;/&lt;connection&gt;</c>.</param>
    /// <returns>The profile id and the connection.</returns>
    /// <exception cref="ConfigException">Thrown when the name has no profile part or its connection is neither <c>game</c> nor <c>relay</c>.</exception>
    public static (string Profile, OpcodeConnection Connection) ParseProtocol(string protocol)
    {
        var slash = protocol.IndexOf('/');
        if (slash <= 0)
            throw new ConfigException($"Connection protocol '{protocol}' does not name a profile (expected '<profile>/<connection>').");
        return TryParseConnection(protocol[(slash + 1)..], out var connection)
            ? (protocol[..slash], connection)
            : throw new ConfigException($"Connection protocol '{protocol}' names connection '{protocol[(slash + 1)..]}'; expected 'game' or 'relay'.");
    }

    private static bool TryParseConnection(string text, out OpcodeConnection connection)
    {
        connection = text switch { "game" => OpcodeConnection.Game, "relay" => OpcodeConnection.Relay, _ => OpcodeConnection.Both };
        return connection != OpcodeConnection.Both;
    }

    /// <summary>Finds the opcode a table assigns to a packet name.</summary>
    /// <param name="table">The opcode table.</param>
    /// <param name="direction">The direction the packet travels.</param>
    /// <param name="name">The packet name, a client label or <c>UnknownNNN</c>.</param>
    /// <returns>The opcode, or <see langword="null"/> when the build has no such packet.</returns>
    public static ushort? Find(this IOpcodeTable table, PacketDirection direction, string name) =>
        table.All.FirstOrDefault(e => e.Direction == direction && e.Name == name)?.Value
        ?? (table is IOpcodeNameFallback fallback ? fallback.ResolveMissingName(direction, name) : null);

    /// <summary>Gets the opcode a table assigns to a packet name.</summary>
    /// <param name="table">The opcode table.</param>
    /// <param name="direction">The direction the packet travels.</param>
    /// <param name="name">The packet name, a client label or <c>UnknownNNN</c>.</param>
    /// <returns>The opcode.</returns>
    /// <exception cref="ConfigException">Thrown when the table has no row with that name.</exception>
    public static ushort Require(this IOpcodeTable table, PacketDirection direction, string name) =>
        table.Find(direction, name)
            ?? throw new ConfigException($"Opcode table '{table.Profile.Id}' has no {(direction == PacketDirection.ClientToServer ? "C2S" : "S2C")} {name} row.");

    /// <summary>Gets the client-to-server opcode of a packet name.</summary>
    /// <param name="table">The opcode table.</param>
    /// <param name="name">The packet name.</param>
    /// <returns>The opcode.</returns>
    /// <exception cref="ConfigException">Thrown when the table has no such C2S row.</exception>
    public static ushort C2S(this IOpcodeTable table, string name) => table.Require(PacketDirection.ClientToServer, name);

    /// <summary>Gets the server-to-client opcode of a packet name.</summary>
    /// <param name="table">The opcode table.</param>
    /// <param name="name">The packet name.</param>
    /// <returns>The opcode.</returns>
    /// <exception cref="ConfigException">Thrown when the table has no such S2C row.</exception>
    public static ushort S2C(this IOpcodeTable table, string name) => table.Require(PacketDirection.ServerToClient, name);
}
