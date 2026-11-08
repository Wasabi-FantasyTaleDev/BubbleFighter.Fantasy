using System.Text.Json;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks grant-bound snapshot replacement, acknowledgement ordering and final selection ownership.</summary>
public sealed class AutomaticCenterRefreshTests
{
    /// <summary>An initially unobserved admitted node can become eligible without adding a manual-only destination.</summary>
    [Fact]
    public void Empty_snapshot_can_recover_only_within_the_original_admitted_pool()
    {
        var options = AutomaticCenterTests.Auto([], admitted: ["first"]);
        var state = new AutomaticCenterState(options);
        Assert.Throws<AutomaticCenterUnavailableException>(() => state.Select(DateTimeOffset.UtcNow, false));
        Assert.Equal("rejected", state.Handle(Command(options, 1, [AutomaticCenterTests.Candidate("second", 1, 0)])).Status);
        Assert.Equal("accepted", state.Handle(Command(options, 2, [AutomaticCenterTests.Candidate("first", 1, 0)])).Status);
        Assert.Equal("first", state.Select(DateTimeOffset.UtcNow, false).CenterId);
        Assert.Equal("accepted", state.Handle(Command(options, 3, [])).Status);
        Assert.Throws<AutomaticCenterUnavailableException>(() => state.Select(DateTimeOffset.UtcNow, false));
        Assert.Equal("accepted", state.Handle(Command(options, 4, [AutomaticCenterTests.Candidate("first", 1, 0)])).Status);
    }

    /// <summary>Invalid matching revisions are consumed without replacing the last complete accepted snapshot.</summary>
    [Fact]
    public void Rejected_snapshot_cannot_be_replayed_and_does_not_extend_or_replace_previous_data()
    {
        var original = AutomaticCenterTests.Candidate("first", 1, 0);
        var options = AutomaticCenterTests.Auto([original]);
        var state = new AutomaticCenterState(options);
        var duplicate = Command(options, 3, [original, original]);
        Assert.Equal("candidates", state.Handle(duplicate).Reason);
        Assert.Same(original, state.Select(DateTimeOffset.UtcNow, false));
        Assert.Equal("revision", state.Handle(Command(options, 3, [])).Reason);
        Assert.Equal("revision", state.Handle(Command(options, 2, [])).Reason);
        Assert.Same(original, state.Select(original.ExpiresAtUtc, false));
        Assert.Equal("accepted", state.Handle(Command(options, 4, [])).Status);
    }

    /// <summary>Only the parent's revision orders snapshots; source clocks and measurement expiry do not gate login.</summary>
    [Fact]
    public void Source_revision_is_opaque_and_wall_clock_observation_is_not_an_ordering_token()
    {
        var options = AutomaticCenterTests.Auto([], admitted: ["first"]);
        var state = new AutomaticCenterState(options);
        AutomaticCenterCandidate Candidate(string revision, DateTimeOffset observed) => new()
        {
            CenterId = "first", Healthy = true, RttUs = 1, Users = 0, Capacity = 100,
            ObservedAtUtc = observed, StatusRevision = revision, ExpiresAtUtc = observed.AddSeconds(15),
        };
        Assert.Equal("accepted", state.Handle(Command(options, 1, [Candidate("z-epoch", DateTimeOffset.UtcNow)])).Status);
        Assert.Equal("accepted", state.Handle(Command(options, 2, [Candidate("a-new-epoch", DateTimeOffset.UtcNow.AddSeconds(-34))])).Status);
        Assert.Equal("a-new-epoch", state.Select(DateTimeOffset.UtcNow, false).StatusRevision);
    }

    /// <summary>Binding failures cannot advance the parent's revision and measurement times cannot extend its login grant.</summary>
    [Fact]
    public void Wrong_launch_or_grant_is_rejected_while_source_expiry_does_not_change_authority()
    {
        var options = AutomaticCenterTests.Auto([], admitted: ["first"]);
        var state = new AutomaticCenterState(options);
        Assert.Equal("binding", state.Handle(new GatewayCommand
        {
            Type = "refresh-automatic", RowId = "selector", LaunchId = options.LaunchId, GrantId = "other", Revision = 100, Candidates = [],
        }).Reason);
        Assert.Equal("binding", state.Handle(new GatewayCommand
        {
            Type = "refresh-automatic", RowId = "selector", LaunchId = new string('0', 32), GrantId = options.GrantId, Revision = 100, Candidates = [],
        }).Reason);
        Assert.Equal("accepted", state.Handle(Command(options, 1, [])).Status);
        Assert.Equal("accepted", state.Handle(Command(options, 2,
            [AutomaticCenterTests.Candidate("first", 1, 0, expires: options.GrantExpiresAtUtc.AddSeconds(1))])).Status);
        Assert.Equal("first", state.Select(DateTimeOffset.UtcNow, false).CenterId);
        Assert.Throws<AutomaticCenterUnavailableException>(() => state.Select(options.GrantExpiresAtUtc, false));
    }

    /// <summary>Selection commits atomically and later parent updates finish without altering the chosen destination.</summary>
    [Fact]
    public void Committed_or_retired_selection_acknowledges_finished_without_replacing_the_target()
    {
        var options = AutomaticCenterTests.Options().AutomaticEntry!;
        var state = new AutomaticCenterState(options);
        var selected = state.Select(DateTimeOffset.UtcNow, true);
        Assert.Equal("second", selected.CenterId);
        Assert.Equal("finished", state.Handle(Command(options, 1, [AutomaticCenterTests.Candidate("first", 0, 0)])).Status);
        Assert.Equal("second", selected.CenterId);
        Assert.Equal("binding", state.Handle(new GatewayCommand { Type = "refresh-automatic", Revision = 2, Candidates = [] }).Reason);
        var retired = new AutomaticCenterState(options);
        retired.Finish();
        Assert.Equal("finished", retired.Handle(Command(options, 1, [])).Status);
    }

    /// <summary>The real control pipe acknowledges replacement and STOP remains compatible without opening game sockets.</summary>
    [Fact]
    public async Task Parent_control_pipe_routes_updates_and_acknowledges_rejections_before_stop()
    {
        var options = AutomaticCenterTests.Options();
        options.ConnectTimeoutMs = 0;
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        var command = Command(options.AutomaticEntry!, 1, []);
        var accepted = await SendAsync(session, command);
        Assert.Equal("automatic-refresh-result", accepted.Type);
        Assert.Equal("selector", accepted.RowId);
        Assert.Equal(1, accepted.Revision);
        Assert.Equal("accepted", accepted.Status);
        Assert.Equal("revision", (await SendAsync(session, command)).Reason);
        Assert.Equal("unsupported-row", (await SendAsync(session, new GatewayCommand
        {
            Type = "refresh-automatic", RowId = "unknown", Revision = 2, Candidates = [],
        })).Reason);
        Assert.False(session.Work.IsCompleted);
        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" }, GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
    }

    private static GatewayCommand Command(AutomaticCenterOptions options, long revision, AutomaticCenterCandidate[] candidates) => new()
    {
        Type = "refresh-automatic", RowId = "selector", LaunchId = options.LaunchId, GrantId = options.GrantId,
        Revision = revision, Candidates = candidates,
    };

    private static async Task<GatewayCommandResult> SendAsync(GatewayLifecycleTests.Session session, GatewayCommand command)
    {
        await ControlMessageStream.WriteJsonAsync(session.Parent, command, GatewayJsonContext.Strict.GatewayCommand, session.Token);
        var body = await ControlMessageStream.ReadAsync(session.Parent, session.Token);
        return JsonSerializer.Deserialize(body!, GatewayJsonContext.Strict.GatewayCommandResult)!;
    }
}
