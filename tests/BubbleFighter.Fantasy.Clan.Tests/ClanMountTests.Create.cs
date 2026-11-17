using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// Founding a clan on the mounted rows: each refusal the client has a notice for is answered with its own
/// result word (kr-1256 sub_517FE0) and leaves the clan unfounded and the card in the bag; a success takes one
/// card and gives the clan the card's capacity.
/// </summary>
public sealed partial class ClanMountTests
{
    /// <summary>
    /// The create dialog's length limits, a request that names no creation card or misstates its capacity, a
    /// taken name, an application elsewhere and a missing card are refused each with its own word; then the
    /// 30-member card founds a clan of 30 and one card leaves the bag, and a seated founder is refused.
    /// </summary>
    [Fact]
    public async Task CreateRefusalsUseTheirOwnNoticeAndSuccessConsumesTheItem()
    {
        await using var rig = await Rig.StartAsync();
        var clan = await LoginClanAsync(rig);
        var directory = rig.Capture.Clans;
        var other = (await directory.CreateAsync("Bears", "", 30, 77, "Other", 1, CancellationToken.None))!;

        Assert.Equal(ClanCreateAnswered.NameLength, await CreateResultAsync(rig, clan, "Abc", "hi", Card30, 30));
        Assert.Equal(ClanCreateAnswered.NameLength, await CreateResultAsync(rig, clan, new string('W', 13), "hi", Card30, 30));
        Assert.Equal(ClanCreateAnswered.IntroductionLength, await CreateResultAsync(rig, clan, "Wolves", "", Card30, 30));
        Assert.Equal(ClanCreateAnswered.IntroductionLength, await CreateResultAsync(rig, clan, "Wolves", new string('i', 97), Card30, 30));
        Assert.Equal(ClanCreateAnswered.Failed, await CreateResultAsync(rig, clan, "Wolves", "hi", 100001, 30));
        Assert.Equal(ClanCreateAnswered.Failed, await CreateResultAsync(rig, clan, "Wolves", "hi", Card30, 10));
        Assert.Equal(ClanCreateAnswered.NameTaken, await CreateResultAsync(rig, clan, "Bears", "hi", Card30, 30));
        Assert.True(await directory.AddMemberAsync(other.ClanId, UserNum, "clanner", 1, ClanRole.Applicant, CancellationToken.None));
        Assert.Equal(ClanCreateAnswered.AlreadyApplied, await CreateResultAsync(rig, clan, "Wolves", "hi", Card30, 30));
        Assert.True(await directory.RemoveMemberAsync(other.ClanId, UserNum, CancellationToken.None));
        // The bag holds no 10-member card: the clan the directory created is undone.
        Assert.Equal(ClanCreateAnswered.Failed, await CreateResultAsync(rig, clan, "Wolves", "hi", Card10, 10));
        Assert.Null(await directory.FindByNameAsync("Wolves", CancellationToken.None));
        Assert.Null(await directory.FindMemberAsync(UserNum, CancellationToken.None));
        Assert.Equal(2, await CardsAsync(rig));

        Assert.Equal(ClanCreateAnswered.Created, await CreateResultAsync(rig, clan, "Wolves", new string('i', 96), Card30, 30));
        Assert.Equal(("01000000" + "D3890100" + "1E000000"), clan.Pins()[0].Hex);
        var founded = await directory.FindByNameAsync("Wolves", CancellationToken.None);
        Assert.Equal((30, UserNum), (founded!.Capacity, founded.MasterUserNum));
        Assert.Equal(1, await CardsAsync(rig));

        Assert.Equal(ClanCreateAnswered.AlreadyMember, await CreateResultAsync(rig, clan, "Lions", "hi", Card30, 30));
        Assert.Equal(1, await CardsAsync(rig));
    }

    /// <summary>Sends one create request and returns the result word of its single answer.</summary>
    private static async Task<int> CreateResultAsync(Rig rig, RecordingSession clan, string name, string introduction, int item, int capacity)
    {
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody(name, introduction, item, capacity));
        var frame = Assert.Single(clan.Frames);
        Assert.Equal(rig.ClanTable.S2C("ClanCreateResult"), frame.Opcode);
        var result = BitConverter.ToInt32(frame.Payload, 0);
        // A refusal names no item: only a success makes the client take one out of its view.
        if (result != ClanCreateAnswered.Created)
            Assert.Equal(new string('0', 16), Convert.ToHexString(frame.Payload, 4, 8));
        return result;
    }

    /// <summary>How many 30-member cards the stored bag holds.</summary>
    private static async Task<int> CardsAsync(Rig rig) =>
        (await rig.Capture.Inventory.ListSlotsAsync(UserNum, InventoryBag.CharacterBag, 64, CancellationToken.None))
            .Where(row => row.ItemId == Card30).Sum(row => row.Quantity);
}
