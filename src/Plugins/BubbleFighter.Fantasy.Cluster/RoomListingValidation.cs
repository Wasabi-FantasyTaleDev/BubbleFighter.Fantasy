using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Cluster;

internal static class RoomListingValidation
{
    internal static bool IsValid(FederatedRoom room, int maxSettingsBytes)
    {
        if (room.Listing is not { } entry) return room.InstanceId == Guid.Empty;
        if (room.InstanceId == Guid.Empty || room.RoomId is < 1 or > ushort.MaxValue
            || room.Phase is not ("Waiting" or "Starting" or "Playing" or "Result")
            || entry.RoomId != room.RoomId || entry.Name != room.Title
            || room.Mode != entry.ModeId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || !Text(entry.MasterNickname, 256) || entry.Occupants < 1
            || entry.Occupants != room.Players || entry.Capacity != room.Capacity
            || entry.HasPassword != room.Locked || entry.MatchInProgress != (room.Phase != "Waiting")) return false;
        if (entry.BuildSettings is not { } settings) return true;
        if (!RoomDirectoryClientOptions.Identifier(settings.Lineage) || !settings.SessionPrefix.IsEmpty
            || settings.Mode is { } mode && mode != entry.ModeId
            || settings.RoundSelections is null || settings.RoundSelections.Count > 3
            || settings.RoundSelections.Any(selection => selection is null || selection.Index is < 0 or > 2)
            || settings.RoundSelections.Select(selection => selection.Index).Distinct().Count() != settings.RoundSelections.Count)
            return false;
        var length = (long)settings.Bytes.Length + settings.Change.Length;
        foreach (var selection in settings.RoundSelections) length += selection.Bytes.Length;
        return length <= maxSettingsBytes;
    }

    internal static FederatedRoom Copy(FederatedRoom room) => room with
    {
        Listing = room.Listing is not { } entry ? null : entry with
        {
            BuildSettings = entry.BuildSettings is not { } settings ? null : settings with
            {
                Bytes = settings.Bytes.ToArray(),
                Change = settings.Change.ToArray(),
                SessionPrefix = settings.SessionPrefix.ToArray(),
                RoundSelections = Array.AsReadOnly(settings.RoundSelections.Select(selection =>
                    new RoomRoundSelection(selection.Index, selection.Bytes.ToArray())).ToArray()),
            },
        },
    };

    private static bool Text(string? text, int limit) => text is not null && text.Length <= limit && !text.Any(char.IsControl);
}
