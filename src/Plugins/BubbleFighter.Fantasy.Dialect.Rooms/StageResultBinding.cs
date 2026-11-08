using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Reads the native stage result once for the active tutorial or script-owned round.</summary>
internal sealed class StageResultBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundStageResultReported>
{
    /// <summary>Gets the owning game protocol for the shared native stage result.</summary>
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireSignature(packets, build, "ScriptStageResult", "Script stage result",
            "outcome:u32", "outcome:u32,unknown_4:bool");
        _ = table.C2S("gcpReqEndStage");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundStageResultReported>>(this));
        RoomRoutes.Bind(context, protocol, table, logger, "gcpReqEndStage", body => Read(packets, build, body));
    }

    internal static RoundStageResultReported Read(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "ScriptStageResult", body, requireCanonicalBooleans: true);
        var outcome = (int)fields["outcome"];
        if (outcome is not (0 or 1) || fields.TryGetValue("unknown_4", out var tail) && (bool)tail)
            throw new NotSupportedException("The stage result requires a native completion or failure confirmation.");
        return new(outcome);
    }
}
