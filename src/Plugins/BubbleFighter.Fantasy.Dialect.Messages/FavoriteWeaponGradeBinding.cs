using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Writes the client's Favorite grade notice, S2C <c>gspNotifyFavoriteWeaponGrade</c> (packets/skill-honor.yaml
/// <c>FavoriteWeaponGrade</c>, kr-1256 sub_9D0E60). Only the zero-flag grade branch is produced; the eight-byte word the client
/// discards is written as zero.
/// </summary>
internal sealed class FavoriteWeaponGradeBinding : IMessageSender<FavoriteWeaponGradeChanged>
{
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly ushort _opcode;

    /// <summary>Creates the sender and checks the layout fields it writes when the row activates.</summary>
    /// <param name="protocol">The game-connection protocol name.</param>
    /// <param name="opcodes">The build's opcode table.</param>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose table is written.</param>
    internal FavoriteWeaponGradeBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    {
        Protocol = protocol;
        _tables = tables;
        _build = build;
        PacketLayoutContract.RequireSignature(tables, build, "FavoriteWeaponGrade", "Favorite weapon grade notice",
            "flag:bool,unknown_4:u64,item_id:u32,old_grade:u32,new_grade:u32");
        _opcode = opcodes.S2C("gspNotifyFavoriteWeaponGrade");
    }

    public string Protocol { get; }

    public ValueTask SendAsync(ISession session, FavoriteWeaponGradeChanged message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return session.SendAsync(_opcode, Encode(_tables, _build, message), ct);
    }

    /// <summary>Encodes the notice body for one build.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose layout is written.</param>
    /// <param name="message">The grade change to announce.</param>
    /// <returns>The 24-byte body with a zero flag.</returns>
    internal static byte[] Encode(PacketTables tables, string build, FavoriteWeaponGradeChanged message)
    {
        if (message.ItemId <= 0 || message.OldGrade < 0 || message.NewGrade < 0 || message.OldGrade == message.NewGrade)
            throw new ArgumentException("A Favorite grade notice names a weapon and two different non-negative grades.", nameof(message));
        return PacketCodec.Write(tables, build, "FavoriteWeaponGrade", new Dictionary<string, object?>
        {
            ["flag"] = false, ["unknown_4"] = 0L, ["item_id"] = (uint)message.ItemId,
            ["old_grade"] = (uint)message.OldGrade, ["new_grade"] = (uint)message.NewGrade,
        });
    }
}