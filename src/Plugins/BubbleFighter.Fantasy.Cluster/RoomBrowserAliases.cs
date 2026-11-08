using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Maps a connection's native room numbers to complete advertised room identities without granting admission.</summary>
internal sealed class RoomBrowserAliases
{
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<RoomBrowserTarget, int> _aliases = [];
    private Dictionary<int, RoomBrowserTarget> _visible = [];

    internal RoomBrowserAliases(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, short.MaxValue);
        _capacity = capacity;
    }

    // kr-1256 sub_854C90 sign-extends the row's 16-bit number into C2S 403; aliases stay positive there.
    internal IReadOnlyList<RoomListEntry> Replace(IReadOnlyList<FederatedRoom> rooms) =>
        Replace([], rooms, "", "");

    internal IReadOnlyList<RoomListEntry> Replace(IReadOnlyList<PublishedRoomSnapshot> localRooms,
        IReadOnlyList<FederatedRoom> rooms, string localNode, string build)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        lock (_gate)
        {
            try
            {
                if ((long)rooms.Count + localRooms.Count > _capacity)
                    throw new InvalidOperationException("The native room list exceeds its configured alias capacity.");
                var targets = new HashSet<RoomBrowserTarget>();
                var candidates = new List<(RoomBrowserTarget Target, RoomListEntry Entry)>(rooms.Count + localRooms.Count);
                foreach (var room in localRooms)
                {
                    if (room is null || room.InstanceId == Guid.Empty || room.Entry is not { } entry
                        || entry.RoomId is < 1 or > ushort.MaxValue
                        || !RoomDirectoryClientOptions.BackendId(localNode) || !RoomDirectoryClientOptions.Identifier(build))
                        throw new InvalidDataException("A local room alias requires a complete captured room identity.");
                    var target = new RoomBrowserTarget(localNode, 0, build, entry.RoomId, room.InstanceId, Local: true);
                    if (!targets.Add(target)) throw new InvalidDataException("The local room list contains a duplicate room identity.");
                    candidates.Add((target, entry));
                }
                foreach (var room in rooms)
                {
                    if (room is null || !RoomDirectoryClientOptions.BackendId(room.NodeId) || room.Epoch <= 0
                        || !RoomDirectoryClientOptions.Identifier(room.ClientBuild)
                        || build.Length > 0 && room.ClientBuild != build
                        || room.RoomId is < 1 or > ushort.MaxValue || room.InstanceId == Guid.Empty || room.Listing is not { } entry
                        || entry.RoomId != room.RoomId)
                        throw new InvalidDataException("A native room alias requires a complete advertised room identity.");
                    var target = new RoomBrowserTarget(room.NodeId, room.Epoch, room.ClientBuild, room.RoomId, room.InstanceId);
                    if (!targets.Add(target)) throw new InvalidDataException("The native room list contains a duplicate room identity.");
                    candidates.Add((target, entry));
                }
                var additions = candidates.Count(candidate => !_aliases.ContainsKey(candidate.Target));
                if (additions > _capacity - _aliases.Count)
                    throw new InvalidOperationException("The connection's native room alias history is exhausted.");

                var next = new Dictionary<int, RoomBrowserTarget>(candidates.Count);
                var rows = new RoomListEntry[candidates.Count];
                for (var index = 0; index < candidates.Count; index++)
                {
                    var (target, entry) = candidates[index];
                    if (!_aliases.TryGetValue(target, out var alias))
                    {
                        alias = _aliases.Count + 1;
                        _aliases.Add(target, alias);
                    }
                    next.Add(alias, target);
                    rows[index] = entry with { RoomId = alias };
                }
                _visible = next;
                return Array.AsReadOnly(rows);
            }
            catch
            {
                // A failed refresh cannot leave a formerly visible destination active.
                _visible.Clear();
                throw;
            }
        }
    }

    internal RoomBrowserTarget? Resolve(int alias)
    {
        lock (_gate) return _visible.GetValueOrDefault(alias);
    }

    internal void Invalidate()
    {
        lock (_gate) _visible.Clear();
    }
}

internal sealed record RoomBrowserTarget(string NodeId, long Epoch, string ClientBuild, int RoomId, Guid InstanceId, bool Local = false);
