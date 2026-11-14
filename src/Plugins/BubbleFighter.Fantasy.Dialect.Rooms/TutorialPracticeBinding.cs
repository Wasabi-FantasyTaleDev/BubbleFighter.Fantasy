using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Publishes supplementary practice through the build's own event-notice consumer.</summary>
internal sealed class TutorialPracticeBinding(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IMessageSender<TutorialPracticeProgress>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireSignature(packets, build, "TutorialPracticeNotice", "Tutorial practice",
            "events:list<TutorialPracticeEvent>");
        PacketLayoutContract.RequireSignature(packets, build, "TutorialPracticeEvent", "Tutorial practice",
            "event_id:u32,progress:u32,unknown_12:u8,unknown_13:u8,unknown_14:u8,unknown_16:u32[8],unknown_48:wstr,unknown_52:wstr,unknown_56:wstr,unknown_60:wstr,unknown_64:wstr,unknown_68:u32,unknown_72:wstr,unknown_76:u32,unknown_80:u32,unknown_84:list<wstr>");
        _ = table.S2C("gspEventNotice");
        context.Effect(context.Services.RegisterMany<IMessageSender<TutorialPracticeProgress>>(this));
    }

    public ValueTask SendAsync(ISession session, TutorialPracticeProgress message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspEventNotice"), Write(packets, build, message.Cleared), ct);

    internal static byte[] Write(PacketTables packets, string build, bool cleared) =>
        PacketCodec.Write(packets, build, "TutorialPracticeNotice", new Dictionary<string, object?>
        {
            ["events"] = new Dictionary<string, object?>[]
            {
                new() { ["progress"] = cleared ? 1 : 0 },
            },
        });
}
