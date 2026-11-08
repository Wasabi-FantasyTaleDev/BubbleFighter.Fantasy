using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Encodes native observation independently of the round rule that selected it instead of respawning.</summary>
internal sealed class RoundObservationSender(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IRoundObservationSender
{
    /// <summary>Gets the game connection whose observer opcode and layout this sender uses.</summary>
    public string Protocol => protocol;

    /// <summary>Requires the audited bomb capability and the mode gate in kr-812 sub_52A640.</summary>
    public bool SupportsMode(int modeId) => modeId == 19 && packets.CapabilityOf(build, "bomb_mission");

    /// <summary>Validates the native observer route and publishes a reversible sender.</summary>
    internal void Mount(IPluginContext context)
    {
        RequireLayout(packets, build);
        _ = table.S2C("gspObserver");
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundPlayerObserved>>(this));
        context.Effect(context.Services.RegisterMany<IRoundObservationSender>(this));
    }

    /// <summary>Writes the scene-object reference consumed by kr-812 sub_52A640 through its native table.</summary>
    public ValueTask SendAsync(ISession session, RoundPlayerObserved message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspObserver"), WriteBody(packets, build, message), ct);

    /// <summary>Writes the four-byte native observer body without converting the actor to a seat or user number.</summary>
    internal static byte[] WriteBody(PacketTables packets, string build, RoundPlayerObserved message) =>
        PacketCodec.Write(packets, build, "gspObserver", new Dictionary<string, object?> { ["actor"] = message.Actor });

    /// <summary>Checks the scene-object reference field before any observer sender is published.</summary>
    internal static void RequireLayout(PacketTables packets, string build) =>
        PacketLayoutContract.RequireFields(packets, build, "gspObserver", "Round observation", ("actor", "u32"));
}
