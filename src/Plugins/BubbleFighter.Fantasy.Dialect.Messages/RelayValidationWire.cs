using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Decodes audited native relay validation bodies without granting a connection or registering an opcode.</summary>
internal static class RelayValidationWire
{
    /// <summary>
    /// Projects only the native actor and display identity after validating the entire report.
    /// kr-812 sub_810C20 copies actor+404; cn-324 sub_6FA120 uses actor+384.
    /// The report supplies no independently proven login account identity.
    /// </summary>
    internal static NativeRelaySceneIdentity ReadIdentity(PacketTables tables, string build,
        ReadOnlySpan<byte> body, PacketReadLimits? limits = null)
    {
        var report = ReadReport(tables, build, body, limits);
        return new((int)report["entity_id"]!, (string)report["nickname"]!);
    }

    /// <summary>
    /// Reads the complete field tree in packets/relay-validation.yaml, including nonempty equipment,
    /// preset and signature vectors. kr-812 sub_810140/sub_8101E0 establish the baseline;
    /// cn-324 sub_6F9E70/sub_6F9ED0 omit presets/signature and cn-717 sub_895580 adds a bool32 tail.
    /// CN2011's native relay-1 consumer cn-279 sub_701270 goes directly to game-side success.
    /// </summary>
    internal static Dictionary<string, object?> ReadReport(PacketTables tables, string build,
        ReadOnlySpan<byte> body, PacketReadLimits? limits = null)
    {
        if (!tables.CapabilityOf(build, "relay_validation_report")
            || tables.Resolve(build, "RelayValidationReport").Count == 0)
            throw new ConfigException($"Build '{build}' has no native relay validation report layout.");
        return PacketCodec.Read(tables, build, "RelayValidationReport", body, limits);
    }
}

/// <summary>The scene identity carried by a decoded report, without granting a room lease or account identity.</summary>
/// <param name="EntityId">Native actor identity copied from the local player.</param>
/// <param name="Nickname">Display identity copied from the logged-in profile.</param>
internal sealed record NativeRelaySceneIdentity(int EntityId, string Nickname);
