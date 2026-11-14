using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Writes the client's title condition progress, S2C <c>gspNotifyUserTitleInstance</c> (packets/titles-medals.yaml
/// <c>TitleInstanceNotice</c>, kr-1256 sub_9D1350). The two outer words no reader consumes are written as zero.
/// </summary>
internal sealed class TitleProgressBinding : IMessageSender<TitleProgressNotified>
{
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly ushort _opcode;

    /// <summary>Creates the sender and checks the layouts it writes when the row activates.</summary>
    /// <param name="protocol">The game-connection protocol name.</param>
    /// <param name="opcodes">The build's opcode table.</param>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose table is written.</param>
    internal TitleProgressBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    {
        Protocol = protocol;
        _tables = tables;
        _build = build;
        PacketLayoutContract.RequireSignature(tables, build, "TitleInstanceNotice", "Title progress", "titles:list<TitleInstance>");
        PacketLayoutContract.RequireSignature(tables, build, "TitleInstance", "Title progress",
            "title_id:u32,unknown_28:bool,unknown_32:u32,conditions:list<TitleConditionProgress>");
        PacketLayoutContract.RequireSignature(tables, build, "TitleConditionProgress", "Title progress",
            "condition_id:u32,current:u32,required:u32");
        _opcode = opcodes.S2C("gspNotifyUserTitleInstance");
    }

    public string Protocol { get; }

    public ValueTask SendAsync(ISession session, TitleProgressNotified message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return session.SendAsync(_opcode, Encode(_tables, _build, message), ct);
    }

    /// <summary>Encodes the notice body for one build.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose layout is written.</param>
    /// <param name="message">The titles and their condition rows.</param>
    /// <returns>A counted list whose size is four plus, per title, sixteen plus twelve per condition row.</returns>
    internal static byte[] Encode(PacketTables tables, string build, TitleProgressNotified message)
    {
        ArgumentNullException.ThrowIfNull(message.Titles);
        // kr-1256 sub_10CF5D0 reads one counted list inside the native 65,535-byte body.
        var size = 4L + message.Titles.Sum(title => 16L + 12L * (title.Conditions?.Count ?? 0));
        if (size > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(message), "The title progress exceeds one native body.");
        var titles = message.Titles.Select(title =>
        {
            if (title.TitleId <= 0 || title.Conditions is null)
                throw new ArgumentException("Title progress names a title and its condition rows.", nameof(message));
            var conditions = title.Conditions.Select(row =>
            {
                if (row.ConditionId < 0 || row.Current < 0 || row.Required <= 0)
                    throw new ArgumentException("A condition row needs a condition kind, a non-negative progress and a positive requirement.", nameof(message));
                return new Dictionary<string, object?>
                {
                    ["condition_id"] = (uint)row.ConditionId, ["current"] = (uint)row.Current, ["required"] = (uint)row.Required,
                };
            }).ToArray();
            return new Dictionary<string, object?>
            {
                ["title_id"] = (uint)title.TitleId, ["unknown_28"] = false, ["unknown_32"] = 0u, ["conditions"] = conditions,
            };
        }).ToArray();
        return PacketCodec.Write(tables, build, "TitleInstanceNotice", new Dictionary<string, object?> { ["titles"] = titles });
    }
}