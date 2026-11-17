using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The clan notices that travel between nodes, and their JSON form. Only one-way notices are listed: a conversation
/// whose later requests this node's row remembers (the master transfer) stays with the build and node it started on.
/// </summary>
internal static class ClanRelayKinds
{
    /// <summary>The most recipients one relayed notice names; a larger clan is split into several notices.</summary>
    internal const int MaxRecipients = 512;

    /// <summary>The largest JSON body a relayed notice carries.</summary>
    internal const int MaxBodyBytes = 8192;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    // Delivers one decoded notice to the listed members connected here; returns how many were told.
    private delegate ValueTask<int> Deliver(IClanConnections connections, IReadOnlyList<long> recipients, string body, CancellationToken ct);

    private static readonly Dictionary<string, Deliver> Kinds = new(StringComparer.Ordinal)
    {
        [nameof(ClanChatReceived)] = DeliverAs<ClanChatReceived>,
        [nameof(ClanWhisperReceived)] = DeliverAs<ClanWhisperReceived>,
        [nameof(ClanMemberChanged)] = DeliverAs<ClanMemberChanged>,
        [nameof(ClanMemberLeft)] = DeliverAs<ClanMemberLeft>,
        [nameof(ClanIntroductionChanged)] = DeliverAs<ClanIntroductionChanged>,
        [nameof(ClanNoticeChanged)] = DeliverAs<ClanNoticeChanged>,
        [nameof(ClanMarkChanged)] = DeliverAs<ClanMarkChanged>,
        [nameof(ClanMasterChanged)] = DeliverAs<ClanMasterChanged>,
        [nameof(ClanDisbandChanged)] = DeliverAs<ClanDisbandChanged>,
        [nameof(ClanHomeSent)] = DeliverAs<ClanHomeSent>,
    };

    /// <summary>Returns the relay notices for a message, split by <see cref="MaxRecipients"/>, or none when the message does not travel.</summary>
    internal static IEnumerable<ClanRelayNotice> Encode<T>(IReadOnlyList<long> recipients, T message) where T : IMessage
    {
        // The runtime type names the kind, so a message passed as a base type still travels as itself.
        var kind = message.GetType().Name;
        if (recipients.Count == 0 || !Kinds.ContainsKey(kind)) yield break;
        var body = JsonSerializer.Serialize(message, message.GetType(), Json);
        if (System.Text.Encoding.UTF8.GetByteCount(body) > MaxBodyBytes) yield break;
        for (var start = 0; start < recipients.Count; start += MaxRecipients)
            yield return new(kind, recipients.Skip(start).Take(MaxRecipients).ToArray(), body);
    }

    /// <summary>Whether a notice is well formed: a listed kind, 1..<see cref="MaxRecipients"/> positive users, a bounded body.</summary>
    internal static bool Valid(ClanRelayNotice? notice) => notice is { Kind: not null, Recipients: not null, Body: not null }
        && Kinds.ContainsKey(notice.Kind) && notice.Recipients.Count is > 0 and <= MaxRecipients && notice.Recipients.All(user => user > 0)
        && notice.Body.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(notice.Body) <= MaxBodyBytes;

    /// <summary>Tells a notice from another node to its recipients connected here; returns how many were told.</summary>
    /// <exception cref="InvalidDataException">The notice is malformed or its body is not the listed message.</exception>
    internal static ValueTask<int> DeliverAsync(IClanConnections connections, ClanRelayNotice notice, CancellationToken ct)
    {
        if (!Valid(notice)) throw new InvalidDataException("The relayed clan notice is malformed.");
        return Kinds[notice.Kind](connections, notice.Recipients, notice.Body, ct);
    }

    private static async ValueTask<int> DeliverAs<T>(IClanConnections connections, IReadOnlyList<long> recipients, string body,
        CancellationToken ct) where T : IMessage
    {
        T message;
        try { message = JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("The relayed clan notice has no body."); }
        catch (JsonException error) { throw new InvalidDataException("The relayed clan notice body does not match its kind.", error); }
        var told = 0;
        foreach (var user in recipients)
        {
            if (!connections.TryGet(user, out var session, out var delivery)) continue;
            try
            {
                if (await delivery.SendAsync(session, message, ct).ConfigureAwait(false)) told++;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection is going away; its close event detaches it.
            }
        }
        return told;
    }
}
