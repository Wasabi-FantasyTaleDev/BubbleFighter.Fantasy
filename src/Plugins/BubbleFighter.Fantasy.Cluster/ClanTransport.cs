using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>One relayed clan notice between a node and the directory; the directory never interprets its body.</summary>
/// <param name="Kind">The typed message the body holds, by its contract type name.</param>
/// <param name="Recipients">The members to tell, by user number.</param>
/// <param name="Body">The message as JSON.</param>
internal sealed record ClanWireNotice(string Kind, long[] Recipients, string Body);

/// <summary>A node's queued notices.</summary>
internal sealed record ClanWirePost(ClanWireNotice[] Notices);

/// <summary>The notices other nodes published after the reader's cursor.</summary>
/// <param name="Hub">The directory incarnation; a new one means the directory restarted and the cursor starts over.</param>
/// <param name="Cursor">The sequence to read after next time.</param>
/// <param name="Notices">The notices, in order.</param>
internal sealed record ClanWireBatch(string Hub, long Cursor, ClanWireNotice[] Notices);

internal static class ClanTransport
{
    // The clan row splits a notice above 512 recipients and keeps bodies within 8 KiB; the directory enforces the same bounds.
    internal const int MaxRecipients = 512;
    internal const int MaxBodyBytes = 8192;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 8,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static bool Valid(ClanWireNotice? notice) => notice is { Kind: not null, Recipients: not null, Body: not null }
        && notice.Kind.Length is > 0 and <= 64 && notice.Kind.All(char.IsAsciiLetterOrDigit)
        && notice.Recipients.Length is > 0 and <= MaxRecipients && notice.Recipients.All(user => user > 0)
        && notice.Body.Length > 0 && Encoding.UTF8.GetByteCount(notice.Body) <= MaxBodyBytes;

    internal static ClanWireNotice ToWire(ClanRelayNotice notice) => new(notice.Kind, [.. notice.Recipients], notice.Body);

    internal static ClanRelayNotice FromWire(ClanWireNotice notice) => new(notice.Kind, notice.Recipients, notice.Body);
}
