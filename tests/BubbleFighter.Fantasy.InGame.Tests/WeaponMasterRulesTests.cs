using System.ComponentModel.DataAnnotations;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks the WeaponMaster configuration, the client tunable it reads, and the per-seat level and weapon rules.</summary>
public sealed class WeaponMasterRulesTests
{
    private static readonly WeaponMasterWeapon[] First = [new(100, 1), new(101, 3)];
    private static readonly WeaponMasterWeapon[] Second = [new(200, 2)];
    private static readonly WeaponMasterWeapon[] Third = [new(300, 1)];

    /// <summary>A draw walks the weights in order: tickets 0 and 1 to 3 pick the first and second weapon.</summary>
    [Fact]
    public void DrawFollowsTheWeights()
    {
        var rules = Rules();
        Assert.Equal(100, rules.Draw(1, new Scripted(0)));
        Assert.Equal(101, rules.Draw(1, new Scripted(1)));
        Assert.Equal(101, rules.Draw(1, new Scripted(3)));
        Assert.Equal(300, rules.Draw(3, new Scripted(0)));
        Assert.Equal(300, rules.Draw(99, new Scripted(0)));
    }

    /// <summary>Seats start with a weapon, earn experience from the rewarded events only, and the first at the last level wins.</summary>
    [Fact]
    public void ExperienceRaisesLevelsToTheLastLevelWhichWins()
    {
        var state = new WeaponMasterRoundState(Rules(), new Scripted(0), 1);
        state.Begin([0, 1]);
        Assert.Equal([new WeaponMasterAssignment(0, 100, 1, WeaponMasterReason.Start), new WeaponMasterAssignment(1, 100, 1, WeaponMasterReason.Start)],
            state.TakePending());
        Assert.Empty(state.TakePending());

        state.Score(0, WeaponMasterEvent.Rescue);
        Assert.Equal(1, state.Level(0));
        Assert.Empty(state.TakePending());

        state.Score(0, WeaponMasterEvent.Kill);
        Assert.Equal(2, state.Level(0));
        Assert.Equal([new WeaponMasterAssignment(0, 200, 2, WeaponMasterReason.LevelUp)], state.TakePending());
        state.Score(0, WeaponMasterEvent.Knockdown);
        state.Score(0, WeaponMasterEvent.Kill);
        Assert.Equal(3, state.Level(0));
        state.TakePending();
        state.Score(0, WeaponMasterEvent.Kill);
        Assert.Equal(3, state.Level(0));
        Assert.Empty(state.TakePending());
        Assert.Equal(1, state.Level(1));
        Assert.Equal(0, state.Winner);
        Assert.Equal(new RoundWeaponMasterProgress(3, 200, 200, 201), state.Progress(0));
        Assert.Equal(new RoundWeaponMasterProgress(1, 0, 0, 100), state.Progress(1));
        state.Score(1, WeaponMasterEvent.Kill);
        Assert.Equal(1, state.Level(1));
    }

    /// <summary>Experience that crosses several thresholds at once hands out only the new level's weapon.</summary>
    [Fact]
    public void OneEventCanCrossSeveralLevels()
    {
        var rules = new WeaponMasterRules([First, Second, Third], [0, 100, 200],
            new Dictionary<WeaponMasterEvent, int> { [WeaponMasterEvent.Kill] = 250 }, 2);
        var state = new WeaponMasterRoundState(rules, new Scripted(0), 1);
        state.Begin([2]);
        state.TakePending();
        state.Score(2, WeaponMasterEvent.Kill);
        Assert.Equal([new WeaponMasterAssignment(2, 300, 3, WeaponMasterReason.LevelUp)], state.TakePending());
        Assert.Equal(2, state.Winner);
        Assert.Equal(1, rules.LevelFor(99));
        Assert.Equal(2, rules.LevelFor(100));
        Assert.Equal(3, rules.LevelFor(5000));
    }

    /// <summary>
    /// Deaths count once per life and hand out nothing by themselves; a revival request is granted only after the
    /// client's number of deaths since the last weapon, and every new weapon starts the count again.
    /// </summary>
    [Fact]
    public void RevivalNeedsTheClientsDeathCountSinceTheLastWeapon()
    {
        var state = new WeaponMasterRoundState(Rules(noScoreDeaths: 2), new Scripted(0), 1);
        state.Begin([4]);
        state.TakePending();

        state.Death(4, life: 1);
        state.Death(4, life: 1);
        Assert.Equal(1, state.DeathsSinceWeapon(4));
        Assert.False(state.Revive(4));
        Assert.Empty(state.TakePending());

        state.Score(4, WeaponMasterEvent.Rescue);
        Assert.Equal(1, state.DeathsSinceWeapon(4));
        state.Death(4, life: 2);
        Assert.Empty(state.TakePending());
        Assert.True(state.Revive(4));
        Assert.Equal([new WeaponMasterAssignment(4, 100, 1, WeaponMasterReason.Revival)], state.TakePending());
        Assert.Equal(0, state.DeathsSinceWeapon(4));
        Assert.False(state.Revive(4));

        state.Death(4, life: 3);
        state.Score(4, WeaponMasterEvent.Kill);
        Assert.Equal(0, state.DeathsSinceWeapon(4));
        state.TakePending();
        state.Death(4, life: 4);
        state.Death(4, life: 5);
        Assert.True(state.Revive(4));
        Assert.Equal([new WeaponMasterAssignment(4, 200, 2, WeaponMasterReason.Revival)], state.TakePending());
    }

    /// <summary>A seat first seen after the round began gets its start weapon along with the event that revealed it.</summary>
    [Fact]
    public void ALateSeatStartsAtLevelOne()
    {
        var state = new WeaponMasterRoundState(Rules(), new Scripted(0), 7);
        state.Score(9, WeaponMasterEvent.Kill);
        Assert.Equal([new WeaponMasterAssignment(9, 100, 1, WeaponMasterReason.Start), new WeaponMasterAssignment(9, 200, 2, WeaponMasterReason.LevelUp)],
            state.TakePending());
        Assert.Equal(7, state.Generation);
    }

    /// <summary>The row without levels stays off, and every malformed setting is refused before activation.</summary>
    [Fact]
    public void OptionsAreOffByDefaultAndFailLoud()
    {
        Assert.DoesNotContain(new InGameOptions().Validate(), result => result.ErrorMessage!.Contains("weapon_master"));
        Assert.Single(Invalid(options => options.WeaponMasterExperience = [new() { Event = "kill", Points = 100 }]));
        Assert.Empty(Invalid(options => options.WeaponMasterLevels = []));

        InGameOptions Valid()
        {
            var options = new InGameOptions
            {
                NativeLifeEveryMode = true,
                WeaponMasterExperience = [new() { Event = "kill", Points = 100 }],
                WeaponMasterLevels = [new() { Weapons = [new() { Item = 100, Weight = 1 }] },
                    new() { Experience = 200, Weapons = [new() { Item = 200, Weight = 1 }] }],
            };
            return options;
        }

        Assert.Empty(WeaponMasterIssues(Valid()));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterExperience = [])));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterExperience =
            [new() { Event = "kill", Points = 100 }, new() { Event = "kill", Points = 50 }])));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterExperience = [new() { Event = "assist", Points = 100 }])));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterExperience = [new() { Event = "kill", Points = 0 }])));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterLevels[1].Experience = 0)));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterLevels[0].Experience = 10)));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterLevels[1].Experience = 65535)));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterLevels = [new() { Weapons = [] }])));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterLevels = [new() { Weapons = [new() { Item = 0, Weight = 1 }] }])));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterLevels = [new() { Weapons = [new() { Item = 5, Weight = 0 }] }])));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterLevels =
            [new() { Weapons = [new() { Item = 5, Weight = 1 }, new() { Item = 5, Weight = 2 }] }])));
        Assert.Empty(WeaponMasterIssues(With(Valid(), options =>
        {
            options.WeaponMasterWeapons = [new() { Item = 300, Weight = 1 }];
            options.WeaponMasterLevels = [new(), new() { Experience = 200 }];
        })));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.WeaponMasterLevels = [new(), new() { Experience = 200 }])));
        Assert.NotEmpty(WeaponMasterIssues(With(Valid(), options => options.NativeLifeEveryMode = false)));
        Assert.Empty(WeaponMasterIssues(With(Valid(), options =>
        {
            options.NativeLifeEveryMode = false;
            options.NativeLifeModeIds = [26];
        })));

        static InGameOptions With(InGameOptions options, Action<InGameOptions> change)
        {
            change(options);
            return options;
        }
        static IEnumerable<ValidationResult> Invalid(Action<InGameOptions> change) =>
            WeaponMasterIssues(With(new InGameOptions(), change));
        static IEnumerable<ValidationResult> WeaponMasterIssues(InGameOptions options) =>
            options.Validate().Where(result => result.ErrorMessage!.Contains("weapon_master"));
    }

    /// <summary>The no-score death count is read from the client's option sheet and nothing else is assumed.</summary>
    [Theory]
    [InlineData("2", 2)]
    [InlineData("5", 5)]
    public void TheClientTunableIsReadFromTheOptionSheet(string cell, int expected) =>
        Assert.Equal(expected, WeaponMasterRules.ReadNoScoreDeathCount(Store(Sheet(("WeaponMasterNoPangDeadCount", cell)))));

    /// <summary>The real kr-1256 option sheet holds the client's value of 2 deaths without a score.</summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void RealKr1256SheetHoldsTwo() =>
        Assert.Equal(2, WeaponMasterRules.ReadNoScoreDeathCount(new RealStore("kr-1256")));

    /// <summary>A missing, repeated, zero or non-numeric value is a configuration error.</summary>
    [Theory]
    [InlineData("other", "1")]
    [InlineData("WeaponMasterNoPangDeadCount", "0")]
    [InlineData("WeaponMasterNoPangDeadCount", "-1")]
    [InlineData("WeaponMasterNoPangDeadCount", "two")]
    public void AnUnusableTunableFailsLoud(string key, string cell) =>
        Assert.Throws<ConfigException>(() => WeaponMasterRules.ReadNoScoreDeathCount(Store(Sheet((key, cell)))));

    /// <summary>The key appearing twice or the file missing is refused rather than guessed.</summary>
    [Fact]
    public void ARepeatedKeyOrMissingFileFailsLoud()
    {
        Assert.Throws<ConfigException>(() => WeaponMasterRules.ReadNoScoreDeathCount(Store(Sheet(
            ("WeaponMasterNoPangDeadCount", "2"), ("WeaponMasterNoPangDeadCount", "3")))));
        Assert.Throws<ConfigException>(() => WeaponMasterRules.ReadNoScoreDeathCount(new MemoryStore([])));
    }

    /// <summary>The rules are built from validated options and the sheet together.</summary>
    [Fact]
    public void CreateJoinsTheOptionsAndTheSheet()
    {
        var options = new InGameOptions
        {
            NativeLifeEveryMode = true,
            WeaponMasterExperience = [new() { Event = "knockdown", Points = 50 }],
            WeaponMasterLevels = [new() { Weapons = [new() { Item = 7, Weight = 2 }] },
                new() { Experience = 150, Weapons = [new() { Item = 8, Weight = 1 }] }],
        };
        var rules = WeaponMasterRules.Create(options, Store(Sheet(("WeaponMasterNoPangDeadCount", "4"))));
        Assert.Equal(2, rules.LevelCount);
        Assert.Equal(4, rules.NoScoreDeathCount);
        Assert.Equal(50, rules.Points(WeaponMasterEvent.Knockdown));
        Assert.Equal(0, rules.Points(WeaponMasterEvent.Kill));
        Assert.Equal(150, rules.Threshold(2));
        Assert.Equal(151, rules.NextThreshold(2));
        Assert.Throws<ConfigException>(() => WeaponMasterRules.Create(new InGameOptions(), Store(Sheet(("WeaponMasterNoPangDeadCount", "4")))));

        options.WeaponMasterWeapons = [new() { Item = 9, Weight = 1 }];
        options.WeaponMasterLevels[1].Weapons = [];
        Assert.Equal(9, WeaponMasterRules.Create(options, Store(Sheet(("WeaponMasterNoPangDeadCount", "4")))).Draw(2, new Scripted(0)));
    }

    private static WeaponMasterRules Rules(int noScoreDeaths = 2) =>
        new([First, Second, Third], [0, 100, 200],
            new Dictionary<WeaponMasterEvent, int> { [WeaponMasterEvent.Kill] = 100, [WeaponMasterEvent.Knockdown] = 100 }, noScoreDeaths);

    private static MemoryStore Store(string workbook) => new(new() { [WeaponMasterRules.GameOptionPath] = Encoding.UTF8.GetBytes(workbook) });

    private static string Sheet(params (string Key, string Value)[] rows) =>
        "<?xml version=\"1.0\"?><Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" "
        + "xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\"><Worksheet ss:Name=\"GameOption\"><Table>"
        + "<Row><Cell><Data ss:Type=\"String\">#WeaponMaster</Data></Cell></Row>"
        + string.Concat(rows.Select(row => $"<Row><Cell><Data ss:Type=\"String\">{row.Key}</Data></Cell>"
            + "<Cell><Data ss:Type=\"String\">description</Data></Cell>"
            + $"<Cell><Data ss:Type=\"Number\">{row.Value}</Data></Cell></Row>"))
        + "</Table></Worksheet></Workbook>";

    private sealed class Scripted(int ticket) : Random
    {
        public override int Next(int maxValue) => Math.Min(ticket, maxValue - 1);
    }

    private sealed class RealStore(string build) : IClientDataStore
    {
        public bool Exists(string path) => true;

        public byte[] Read(string path) => LocalClientData.Bytes(build, path);
    }

    private sealed class MemoryStore(Dictionary<string, byte[]> files) : IClientDataStore
    {
        public bool Exists(string path) => files.ContainsKey(path);

        public byte[] Read(string path) => files.TryGetValue(path, out var bytes) ? bytes : throw new FileNotFoundException(path);
    }
}
