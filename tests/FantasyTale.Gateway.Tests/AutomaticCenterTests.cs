using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks selected-state redirect bytes and resource ownership independently of native game acceptance.</summary>
public sealed class AutomaticCenterTests
{
    /// <summary>An auxiliary EOF before native status identifies the failing socket without logging arbitrary messages.</summary>
    [Fact]
    public async Task Auxiliary_eof_before_status_reports_a_fixed_failure_event()
    {
        var options = Options("kr-1256");
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var greeting = new byte[options.Validate().Dialect.PreambleLength(PacketDirection.ServerToClient)];
        await auxiliary.Client.GetStream().ReadExactlyAsync(greeting, session.Token);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));
        auxiliary.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => session.Work.WaitAsync(session.Token));
        Assert.Contains("automatic failure; phase=status, kind=EndOfStreamException.", session.Log.ToString());
    }

    /// <summary>The native status reports the actual selected snapshot and uniqueness retires an unselected auxiliary.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-735")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-324")]
    [InlineData("kr-1256")]
    public async Task Auxiliary_status_and_uniqueness_keep_cipher_state_and_retire_without_redirect(string build)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        var status = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.StatusReply, status.Opcode);
        Assert.Equal(protocol.Status.Write(new Dictionary<string, object?>
        {
            ["center_id"] = 9, ["name"] = options.ResolveAutomaticName(options.AutomaticEntry!), ["users"] = 10, ["capacity"] = 100,
        }), status.Payload);
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        Assert.Equal(status.Payload, Assert.Single(await auxiliary.ReadAsync(1, session.Token)).Payload);
        await auxiliary.SendAsync(protocol.UniquenessRequest, new byte[4], session.Token);
        var uniqueness = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.UniquenessReply, uniqueness.Opcode);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, uniqueness.Payload);
        auxiliary.Dispose();
        Assert.Equal("finished", (await RefreshAsync(session, options, 1, [])).Status);
        Assert.False(session.Work.IsCompleted);
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
        Assert.DoesNotContain("redirect sent", session.Log.ToString());
        Assert.DoesNotContain("connection role=", session.Log.ToString());
    }

    /// <summary>The automatic native status uses the admitted anchor's compact server name when one exists.</summary>
    [Theory]
    [InlineData("Beijing", "自动选择（Beijing）")]
    [InlineData(null, "自动选择（Beijing production cluster）")]
    public async Task Automatic_status_uses_anchor_short_name(string? shortName, string expected)
    {
        var options = Options("cn-324");
        options.Centers[0] = new CenterEntry
        {
            Id = "first", Name = "Beijing production cluster", ShortName = shortName,
            BackendId = "physical-first", NativeCenterId = 2, Host = "192.0.2.10", Port = 7110,
        };
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        var status = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.Status.Write(new Dictionary<string, object?>
        {
            ["center_id"] = 9, ["name"] = expected, ["users"] = 10, ["capacity"] = 100,
        }), status.Payload);
        auxiliary.Dispose();
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>A selected native login receives one ordered move/promotion burst with no fake login reply or ticket forwarding.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-735")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-324")]
    public async Task Selected_login_redirects_with_pinned_bytes_and_redacted_credentials(string build)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var profile = options.Validate();
        var protocol = profile.Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        await auxiliary.ReadAsync(1, session.Token);
        var login = Login(protocol, "private-one-use-worker-ticket");
        await auxiliary.SendAsync(protocol.LoginRequest, login, session.Token, fragmented: true);
        var redirect = await auxiliary.ReadAsync(2, session.Token);
        Assert.Equal(new ushort[] { protocol.MoveReply, protocol.PromotePending }, redirect.Select(frame => frame.Opcode));
        var expected = new byte[] { 0, 0, 0, 0, 198, 51, 100, 20, 0xC6, 0x1B };
        Assert.Equal(protocol.Move.Write(new Dictionary<string, object?>()).Length == 14 ? [.. expected, 0, 0, 0, 0] : expected, redirect[0].Payload);
        Assert.Empty(redirect[1].Payload);
        Assert.DoesNotContain("private-one-use-worker-ticket", session.Log.ToString());
        Assert.DoesNotContain("private-one-use-worker-ticket", System.Text.Json.JsonSerializer.Serialize(session.Ready, GatewayJsonContext.Strict.GatewayReady));
        Assert.False(session.Work.IsCompleted);
        await AcknowledgeReleaseAsync(auxiliary, protocol, session.Token);
        auxiliary.Dispose();
        await session.Work.WaitAsync(session.Token);
        Assert.Equal(0, await bootstrap.Socket.Client.GetStream().ReadAsync(new byte[1], session.Token));
        Assert.Contains("automatic native redirect sent", session.Log.ToString());
        Assert.Contains("Fantasy Tale Gateway stopped.", session.Log.ToString());
    }

    /// <summary>A parent refresh changes the actual native redirect, and a later update cannot disturb committed handoff.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-735")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-324")]
    public async Task Refreshed_worker_is_used_on_selection_and_subsequent_updates_acknowledge_finished(string build)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        Assert.Equal("accepted", (await RefreshAsync(session, options, 1, [Candidate("first", 0, 25)])).Status);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        var status = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.Status.Write(new Dictionary<string, object?>
        {
            ["center_id"] = 9, ["name"] = options.ResolveAutomaticName(options.AutomaticEntry!), ["users"] = 25, ["capacity"] = 100,
        }), status.Payload);
        await auxiliary.SendAsync(protocol.LoginRequest, Login(protocol, "private-refreshed-ticket"), session.Token);
        var redirect = await auxiliary.ReadAsync(2, session.Token);
        var expected = new byte[] { 0, 0, 0, 0, 192, 0, 2, 10, 0xC6, 0x1B };
        Assert.Equal(protocol.Move.Write(new Dictionary<string, object?>()).Length == 14 ? [.. expected, 0, 0, 0, 0] : expected, redirect[0].Payload);
        Assert.Equal("finished", (await RefreshAsync(session, options, 2, [])).Status);
        Assert.False(session.Work.IsCompleted);
        await AcknowledgeReleaseAsync(auxiliary, protocol, session.Token);
        auxiliary.Dispose();
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>A frozen refresh acknowledgement followed by source release and parent STOP never turns normal cleanup into failure.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("id-400", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-406", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-408", false)]
    [InlineData("cn-706", false)]
    [InlineData("cn-717", false)]
    [InlineData("id-1809", false)]
    [InlineData("cn-735", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("kr-812", true)]
    [InlineData("id-400", true)]
    [InlineData("cn-279", true)]
    [InlineData("cn-406", true)]
    [InlineData("cn-404", true)]
    [InlineData("cn-408", true)]
    [InlineData("cn-706", true)]
    [InlineData("cn-717", true)]
    [InlineData("id-1809", true)]
    [InlineData("cn-735", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-324", false)]
    [InlineData("cn-324", true)]
    public async Task Redirect_release_and_parent_stop_complete_normally_after_finished_ack(string build, bool releaseFirst)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        await auxiliary.ReadAsync(1, session.Token);
        await auxiliary.SendAsync(protocol.LoginRequest, Login(protocol, "private-stop-race-ticket"), session.Token);
        await auxiliary.ReadAsync(2, session.Token);
        Assert.Equal("finished", (await RefreshAsync(session, options, 1, [])).Status);
        await AcknowledgeReleaseAsync(auxiliary, protocol, session.Token);
        if (releaseFirst)
        {
            auxiliary.Dispose();
        }
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        var stop = ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" },
            GatewayJsonContext.Strict.GatewayCommand, stopping.Token).AsTask();
        // Selected release can complete the host before its parent reader consumes STOP.
        // The in-process fixture retains that pipe until disposal, unlike the exiting executable.
        if (await Task.WhenAny(stop, session.Work).WaitAsync(session.Token) == session.Work)
            await stopping.CancelAsync();
        try { await stop; }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !session.Token.IsCancellationRequested) { }
        if (!releaseFirst) auxiliary.Dispose();
        await session.Work.WaitAsync(session.Token);
        Assert.Equal(0, await bootstrap.Socket.Client.GetStream().ReadAsync(new byte[1], session.Token));
        Assert.DoesNotContain("private-stop-race-ticket", session.Log.ToString());
    }

    /// <summary>Withdrawing candidates closes the automatic socket without terminating the manual bootstrap.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("id-400", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-406", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-408", false)]
    [InlineData("cn-706", false)]
    [InlineData("cn-717", false)]
    [InlineData("id-1809", false)]
    [InlineData("cn-735", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("kr-812", true)]
    [InlineData("id-400", true)]
    [InlineData("cn-279", true)]
    [InlineData("cn-406", true)]
    [InlineData("cn-404", true)]
    [InlineData("cn-408", true)]
    [InlineData("cn-706", true)]
    [InlineData("cn-717", true)]
    [InlineData("id-1809", true)]
    [InlineData("cn-735", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-324", false)]
    [InlineData("cn-324", true)]
    public async Task Unavailable_automatic_status_or_selected_login_preserves_manual_bootstrap(string build, bool afterStatus)
    {
        var options = Options(build);
        options.HandshakeTimeoutMs = 500;
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        if (afterStatus)
        {
            await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
            await auxiliary.ReadAsync(1, session.Token);
        }
        Assert.Equal("accepted", (await RefreshAsync(session, options, 1, [])).Status);
        await auxiliary.SendAsync(afterStatus ? protocol.LoginRequest : protocol.StatusRequest,
            afterStatus ? Login(protocol, "private-withdrawn-ticket") : [], session.Token);
        var buffer = new byte[4096];
        while (await auxiliary.Client.GetStream().ReadAsync(buffer, session.Token) != 0) { }
        Assert.Equal("finished", (await RefreshAsync(session, options, 2, [])).Status);
        Assert.False(session.Work.IsCompleted);
        Assert.Contains("manual bootstrap retained", session.Log.ToString());
        Assert.DoesNotContain("private-withdrawn-ticket", session.Log.ToString());
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>Explicit parent-owned accept waits survive resource preparation while accepted handshakes remain bounded.</summary>
    [Fact]
    public async Task Parent_owned_accept_does_not_start_the_handshake_deadline_before_a_game_connects()
    {
        var options = Options();
        options.ConnectTimeoutMs = 0;
        options.HandshakeTimeoutMs = 500;
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        await Task.Delay(750, session.Token);
        Assert.False(session.Work.IsCompleted);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        await auxiliary.ReadAsync(1, session.Token);
        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" }, GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>Actual status and selected-login requests can await one bounded parent refresh before committing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_snapshot_recovers_during_the_real_auxiliary_freshness_wait(bool afterStatus)
    {
        var original = Options();
        var options = new CenterSelectorOptions
        {
            Build = original.Build, Centers = original.Centers,
            AutomaticEntry = Auto(afterStatus ? [Candidate("second", 1, 0)] : [], admitted: ["first", "second"]),
        };
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        if (afterStatus)
        {
            await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
            await auxiliary.ReadAsync(1, session.Token);
            Assert.Equal("accepted", (await RefreshAsync(session, options, 1, [])).Status);
        }
        await auxiliary.SendAsync(afterStatus ? protocol.LoginRequest : protocol.StatusRequest,
            afterStatus ? Login(protocol, "private-waiting-ticket") : [], session.Token);
        while (!session.Log.ToString().Contains("automatic awaiting fresh snapshot", StringComparison.Ordinal))
            await Task.Delay(10, session.Token);
        Assert.Equal("accepted", (await RefreshAsync(session, options, 2, [Candidate("first", 0, 25)])).Status);
        if (!afterStatus)
        {
            await auxiliary.ReadAsync(1, session.Token);
            await auxiliary.SendAsync(protocol.LoginRequest, Login(protocol, "private-waiting-ticket"), session.Token);
        }
        var redirect = await auxiliary.ReadAsync(2, session.Token);
        var expected = new byte[] { 0, 0, 0, 0, 192, 0, 2, 10, 0xC6, 0x1B };
        Assert.Equal(protocol.Move.Write(new Dictionary<string, object?>()).Length == 14 ? [.. expected, 0, 0, 0, 0] : expected, redirect[0].Payload);
        Assert.Equal("finished", (await RefreshAsync(session, options, 3, [])).Status);
        Assert.DoesNotContain("private-waiting-ticket", session.Log.ToString());
        auxiliary.Dispose();
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>Parent STOP wakes an automatic request waiting for observations and releases both owned connections.</summary>
    [Fact]
    public async Task Parent_stop_cancels_an_active_freshness_wait()
    {
        var original = Options();
        var options = new CenterSelectorOptions
        {
            Build = original.Build, Centers = original.Centers,
            AutomaticEntry = Auto([], admitted: ["first", "second"]),
        };
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        await auxiliary.SendAsync(options.Validate().Automatic!.StatusRequest, [], session.Token);
        while (!session.Log.ToString().Contains("automatic awaiting fresh snapshot", StringComparison.Ordinal))
            await Task.Delay(10, session.Token);
        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" }, GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
        var buffer = new byte[4096];
        while (await auxiliary.Client.GetStream().ReadAsync(buffer, session.Token) != 0) { }
        Assert.Equal(0, await bootstrap.Socket.Client.GetStream().ReadAsync(new byte[1], session.Token));
    }

    /// <summary>A socket alone or an unaudited first-connect cannot stand in for native status followed by selection.</summary>
    [Theory]
    [InlineData("kr-812", false, false)]
    [InlineData("id-400", false, false)]
    [InlineData("cn-279", false, false)]
    [InlineData("cn-406", false, false)]
    [InlineData("cn-404", false, false)]
    [InlineData("cn-408", false, false)]
    [InlineData("cn-706", false, false)]
    [InlineData("cn-717", false, false)]
    [InlineData("id-1809", false, false)]
    [InlineData("cn-735", false, false)]
    [InlineData("cn-774", false, false)]
    [InlineData("tw-454", false, false)]
    [InlineData("kr-812", true, false)]
    [InlineData("id-400", true, false)]
    [InlineData("cn-279", true, false)]
    [InlineData("cn-406", true, false)]
    [InlineData("cn-404", true, false)]
    [InlineData("cn-408", true, false)]
    [InlineData("cn-706", true, false)]
    [InlineData("cn-717", true, false)]
    [InlineData("id-1809", true, false)]
    [InlineData("cn-735", true, false)]
    [InlineData("cn-774", true, false)]
    [InlineData("tw-454", true, false)]
    [InlineData("kr-812", true, true)]
    [InlineData("id-400", true, true)]
    [InlineData("cn-279", true, true)]
    [InlineData("cn-406", true, true)]
    [InlineData("cn-404", true, true)]
    [InlineData("cn-408", true, true)]
    [InlineData("cn-706", true, true)]
    [InlineData("cn-717", true, true)]
    [InlineData("id-1809", true, true)]
    [InlineData("cn-735", true, true)]
    [InlineData("cn-774", true, true)]
    [InlineData("tw-454", true, true)]
    [InlineData("cn-324", false, false)]
    [InlineData("cn-324", true, false)]
    [InlineData("cn-324", true, true)]
    public async Task Invalid_selection_sequence_or_login_shape_fails_and_cancels_bootstrap(string build, bool status, bool malformed)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        if (status)
        {
            await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
            await auxiliary.ReadAsync(1, session.Token);
        }
        await auxiliary.SendAsync(status && !malformed ? (ushort)1 : protocol.LoginRequest,
            malformed ? new byte[3] : Login(protocol, "private-sequence-ticket"), session.Token);
        await Assert.ThrowsAsync<PacketFormatException>(() => session.Work.WaitAsync(session.Token));
        Assert.DoesNotContain("private-sequence-ticket", session.Log.ToString());
        Assert.DoesNotContain("redirect sent", session.Log.ToString());
    }

    /// <summary>Parent STOP and pipe EOF cancel both listeners and accepted sockets without waiting for any target response.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("id-400", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-406", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-408", false)]
    [InlineData("cn-706", false)]
    [InlineData("cn-717", false)]
    [InlineData("id-1809", false)]
    [InlineData("cn-735", false)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("kr-812", true)]
    [InlineData("id-400", true)]
    [InlineData("cn-279", true)]
    [InlineData("cn-406", true)]
    [InlineData("cn-404", true)]
    [InlineData("cn-408", true)]
    [InlineData("cn-706", true)]
    [InlineData("cn-717", true)]
    [InlineData("id-1809", true)]
    [InlineData("cn-735", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-324", false)]
    [InlineData("cn-324", true)]
    public async Task Parent_stop_or_eof_releases_automatic_and_bootstrap_sockets(string build, bool eof)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        await auxiliary.ReadAsync(1, session.Token);
        if (eof) session.Parent.Dispose();
        else await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" }, GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
        Assert.Equal(0, await auxiliary.Client.GetStream().ReadAsync(new byte[1], session.Token));
        using var retry = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () => await retry.ConnectAsync(IPAddress.Loopback, bootstrap.AutomaticPort, session.Token));
    }

    /// <summary>A redirect does not imply a connected worker, and a source that never releases expires explicitly.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-735")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-324")]
    [InlineData("kr-1256")]
    public async Task Source_release_deadline_remains_a_failure_without_target_acceptance(string build)
    {
        var options = Options(build, redirectTimeoutMs: 200);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        await auxiliary.ReadAsync(1, session.Token);
        await auxiliary.SendAsync(protocol.LoginRequest, Login(protocol, "private-timeout-ticket"), session.Token);
        await auxiliary.ReadAsync(2, session.Token);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => session.Work.WaitAsync(session.Token));
        Assert.Equal("Automatic Center release deadline expired.", error.Message);
    }

    /// <summary>Health and capacity exclude workers before RTT, occupancy and ordinal tie-breaking are evaluated.</summary>
    [Fact]
    public void Candidate_selection_is_explicit_deterministic_and_fails_without_eligible_workers()
    {
        var options = Options();
        var automatic = options.AutomaticEntry!;
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("second", automatic.Select(now).CenterId);
        Assert.Throws<GatewayConfigurationException>(() => automatic.Select(now.AddDays(2)));
        var candidates = new[]
        {
            Candidate("unhealthy", 0, 0, healthy: false), Candidate("full", 0, 100),
            Candidate("expired-unhealthy", 0, 0, healthy: false, expires: now.AddSeconds(-1)),
            Candidate("high-load", 100, 90), Candidate("b", 100, 10), Candidate("a", 100, 10),
        };
        Assert.Equal("a", Auto(candidates).Select(now).CenterId);
    }

/// <summary>Native login validation is bounded and does not accept absent credentials or oversized adapter vectors.</summary>
    [Theory]
    [InlineData("kr-812", 26)]
    [InlineData("cn-324", 18)]
    [InlineData("id-400", 18)]
    [InlineData("cn-279", 18)]
    [InlineData("cn-406", 18)]
    [InlineData("cn-404", 18)]
    [InlineData("cn-408", 18)]
    [InlineData("cn-706", 22)]
    [InlineData("cn-717", 26)]
    [InlineData("id-1809", 22)]
    [InlineData("cn-735", 26)]
    [InlineData("cn-774", 30)]
    [InlineData("tw-454", 30)]
    [InlineData("kr-1256", 30)]
    public void Login_shape_pins_length_and_rejects_empty_strings_trailing_bytes_and_adapter_exhaustion(string build, int emptyAdaptersLength)
    {
        var protocol = Options(build).Validate().Automatic!;
        var login = Login(protocol, "x");
        Assert.Equal(emptyAdaptersLength, login.Length);
        protocol.Login.ValidateLogin(login, 1024, 32);
        var adapterLogin = protocol.Login.Write(new Dictionary<string, object?>
        {
            ["credential"] = "x", ["adapters"] = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["ipv4_word"] = 0x370200C0u },
            },
        });
        Assert.Equal(emptyAdaptersLength + 4, adapterLogin.Length);
        Assert.Equal(new byte[] { 1, 0, 0, 0, 192, 0, 2, 55 }, adapterLogin.AsSpan(14, 8).ToArray());
        protocol.Login.ValidateLogin(adapterLogin, 1024, 1);
        Assert.Throws<PacketFormatException>(() => protocol.Login.ValidateLogin(Login(protocol, ""), 1024, 32));
        Assert.Throws<PacketFormatException>(() => protocol.Login.ValidateLogin([.. login, 0], 1024, 32));
        Assert.Throws<PacketFormatException>(() => protocol.Login.ValidateLogin(Login(protocol, "long"), 1, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(login.AsSpan(14), uint.MaxValue);
        Assert.Throws<PacketFormatException>(() => protocol.Login.ValidateLogin(login, 1024, 32));
        Assert.Equal(emptyAdaptersLength == 30 ? 14 : 10, protocol.Move.Write(new Dictionary<string, object?>()).Length);
        Assert.Empty(protocol.Promotion.Write(new Dictionary<string, object?>()));
    }

    /// <summary>The modern source completes its empty native acknowledgement before retiring the obsolete bootstrap.</summary>
    [Theory]
    [InlineData("kr-1256", false, 16, 18, 8, 9, 10, 14)]
    [InlineData("kr-1256", true, 16, 18, 8, 9, 10, 14)]
    [InlineData("cn-735", false, 17, 19, 9, 10, 11, 10)]
    [InlineData("cn-735", true, 17, 19, 9, 10, 11, 10)]
    [InlineData("cn-774", false, 18, 20, 10, 11, 12, 14)]
    [InlineData("cn-774", true, 18, 20, 10, 11, 12, 14)]
    [InlineData("tw-454", false, 18, 20, 10, 11, 12, 14)]
    [InlineData("tw-454", true, 18, 20, 10, 11, 12, 14)]
    public async Task Modern_redirect_pins_native_offer_and_acknowledged_source_close(string build, bool fragmented,
        ushort statusReply, ushort uniquenessReply, ushort moveReply, ushort promote, ushort completion, int moveLength)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        Assert.Equal(new ushort[] { 22, statusReply, 23, uniquenessReply, 3, moveReply, promote }, new ushort[]
        {
            protocol.StatusRequest, protocol.StatusReply, protocol.UniquenessRequest, protocol.UniquenessReply,
            protocol.LoginRequest, protocol.MoveReply, protocol.PromotePending,
        });
        Assert.Equal((ushort)55, protocol.Release!.Request);
        Assert.Equal(completion, protocol.Release.Reply);
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        await auxiliary.ReadAsync(1, session.Token);
        await auxiliary.SendAsync(protocol.LoginRequest, Login(protocol, "private-modern-ticket"), session.Token);
        var redirect = await auxiliary.ReadAsync(2, session.Token);
        Assert.Equal(new ushort[] { moveReply, promote }, redirect.Select(frame => frame.Opcode));
        Assert.Equal(moveLength == 14 ? "00000000C6336414C61B00000000" : "00000000C6336414C61B", Convert.ToHexString(redirect[0].Payload));
        Assert.Empty(redirect[1].Payload);
        Assert.False(session.Work.IsCompleted);
        await auxiliary.SendAsync(protocol.Release.Request, [], session.Token, fragmented);
        var completed = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(completion, completed.Opcode);
        Assert.Empty(completed.Payload);
        Assert.False(session.Work.IsCompleted);
        auxiliary.Dispose();
        await session.Work.WaitAsync(session.Token);
        Assert.Equal(0, await bootstrap.Socket.Client.GetStream().ReadAsync(new byte[1], session.Token));
        Assert.Contains("source promotion acknowledged", session.Log.ToString());
        Assert.DoesNotContain("private-modern-ticket", session.Log.ToString());
    }

    /// <summary>A wrong, nonempty or repeated modern release frame fails without silently accepting malformed handoff.</summary>
    [Theory]
    [InlineData("kr-1256", 0)]
    [InlineData("kr-1256", 1)]
    [InlineData("kr-1256", 2)]
    [InlineData("cn-735", 0)]
    [InlineData("cn-735", 1)]
    [InlineData("cn-735", 2)]
    [InlineData("cn-774", 0)]
    [InlineData("cn-774", 1)]
    [InlineData("cn-774", 2)]
    [InlineData("tw-454", 0)]
    [InlineData("tw-454", 1)]
    [InlineData("tw-454", 2)]
    public async Task Modern_release_rejects_wrong_nonempty_or_duplicate_acknowledgements(string build, int kind)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        await auxiliary.ReadAsync(1, session.Token);
        await auxiliary.SendAsync(protocol.LoginRequest, Login(protocol, "private-invalid-release-ticket"), session.Token);
        await auxiliary.ReadAsync(2, session.Token);
        if (kind == 2)
        {
            await auxiliary.SendAsync(protocol.Release!.Request, [], session.Token);
            await auxiliary.ReadAsync(1, session.Token);
        }
        await auxiliary.SendAsync(kind == 0 ? protocol.StatusRequest : protocol.Release!.Request,
            kind == 1 ? [1] : [], session.Token);
        await Assert.ThrowsAsync<PacketFormatException>(() => session.Work.WaitAsync(session.Token));
        Assert.DoesNotContain("private-invalid-release-ticket", session.Log.ToString());
    }

    /// <summary>A modern source cannot claim a graceful handoff by disconnecting before its native acknowledgement.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-735")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task Modern_release_requires_acknowledgement_before_eof(string build)
    {
        var options = Options(build);
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        await auxiliary.ReadAsync(1, session.Token);
        await auxiliary.SendAsync(protocol.LoginRequest, Login(protocol, "private-missing-release-ticket"), session.Token);
        await auxiliary.ReadAsync(2, session.Token);
        auxiliary.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => session.Work.WaitAsync(session.Token));
        Assert.DoesNotContain("source released", session.Log.ToString());
    }

    /// <summary>The modern login includes its final counted string after both retained words.</summary>
    [Fact]
    public void Modern_login_pins_adapter_words_and_final_string_without_legacy_offsets()
    {
        var protocol = Options("kr-1256").Validate().Automatic!;
        var payload = protocol.Login.Write(new Dictionary<string, object?>
        {
            ["credential"] = "x", ["unknown_24"] = "a", ["unknown_48"] = "b",
            ["adapters"] = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["ipv4_word"] = 0x370200C0u },
            },
            ["unknown_84"] = 0x11223344u, ["unknown_88"] = 0x55667788u, ["unknown_92"] = "z",
        });
        Assert.Equal("01000000780001000000610001000000620001000000C00002374433221188776655010000007A00", Convert.ToHexString(payload));
        protocol.Login.ValidateLogin(payload, 1, 1);
        Assert.Throws<PacketFormatException>(() => protocol.Login.ValidateLogin(payload[..^1], 1, 1));
        Assert.Throws<PacketFormatException>(() => protocol.Login.ValidateLogin(payload[..^6], 1, 1));
    }

    /// <summary>Omitting optional resource limits preserves their constructor defaults through generated JSON metadata.</summary>
    [Fact]
    public void Optional_limits_survive_source_generated_json_when_omitted()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Options(), GatewayJsonContext.Strict.CenterSelectorOptions);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["automatic_entry"]!.AsObject().Remove("redirect_timeout_ms");
        node["automatic_entry"]!.AsObject().Remove("max_login_adapters");
        var parsed = System.Text.Json.JsonSerializer.Deserialize(node.ToJsonString(), GatewayJsonContext.Strict.CenterSelectorOptions)!;
        Assert.Equal(10000, parsed.AutomaticEntry!.RedirectTimeoutMs);
        Assert.Equal(32, parsed.AutomaticEntry.MaxLoginAdapters);
        parsed.Validate();
    }

    /// <summary>Duplicate physical destinations and collisions with the automatic identity cannot create fake alternatives.</summary>
    [Fact]
    public void Aliases_and_identity_collisions_fail_while_empty_or_stale_snapshots_can_be_refreshed()
    {
        var valid = Options();
        var original = valid.Centers[0];
        var alias = new CenterEntry { Id = "second", Name = "Second", BackendId = original.BackendId,
            NativeCenterId = 3, Host = original.Host, Port = original.Port };
        Assert.Throws<GatewayConfigurationException>(() => new CenterSelectorOptions
        {
            Build = "kr-812", Centers = [original, alias], AutomaticEntry = valid.AutomaticEntry,
        }.Validate());
        Assert.Throws<GatewayConfigurationException>(() => new CenterSelectorOptions
        {
            Build = "kr-812", Centers = valid.Centers, AutomaticEntry = Auto([Candidate("absent", 1, 0)]),
        }.Validate());
        new CenterSelectorOptions
        {
            Build = "kr-812", Centers = valid.Centers, AutomaticEntry = Auto([Candidate("first", 1, 0, expires: DateTimeOffset.UtcNow.AddDays(-1))]),
        }.Validate();
        new CenterSelectorOptions
        {
            Build = "kr-812", Centers = valid.Centers, AutomaticEntry = Auto([Candidate("first", 1, 100)]),
        }.Validate();
        new CenterSelectorOptions
        {
            Build = "kr-812", Centers = valid.Centers, AutomaticEntry = Auto([], admitted: ["first", "second"]),
        }.Validate();
    }

    /// <summary>Neither a local automatic endpoint nor the bootstrap endpoint can appear as its own worker.</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    public void Local_redirect_loops_are_rejected(string host)
    {
        var options = new CenterSelectorOptions { Build = "kr-812", Centers = [new CenterEntry
        {
            Id = "worker", Name = "Worker", Host = host, Port = 7110,
        }] };
        Assert.Throws<GatewayConfigurationException>(() => BfCenterSelectorPlugin.ValidateRedirectTargets(options, 7110));
        BfCenterSelectorPlugin.ValidateRedirectTargets(options, 7111);
    }

    /// <summary>CN2012 keeps its own native opcodes and complete counted-word login body without the KR trailer.</summary>
    [Fact]
    public void Chinese_native_redirect_uses_its_own_opcode_spaces_and_complete_login_layout()
    {
        var protocol = Options("cn-324").Validate().Automatic!;
        Assert.Equal(new ushort[] { 22, 15, 23, 17, 3, 8, 9 }, new ushort[]
        {
            protocol.StatusRequest, protocol.StatusReply, protocol.UniquenessRequest, protocol.UniquenessReply,
            protocol.LoginRequest, protocol.MoveReply, protocol.PromotePending,
        });
        var payload = protocol.Login.Write(new Dictionary<string, object?>
        {
            ["credential"] = "x", ["unknown_8"] = "a", ["unknown_12"] = "",
            ["adapters"] = new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["ipv4_word"] = 0x370200C0u },
            },
        });
        Assert.Equal(new byte[] { 1, 0, 0, 0, 120, 0, 1, 0, 0, 0, 97, 0, 0, 0, 0, 0, 1, 0, 0, 0, 192, 0, 2, 55 }, payload);
        protocol.Login.ValidateLogin(payload, 1024, 1);
        Assert.Throws<PacketFormatException>(() => protocol.Login.ValidateLogin([.. payload, .. new byte[8]], 1024, 1));
    }

    /// <summary>Only one empty native start marker is accepted before the auxiliary status exchange.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Chinese_native_marker_rejects_nonempty_duplicate_or_late_input(int kind)
    {
        var options = Options("cn-324");
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token, sendMarker: kind != 1);
        Assert.Equal((ushort)2, options.Validate().Marker);
        if (kind == 2)
        {
            await auxiliary.SendAsync(options.Validate().Automatic!.StatusRequest, [], session.Token);
            await auxiliary.ReadAsync(1, session.Token);
        }
        await auxiliary.SendAsync(2, kind == 1 ? [1] : [], session.Token);
        await Assert.ThrowsAsync<PacketFormatException>(() => session.Work.WaitAsync(session.Token));
        Assert.DoesNotContain("redirect sent", session.Log.ToString());
    }

    /// <summary>An unavailable native status preserves its configured name, blocks selection and never promotes a worker.</summary>
    [Theory]
    [InlineData("empty", false)]
    [InlineData("unhealthy", false)]
    [InlineData("full", false)]
    [InlineData("empty", true)]
    [InlineData("unhealthy", true)]
    [InlineData("full", true)]
    public async Task Unavailable_status_keeps_real_label_and_rejects_login_or_retires_uniqueness(string fault, bool uniqueness)
    {
        var original = Options("cn-324");
        var candidates = fault switch
        {
            "unhealthy" => [Candidate("first", 1, 1, healthy: false)],
            "full" => [Candidate("first", 1, 100)],
            _ => Array.Empty<AutomaticCenterCandidate>(),
        };
        var options = new CenterSelectorOptions
        {
            Build = original.Build, Centers = original.Centers,
            AutomaticEntry = Auto(candidates, admitted: ["first", "second"], unavailableCapacity: 731, name: "Automatic Primary"),
        };
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        var status = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.StatusReply, status.Opcode);
        var reader = new PacketReader(status.Payload);
        Assert.Equal(9u, reader.ReadUInt32());
        Assert.Equal(options.ResolveAutomaticName(options.AutomaticEntry!), reader.ReadWideStringPrefixed());
        Assert.Equal(1462u, reader.ReadUInt32());
        Assert.Equal(731u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        // A later healthy refresh cannot turn an already unavailable auxiliary into a selectable target.
        Assert.Equal("accepted", (await RefreshAsync(session, options, 1, [Candidate("first", 1, 0)])).Status);
        await auxiliary.SendAsync(uniqueness ? protocol.UniquenessRequest : protocol.LoginRequest,
            uniqueness ? new byte[4] : Login(protocol, "private-unavailable-ticket"), session.Token);
        if (uniqueness)
        {
            var reply = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
            Assert.Equal(protocol.UniquenessReply, reply.Opcode);
            Assert.Equal(new byte[] { 1, 0, 0, 0 }, reply.Payload);
            auxiliary.Dispose();
        }
        else Assert.Equal(0, await auxiliary.Client.GetStream().ReadAsync(new byte[1], session.Token));
        Assert.Equal("finished", (await RefreshAsync(session, options, 2, [])).Status);
        Assert.False(session.Work.IsCompleted);
        bootstrap.Dispose();
        await session.Work.WaitAsync(session.Token);
        Assert.DoesNotContain("redirect sent", session.Log.ToString());
        Assert.DoesNotContain("private-unavailable-ticket", session.Log.ToString());
    }

    /// <summary>A healthy heartbeat remains selectable through status and login despite source clock skew.</summary>
    [Theory]
    [InlineData("cn-324", 0)]
    [InlineData("cn-406", -19)]
    [InlineData("cn-406", -3600)]
    public async Task Healthy_heartbeat_supplies_status_and_native_redirect_regardless_of_source_clock(string build, int expiryOffsetSeconds)
    {
        var original = Options(build);
        var options = new CenterSelectorOptions
        {
            Build = original.Build, Centers = original.Centers,
            AutomaticEntry = Auto([Candidate("first", 1, 27, expires: DateTimeOffset.UtcNow.AddSeconds(expiryOffsetSeconds))], unavailableCapacity: 731, name: "Automatic Primary"),
        };
        await using var session = await GatewayLifecycleTests.Session.StartAsync(options);
        using var bootstrap = await ConnectBootstrapAsync(session, options);
        using var auxiliary = await ConnectAsync(bootstrap.AutomaticPort, options, session.Token);
        var protocol = options.Validate().Automatic!;
        await auxiliary.SendAsync(protocol.StatusRequest, [], session.Token);
        var status = Assert.Single(await auxiliary.ReadAsync(1, session.Token));
        Assert.Equal(protocol.Status.Write(new Dictionary<string, object?>
        {
            ["center_id"] = 9, ["name"] = options.ResolveAutomaticName(options.AutomaticEntry!), ["users"] = 27, ["capacity"] = 100,
        }), status.Payload);
        await auxiliary.SendAsync(protocol.LoginRequest, Login(protocol, "private-singleton-ticket"), session.Token);
        var redirect = await auxiliary.ReadAsync(2, session.Token);
        Assert.Equal(new ushort[] { protocol.MoveReply, protocol.PromotePending }, redirect.Select(frame => frame.Opcode));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 192, 0, 2, 10, 0xC6, 0x1B }, redirect[0].Payload);
        Assert.Empty(redirect[1].Payload);
        auxiliary.Dispose();
        await session.Work.WaitAsync(session.Token);
        Assert.DoesNotContain("private-singleton-ticket", session.Log.ToString());
    }

    /// <summary>The unavailable occupancy sentinel must remain positive and within native signed word arithmetic.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1073741824)]
    public void Unavailable_capacity_requires_a_safe_positive_pool_capacity(int capacity)
    {
        var original = Options("cn-324");
        var options = new CenterSelectorOptions
        {
            Build = original.Build, Centers = original.Centers,
            AutomaticEntry = Auto([], admitted: ["first"], unavailableCapacity: capacity),
        };
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Admission uses explicit identities even when an admitted label describes a test node.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    public void Explicit_admission_retains_test_named_workers_and_excludes_only_the_manual_worker(string build)
    {
        var options = new CenterSelectorOptions
        {
            Build = build,
            Centers =
            [
                new() { Id = "production", Name = "Production", BackendId = "production", NativeCenterId = 2, Host = "192.0.2.10", Port = 7110 },
                new() { Id = "test-current", Name = "Test current", BackendId = "current", NativeCenterId = 3, Host = "192.0.2.20", Port = 7110 },
                new() { Id = "test-retired", Name = "Test retired", BackendId = "retired", NativeCenterId = 4, Host = "192.0.2.30", Port = 7110 },
            ],
            AutomaticEntry = Auto([Candidate("production", 500, 0), Candidate("test-current", 10, 0)],
                admitted: ["production", "test-current"]),
        };
        options.Validate();
        var state = new AutomaticCenterState(options.AutomaticEntry);
        Assert.Equal("test-current", state.Select(DateTimeOffset.UtcNow, commit: false).CenterId);
        var result = state.Handle(new GatewayCommand
        {
            Type = "refresh-automatic", LaunchId = options.AutomaticEntry.LaunchId,
            GrantId = options.AutomaticEntry.GrantId, Revision = 1,
            Candidates = [Candidate("test-retired", 0, 0)],
        });
        Assert.Equal("rejected", result.Status);
        Assert.Equal("test-current", state.Select(DateTimeOffset.UtcNow, commit: false).CenterId);
        Assert.Contains(options.Centers, center => center.Id == "test-retired");
    }

    internal static CenterSelectorOptions Options(string build = "kr-812", int redirectTimeoutMs = 10000) => new()
    {
        Build = build,
        Centers =
        [
            new CenterEntry { Id = "first", Name = "First", BackendId = "physical-first", NativeCenterId = 2, Host = "192.0.2.10", Port = 7110 },
            new CenterEntry { Id = "second", Name = "Second", BackendId = "physical-second", NativeCenterId = 3, Host = "198.51.100.20", Port = 7110 },
        ],
        AutomaticEntry = Auto([Candidate("first", 500, 0), Candidate("second", 200, 10)], redirectTimeoutMs),
    };

    internal static AutomaticCenterOptions Auto(AutomaticCenterCandidate[] candidates, int redirectTimeoutMs = 10000,
        string[]? admitted = null, int? unavailableCapacity = null, string name = "Automatic", DateTimeOffset? grantExpiresAtUtc = null) => new()
    {
        Capability = AutomaticCenterOptions.SupportedCapability, Id = "automatic", Name = name, NativeCenterId = 9,
        UnavailableCapacity = unavailableCapacity,
        SelectionPolicy = "lowest-rtt-then-load", Candidates = candidates, RedirectTimeoutMs = redirectTimeoutMs,
        LaunchId = "0123456789abcdef0123456789abcdef", GrantId = "fixture-grant", GrantExpiresAtUtc = grantExpiresAtUtc ?? DateTimeOffset.UtcNow.AddDays(2),
        AdmittedCenterIds = admitted ?? candidates.Select(candidate => candidate.CenterId).Distinct(StringComparer.Ordinal).ToArray(),
    };

    internal static AutomaticCenterCandidate Candidate(string id, int rtt, int users, bool healthy = true, DateTimeOffset? expires = null) => new()
    {
        CenterId = id, Healthy = healthy, RttUs = rtt, Users = users, Capacity = 100, ExpiresAtUtc = expires ?? DateTimeOffset.UtcNow.AddDays(1),
        ObservedAtUtc = (expires ?? DateTimeOffset.UtcNow.AddDays(1)).AddMinutes(-1), StatusRevision = "fixture-observation",
    };

    private static byte[] Login(AutomaticCenterProtocol protocol, string credential) => protocol.Login.Write(new Dictionary<string, object?>
    {
        ["credential"] = credential, ["adapters"] = Array.Empty<IReadOnlyDictionary<string, object?>>(),
    });

    private static async Task<GatewayCommandResult> RefreshAsync(GatewayLifecycleTests.Session session, CenterSelectorOptions options,
        long revision, AutomaticCenterCandidate[] candidates)
    {
        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand
        {
            Type = "refresh-automatic", RowId = "selector", LaunchId = options.AutomaticEntry!.LaunchId,
            GrantId = options.AutomaticEntry.GrantId, Revision = revision, Candidates = candidates,
        }, GatewayJsonContext.Strict.GatewayCommand, session.Token);
        var body = await ControlMessageStream.ReadAsync(session.Parent, session.Token);
        return System.Text.Json.JsonSerializer.Deserialize(body!, GatewayJsonContext.Strict.GatewayCommandResult)!;
    }

    private static async Task AcknowledgeReleaseAsync(NativeSocket socket, AutomaticCenterProtocol protocol, CancellationToken ct)
    {
        if (protocol.Release is not { } release) return;
        await socket.SendAsync(release.Request, [], ct);
        var completion = Assert.Single(await socket.ReadAsync(1, ct));
        Assert.Equal(release.Reply, completion.Opcode);
        Assert.Empty(completion.Payload);
    }

    private static async Task<Bootstrap> ConnectBootstrapAsync(GatewayLifecycleTests.Session session, CenterSelectorOptions options)
    {
        var client = await session.ConnectGameAsync();
        var native = new NativeSocket(client, options.Validate());
        await native.SendAsync(native.Profile.PatchRequest, native.Profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = native.Profile.Major }), session.Token);
        var frames = await native.ReadAsync(2, session.Token);
        foreach (var exchange in native.Profile.BootstrapExchanges)
        {
            var request = exchange.RequestLayout.Write(new Dictionary<string, object?> { ["mask"] = 1u });
            await native.SendAsync(exchange.Request, request, session.Token, fragmented: true);
            var response = Assert.Single(await native.ReadAsync(1, session.Token));
            Assert.Equal(exchange.Reply, response.Opcode);
            Assert.Equal(exchange.ReplyPayload, response.Payload);
        }
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(frames[1].Payload));
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, frames[1].Payload.AsSpan(16, 4).ToArray());
        return new Bootstrap(native, BinaryPrimitives.ReadUInt16LittleEndian(frames[1].Payload.AsSpan(20, 2)));
    }

    private static async Task<NativeSocket> ConnectAsync(int port, CenterSelectorOptions options, CancellationToken ct, bool sendMarker = true)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        var native = new NativeSocket(client, options.Validate());
        if (sendMarker && native.Profile.Marker is { } marker) await native.SendAsync(marker, [], ct);
        return native;
    }

    private sealed record Bootstrap(NativeSocket Socket, int AutomaticPort) : IDisposable
    {
        public void Dispose() => Socket.Dispose();
    }

    internal sealed class NativeSocket(TcpClient client, BubbleFighterBuildProfile profile) : IDisposable
    {
        private readonly IStreamTransform _encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer);
        private readonly WireStreamDecoder _decoder = new(profile.Dialect, PacketDirection.ServerToClient, 16384);
        internal TcpClient Client { get; } = client;
        internal BubbleFighterBuildProfile Profile { get; } = profile;
        internal async Task SendAsync(ushort opcode, byte[] body, CancellationToken ct, bool fragmented = false,
            Func<Task>? beforeFinalFragment = null)
        {
            var bytes = Profile.Dialect.EncodeFrame(opcode, body);
            _encryptor.Apply(bytes);
            if (fragmented)
            {
                await Client.GetStream().WriteAsync(bytes.AsMemory(0, 2), ct);
                if (beforeFinalFragment is not null) await beforeFinalFragment();
                await Client.GetStream().WriteAsync(bytes.AsMemory(2), ct);
            }
            else await Client.GetStream().WriteAsync(bytes, ct);
        }
        internal async Task<List<DecodedFrame>> ReadAsync(int count, CancellationToken ct)
        {
            var frames = new List<DecodedFrame>();
            var buffer = new byte[4096];
            while (frames.Count < count)
            {
                var read = await Client.GetStream().ReadAsync(buffer, ct);
                Assert.NotEqual(0, read);
                frames.AddRange(_decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(frame => frame.Value));
            }
            Assert.Equal(count, frames.Count);
            return frames;
        }
        public void Dispose() => Client.Dispose();
    }
}
