using BubbleFighter.Fantasy.Contracts.Couples;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence;

[SugarTable("couples")]
internal sealed class CoupleRow
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public string Id { get; set; } = "";
    [SugarColumn(ColumnName = "authority")]
    public string Authority { get; set; } = "";
    [SugarColumn(ColumnName = "first_user")]
    public long FirstUser { get; set; }
    [SugarColumn(ColumnName = "second_user")]
    public long SecondUser { get; set; }
    [SugarColumn(ColumnName = "created_at")]
    public long CreatedAt { get; set; }
    [SugarColumn(ColumnName = "ring_item")]
    public int RingItem { get; set; }

    internal CoupleRelationship Relationship() => new(Guid.Parse(Id), new(Authority, FirstUser), new(Authority, SecondUser),
        new DateTimeOffset(CreatedAt, TimeSpan.Zero)) { RingItemId = RingItem };

    internal static CoupleRow From(CoupleInvitation invitation, DateTimeOffset now) => new()
    {
        Id = invitation.Id.ToString("D"), Authority = invitation.Sender.Authority,
        FirstUser = invitation.Sender.UserNum, SecondUser = invitation.Recipient.UserNum,
        CreatedAt = now.UtcTicks, RingItem = invitation.RingItemId,
    };
}

[SugarTable("couple_members")]
internal sealed class CoupleMemberRow
{
    [SugarColumn(ColumnName = "authority", IsPrimaryKey = true)]
    public string Authority { get; set; } = "";
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }
    [SugarColumn(ColumnName = "couple_id")]
    public string CoupleId { get; set; } = "";
}
