using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Reloads numbered clear marks for a build whose main-menu picker requests them.</summary>
internal sealed class StageMainTutorialListBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<TutorialStageListRequested>, IMessageSender<TutorialStageListReceived>
{
    public string Protocol => protocol;
    private readonly ushort _reply = table.S2C("gspResSingleStageInfo");

    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireFields(packets, build, "TutorialStageInfo", "Stage Main tutorial", ("cleared", "list<TutorialStageClear>"));
        PacketLayoutContract.RequireFields(packets, build, "TutorialStageClear", "Stage Main tutorial",
            ("stage", "u32"), ("best_count", "u32"), ("best_seconds", "u32"));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<TutorialStageListRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<TutorialStageListReceived>>(this));
        RoomRoutes.Bind(context, protocol, table, logger, "gcpReqSingleStageInfo", body =>
        {
            RoomRoutes.RequireEmpty(body, "gcpReqSingleStageInfo");
            return new TutorialStageListRequested();
        });
    }

    public ValueTask SendAsync(ISession session, TutorialStageListReceived message, CancellationToken ct) =>
        session.SendAsync(_reply, PacketCodec.Write(packets, build, "TutorialStageInfo", new Dictionary<string, object?>
        {
            ["cleared"] = message.Clears.Select(row => new Dictionary<string, object?>
                { ["stage"] = row.Stage, ["best_count"] = row.BestCount, ["best_seconds"] = row.BestSeconds }),
        }), ct);
}
