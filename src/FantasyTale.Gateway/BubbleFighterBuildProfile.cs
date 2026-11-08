using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;

namespace FantasyTale.Gateway;

/// <summary>The build-time projection of the canonical profile, opcode and packet tables.</summary>
internal sealed class BubbleFighterBuildProfile(string id, string region, int major, int resource,
    string frame, string tableA, string tableB, ushort greetingStatus, ushort patchRequest,
    ushort patchReply, ushort centerList, ushort? marker, ushort? keepAlivePing, int group, int reservedCenter,
    bool supportsSelectorIdentity, bool experimentalDirectory, string bootstrapCommandLine, ushort[] preHandshakeOpcodes,
    CompiledPacket firstConnect, CompiledPacket patchResponse, CompiledPacket directory, AutomaticCenterProtocol? automatic = null,
    NativeBootstrapExchange[]? bootstrapExchanges = null, NativeBootstrapObservation[]? bootstrapObservations = null)
{
    internal string Id { get; } = id;
    internal int Major { get; } = major;
    internal ushort GreetingStatus { get; } = greetingStatus;
    internal ushort PatchRequest { get; } = patchRequest;
    internal ushort PatchReply { get; } = patchReply;
    internal ushort CenterList { get; } = centerList;
    internal ushort? Marker { get; } = marker;
    internal ushort? KeepAlivePing { get; } = keepAlivePing;
    internal int Group { get; } = group;
    internal int ReservedCenter { get; } = reservedCenter;
    internal bool SupportsSelectorIdentity { get; } = supportsSelectorIdentity;
    internal bool ExperimentalDirectory { get; } = experimentalDirectory;
    internal string BootstrapCommandLine { get; } = bootstrapCommandLine;
    internal ushort[] PreHandshakeOpcodes { get; } = preHandshakeOpcodes;
    internal CompiledPacket FirstConnect { get; } = firstConnect;
    internal CompiledPacket PatchResponse { get; } = patchResponse;
    internal CompiledPacket Directory { get; } = directory;
    internal AutomaticCenterProtocol? Automatic { get; } = automatic;
    internal NativeBootstrapExchange[] BootstrapExchanges { get; } = bootstrapExchanges ?? [];
    internal NativeBootstrapObservation[] BootstrapObservations { get; } = bootstrapObservations ?? [];
    internal IWireDialect Dialect { get; } = WireDialect.Create(
        new ClientProfile(id, region, major, 0, resource, id), WireStream.Parse(frame, tableA, tableB));
}

/// <summary>Holds the audited native automatic endpoint opcodes and canonical packet layouts.</summary>
internal sealed record AutomaticCenterProtocol(ushort StatusRequest, ushort StatusReply, ushort UniquenessRequest,
    ushort UniquenessReply, ushort LoginRequest, ushort MoveReply, ushort PromotePending,
    CompiledPacket Status, CompiledPacket Availability, CompiledPacket Login, CompiledPacket Move, CompiledPacket Promotion,
    NativeHandoffRelease? Release = null);

/// <summary>Describes a native source acknowledgement and the final source-close reply after pending promotion.</summary>
internal sealed record NativeHandoffRelease(ushort Request, ushort Reply, CompiledPacket Acknowledgement, CompiledPacket Completion);

/// <summary>Answers one audited startup request with a canonical fixed reply while retaining native selection.</summary>
internal sealed record NativeBootstrapExchange(ushort Request, ushort Reply, CompiledPacket RequestLayout, byte[] ReplyPayload);

/// <summary>Validates a nonblocking auxiliary request without asserting account balance or regional authorization.</summary>
internal sealed record NativeBootstrapObservation(ushort Opcode, CompiledPacket Layout);
