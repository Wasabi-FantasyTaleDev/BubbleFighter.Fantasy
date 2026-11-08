using System.IO.Pipes;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Exercises encrypted bootstrap exchanges and parent-owned socket and task lifetimes.</summary>
public sealed class GatewayLifecycleTests
{
    /// <summary>The KR2026 bootstrap sends the status accepted by kr-1256 sub_99EAD0 before any encrypted frames.</summary>
    [Fact]
    public async Task Korean_modern_bootstrap_sends_native_accepted_greeting_word()
    {
        await using var session = await Session.StartAsync(GatewayConfigurationTests.Options("kr-1256"));
        using var game = await session.ConnectGameAsync();
        var greeting = new byte[258];
        await game.GetStream().ReadExactlyAsync(greeting, session.Token);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));
        Assert.All(greeting.Skip(2), value => Assert.Equal(0, value));
        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" },
            GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>One live destination keeps the chooser active while five disabled rows stay out of its connection directory.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-706")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-735")]
    public async Task Presentation_only_unavailable_rows_are_absent_from_encrypted_native_directory(string build)
    {
        var options = SelectorPresentationTests.Options(build);
        await using var session = await Session.StartAsync(options);
        Assert.Equal("bootstrap", session.Ready.Rows[0].Mode);
        var mapping = Assert.IsType<SelectorIdentityMapping>(session.Ready.Rows[0].SelectorIdentity);
        Assert.Equal(SelectorIdentityOptions.PresentationCapability, mapping.Capability);
        Assert.Equal(options.SelectorIdentity!.LaunchId, mapping.LaunchId);
        Assert.Equal(build, mapping.Build);
        Assert.Single(mapping.Entries);
        Assert.Equal(6, mapping.Presentation!.Length);
        Assert.Equal(options.Centers[0].Id, mapping.Presentation[0].BindingId);
        Assert.All(mapping.Presentation.Skip(1), row => Assert.Null(row.BindingId));
        using var game = await session.ConnectGameAsync();
        var profile = options.Validate();
        var request = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(request);
        await game.GetStream().WriteAsync(request, session.Token);
        var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ServerToClient, 16384);
        var frames = new List<DecodedFrame>();
        var buffer = new byte[4096];
        while (frames.Count < 2)
        {
            var read = await game.GetStream().ReadAsync(buffer, session.Token);
            Assert.NotEqual(0, read);
            frames.AddRange(decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(item => item.Value));
        }
        Assert.Equal(new[] { profile.PatchReply, profile.CenterList }, frames.Select(frame => frame.Opcode));
        Assert.Equal(22, frames[1].Payload.Length);
        Assert.Equal(BfCenterSelectorPlugin.EncodeDirectory(profile, options.Centers), frames[1].Payload);
        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" },
            GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>Six explicit choices retain ordered alias bindings through the encrypted native bootstrap.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-706")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-735")]
    public async Task Six_native_choices_preserve_same_backend_aliases_in_ready_and_wire(string build)
    {
        var options = new CenterSelectorOptions
        {
            Build = build, SelectorIdentity = SelectorIdentityTests.Options(build).SelectorIdentity,
            Centers = Enumerable.Range(0, 6).Select(i => new CenterEntry
            {
                Id = "choice-" + i, Name = "Choice " + i, NativeCenterId = i + 1,
                BackendId = "backend-" + Math.Max(1, i), Host = "192.0.2." + Math.Max(1, i), Port = 7140,
            }).ToArray(),
        };
        await using var session = await Session.StartAsync(options);
        var mapping = Assert.IsType<SelectorIdentityMapping>(session.Ready.Rows[0].SelectorIdentity);
        Assert.Equal(6, mapping.Entries.Length);
        Assert.Equal(options.SelectorIdentity!.LaunchId, mapping.LaunchId);
        Assert.Equal(options.Build, mapping.Build);
        using var game = await session.ConnectGameAsync();
        var profile = options.Validate();
        var patch = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(patch);
        await game.GetStream().WriteAsync(patch, session.Token);
        var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ServerToClient, 16384);
        var frames = new List<DecodedFrame>();
        var buffer = new byte[4096];
        while (frames.Count < 2)
        {
            var read = await game.GetStream().ReadAsync(buffer, session.Token);
            Assert.NotEqual(0, read);
            frames.AddRange(decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(item => item.Value));
        }
        Assert.Equal(112, frames[1].Payload.Length);
        Assert.Equal(BfCenterSelectorPlugin.CreateBindings(profile, options.Centers), mapping.Entries);
        Assert.Equal(BfCenterSelectorPlugin.EncodeDirectory(profile, options.Centers), frames[1].Payload);
        Assert.Equal(mapping.Entries[0].Host, mapping.Entries[1].Host);
        Assert.NotEqual(mapping.Entries[0].SubKey, mapping.Entries[1].SubKey);
        game.Dispose();
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>Experimental modern gate records retain distinct keys and the configured endpoint bytes.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void Experimental_modern_directory_projects_keys_and_endpoints_into_unknown_fields(string build)
    {
        var profile = CompiledBuildCatalog.Resolve(build);
        Assert.True(profile.ExperimentalDirectory);
        var centers = GatewayConfigurationTests.Options(build).Centers;

        var body = BfCenterSelectorPlugin.EncodeDirectory(profile, centers);

        Assert.Equal(40, body.Length);
        Assert.Equal(profile.Group, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4, 4)));
        Assert.Equal(profile.ReservedCenter + 1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8, 4)));
        Assert.Equal(profile.Group, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(22, 4)));
        Assert.Equal(profile.ReservedCenter + 2, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(26, 4)));
        Assert.Equal(IPAddress.Parse(centers[0].Host).GetAddressBytes(), body.AsSpan(16, 4).ToArray());
        Assert.Equal(centers[0].Port, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20, 2)));
        Assert.Equal(IPAddress.Parse(centers[1].Host).GetAddressBytes(), body.AsSpan(34, 4).ToArray());
        Assert.Equal(centers[1].Port, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(38, 2)));
    }

    /// <summary>Fragmented native requests receive the directory before bootstrap release completes the row.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-404")]
    [InlineData("cn-406")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("cn-774")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("tw-454")]
    [InlineData("kr-1256")]
    [InlineData("cn-408")]
    [InlineData("cn-735")]
    public async Task Fragmented_encrypted_bootstrap_delivers_only_real_targets_and_exits_on_release(string build)
    {
        await using var session = await Session.StartAsync(GatewayConfigurationTests.Options(build));
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve(build);
        var encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer);
        var greeting = new byte[profile.Dialect.PreambleLength(PacketDirection.ServerToClient)];
        await game.GetStream().ReadExactlyAsync(greeting, session.Token);
        Assert.Equal(profile.Dialect.BuildServerPreamble(profile.GreetingStatus, profile.BootstrapCommandLine), greeting);
        var patch = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        var bytes = profile.Marker is { } marker ? profile.Dialect.EncodeFrame(marker, []).Concat(patch).ToArray() : patch;
        encryptor.Apply(bytes);
        await game.GetStream().WriteAsync(bytes.AsMemory(0, 2), session.Token);
        await game.GetStream().WriteAsync(bytes.AsMemory(2), session.Token);
        var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ServerToClient, 16384);
        decoder.Feed(greeting);
        var frames = new List<DecodedFrame>();
        var buffer = new byte[4096];
        while (frames.Count < 2)
        {
            var read = await game.GetStream().ReadAsync(buffer, session.Token);
            Assert.NotEqual(0, read);
            frames.AddRange(decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(item => item.Value));
        }
        Assert.Equal(new[] { profile.PatchReply, profile.CenterList }, frames.Select(item => item.Opcode));
        Assert.Equal(profile.PatchResponse.Write(new Dictionary<string, object?>()), frames[0].Payload);
        Assert.Equal(40, frames[1].Payload.Length);
        Assert.Equal(BfCenterSelectorPlugin.EncodeDirectory(profile, GatewayConfigurationTests.Options(build).Centers), frames[1].Payload);
        Assert.False(session.Work.IsCompleted);
        game.Dispose();
        await session.Work.WaitAsync(session.Token);
    }

    /// <summary>Known profile pre-handshake frames are consumed before first-connect without shifting cipher state.</summary>
    [Theory]
    [InlineData("cn-717", 19, 0)]
    [InlineData("cn-774", 19, 0)]
    [InlineData("tw-454", 19, 0)]
    public async Task Configured_pre_handshake_frames_are_accepted_before_the_native_directory(string build, ushort opcode, int payloadLength)
    {
        await using var session = await Session.StartAsync(GatewayConfigurationTests.Options(build));
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve(build);
        var preHandshake = profile.Dialect.EncodeFrame(opcode, new byte[payloadLength]);
        var firstConnect = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        var bytes = preHandshake.Concat(firstConnect).ToArray();
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(bytes);
        await game.GetStream().WriteAsync(bytes, session.Token);

        var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ServerToClient, 16384);
        var frames = new List<DecodedFrame>();
        var buffer = new byte[4096];
        while (frames.Count < 2)
        {
            var read = await game.GetStream().ReadAsync(buffer, session.Token);
            Assert.NotEqual(0, read);
            frames.AddRange(decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(item => item.Value));
        }

        Assert.Equal(new[] { profile.PatchReply, profile.CenterList }, frames.Select(frame => frame.Opcode));
    }

    /// <summary>Native post-load markers and repeated keep-alives leave the directory connection open without replies.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encrypted_keep_alives_preserve_selection_until_client_release(bool markerFirst)
    {
        await using var session = await Session.StartAsync(GatewayConfigurationTests.Options("cn-324"));
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve("cn-324");
        var stream = game.GetStream();
        var encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer);
        var patch = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        encryptor.Apply(patch);
        await stream.WriteAsync(patch, session.Token);
        var response = new byte[profile.Dialect.PreambleLength(PacketDirection.ServerToClient) +
            profile.Dialect.EncodeFrame(profile.PatchReply, new byte[8]).Length +
            profile.Dialect.EncodeFrame(profile.CenterList, new byte[40]).Length];
        await stream.ReadExactlyAsync(response, session.Token);

        var marker = profile.Dialect.EncodeFrame(profile.Marker!.Value, []);
        var ping = profile.Dialect.EncodeFrame(profile.KeepAlivePing!.Value, []);
        var repeats = Enumerable.Range(0, 32).SelectMany(_ => ping);
        var postLoad = markerFirst ? marker.Concat(repeats).ToArray() : repeats.Concat(marker).ToArray();
        encryptor.Apply(postLoad);
        await stream.WriteAsync(postLoad.AsMemory(0, 3), session.Token);
        await stream.WriteAsync(postLoad.AsMemory(3), session.Token);
        game.Client.Shutdown(SocketShutdown.Send);

        Assert.Equal(0, await stream.ReadAsync(new byte[1], session.Token));
        await session.Work.WaitAsync(session.Token);
        var log = session.Log.ToString();
        Assert.Single(log.Split('\n'), line => line.Contains("native keep-alive accepted.", StringComparison.Ordinal));
        Assert.Contains("native marker accepted.", log);
        Assert.Contains("bootstrap connection released.", log);
    }

    /// <summary>A ping must be empty, follow first-connect, and belong to the selected client's audited opcode table.</summary>
    [Theory]
    [InlineData("cn-324", false, 0)]
    [InlineData("cn-324", true, 1)]
    [InlineData("kr-812", true, 0)]
    [InlineData("cn-279", true, 0)]
    public async Task Invalid_or_unsupported_keep_alive_is_rejected(string build, bool sendPatch, int payloadLength)
    {
        await using var session = await Session.StartAsync(GatewayConfigurationTests.Options(build));
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve(build);
        var ping = profile.Dialect.EncodeFrame(117, new byte[payloadLength]);
        var bytes = sendPatch
            ? profile.Dialect.EncodeFrame(profile.PatchRequest,
                profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major })).Concat(ping).ToArray()
            : ping;
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(bytes);
        await game.GetStream().WriteAsync(bytes, session.Token);
        await Assert.ThrowsAsync<PacketFormatException>(() => session.Work.WaitAsync(session.Token));
        Assert.Contains($"opcode=117, payload_bytes={payloadLength}", session.Log.ToString());
    }

    /// <summary>Frequent valid keep-alives cannot keep the local bootstrap process alive past its selection limit.</summary>
    [Fact]
    public async Task Repeated_keep_alives_do_not_extend_the_selection_deadline()
    {
        var options = new CenterSelectorOptions
        {
            Build = "cn-324", Centers = GatewayConfigurationTests.Options().Centers,
            HandshakeTimeoutMs = 500, SelectionTimeoutMs = 1000,
        };
        await using var session = await Session.StartAsync(options);
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve(options.Build);
        var encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer);
        var patch = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        encryptor.Apply(patch);
        await game.GetStream().WriteAsync(patch, session.Token);
        using var pumping = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        var sender = SendPingsAsync();
        try
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => session.Work.WaitAsync(session.Token));
            Assert.Equal("The game's selection deadline expired.", error.Message);
            Assert.Contains("native keep-alive accepted.", session.Log.ToString());
        }
        finally
        {
            await pumping.CancelAsync();
            await sender;
        }

        async Task SendPingsAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
                while (await timer.WaitForNextTickAsync(pumping.Token))
                {
                    var ping = profile.Dialect.EncodeFrame(profile.KeepAlivePing!.Value, []);
                    encryptor.Apply(ping);
                    await game.GetStream().WriteAsync(ping, pumping.Token);
                }
            }
            catch (OperationCanceledException) when (pumping.IsCancellationRequested) { }
            catch (IOException) when (session.Work.IsCompleted) { }
        }
    }

    /// <summary>A parent-owned selector remains alive beyond the legacy deadline and stops with its parent.</summary>
    [Fact]
    public async Task Parent_owned_selection_lifetime_waits_for_parent_stop()
    {
        var options = new CenterSelectorOptions
        {
            Build = "kr-812",
            Centers = GatewayConfigurationTests.Options().Centers,
            HandshakeTimeoutMs = 500,
            SelectionTimeoutMs = 0,
        };
        await using var session = await Session.StartAsync(options);
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve(options.Build);
        var request = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(request);
        var stream = game.GetStream();
        await stream.WriteAsync(request, session.Token);
        var responseLength = profile.Dialect.PreambleLength(PacketDirection.ServerToClient)
            + profile.Dialect.EncodeFrame(profile.PatchReply, profile.PatchResponse.Write(new Dictionary<string, object?>())).Length
            + profile.Dialect.EncodeFrame(profile.CenterList, BfCenterSelectorPlugin.EncodeDirectory(profile, options.Centers)).Length;
        await stream.ReadExactlyAsync(new byte[responseLength], session.Token);

        await Task.Delay(1200, session.Token);
        Assert.False(session.Work.IsCompleted);

        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" },
            GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], session.Token));
        Assert.Contains("Fantasy Tale Gateway stopped.", session.Log.ToString());
    }

    /// <summary>STOP and pipe EOF close the listener both before and after game connection acceptance.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stop_and_parent_eof_release_waiting_and_connected_sockets(bool connect, bool eof)
    {
        await using var session = await Session.StartAsync(GatewayConfigurationTests.Options());
        using var game = connect ? await session.ConnectGameAsync() : null;
        if (eof) session.Parent.Dispose();
        else await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" }, GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
        using var probe = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync("127.0.0.1", session.Ready.Rows[0].Port));
    }

    /// <summary>Parent-confirmed selection closes a retained bootstrap while the direct target stays usable.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-706")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-735")]
    public async Task Stop_after_directory_releases_bootstrap_without_closing_direct_target(string build)
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start(1);
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var options = new CenterSelectorOptions
        {
            Build = build,
            Centers =
            [
                new CenterEntry { Id = "selected", Name = "Selected", Host = "127.0.0.1", Port = targetPort },
                new CenterEntry { Id = "other", Name = "Other", Host = "192.0.2.2", Port = targetPort },
            ],
        };
        await using var session = await Session.StartAsync(options);
        using var bootstrap = await session.ConnectGameAsync();
        var profile = options.Validate();
        var request = profile.Dialect.EncodeFrame(profile.PatchRequest,
            profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(request);
        var bootstrapStream = bootstrap.GetStream();
        await bootstrapStream.WriteAsync(request, session.Token);
        var responseLength = profile.Dialect.PreambleLength(PacketDirection.ServerToClient) +
            profile.Dialect.EncodeFrame(profile.PatchReply, profile.PatchResponse.Write(new Dictionary<string, object?>())).Length +
            profile.Dialect.EncodeFrame(profile.CenterList, BfCenterSelectorPlugin.EncodeDirectory(profile, options.Centers)).Length;
        await bootstrapStream.ReadExactlyAsync(new byte[responseLength], session.Token);

        using var directGame = new TcpClient();
        await directGame.ConnectAsync(IPAddress.Loopback, targetPort, session.Token);
        using var acceptedTarget = await target.AcceptTcpClientAsync(session.Token);
        await AssertDirectRoundTripAsync(0x31);
        Assert.False(session.Work.IsCompleted);

        await ControlMessageStream.WriteJsonAsync(session.Parent, new GatewayCommand { Type = "stop" },
            GatewayJsonContext.Strict.GatewayCommand, session.Token);
        await session.Work.WaitAsync(session.Token);
        Assert.Equal(0, await bootstrapStream.ReadAsync(new byte[1], session.Token));
        using var probe = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync("127.0.0.1", session.Ready.Rows[0].Port));
        await AssertDirectRoundTripAsync(0x72);
        Assert.Contains("Fantasy Tale Gateway stopped.", session.Log.ToString());

        async Task AssertDirectRoundTripAsync(byte value)
        {
            await directGame.GetStream().WriteAsync(new byte[] { value }, session.Token);
            var received = new byte[1];
            await acceptedTarget.GetStream().ReadExactlyAsync(received, session.Token);
            Assert.Equal(value, received[0]);
            await acceptedTarget.GetStream().WriteAsync(received, session.Token);
            await directGame.GetStream().ReadExactlyAsync(received, session.Token);
            Assert.Equal(value, received[0]);
        }
    }

    /// <summary>A timed-out capability terminates the host even while its parent pipe remains open.</summary>
    [Fact]
    public async Task Capability_failure_wakes_host_with_parent_pipe_still_open()
    {
        var options = new CenterSelectorOptions { Build = "kr-812", Centers = GatewayConfigurationTests.Options().Centers, ConnectTimeoutMs = 300 };
        await using var session = await Session.StartAsync(options);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => session.Work.WaitAsync(session.Token));
        Assert.Equal("The game's connect deadline expired.", error.Message);
    }

    /// <summary>Accepted clients report whether the first request or the later selection deadline expired.</summary>
    [Theory]
    [InlineData(false, "handshake")]
    [InlineData(true, "selection")]
    public async Task Accepted_connection_timeout_identifies_the_incomplete_phase(bool sendPatch, string phase)
    {
        var options = new CenterSelectorOptions
        {
            Build = "cn-324",
            Centers = GatewayConfigurationTests.Options().Centers,
            HandshakeTimeoutMs = 500,
            SelectionTimeoutMs = 1500,
        };
        await using var session = await Session.StartAsync(options);
        using var game = await session.ConnectGameAsync();
        if (sendPatch)
        {
            var profile = CompiledBuildCatalog.Resolve(options.Build);
            var bytes = profile.Dialect.EncodeFrame(profile.PatchRequest,
                profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
            profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(bytes);
            await game.GetStream().WriteAsync(bytes, session.Token);
        }
        var error = await Assert.ThrowsAsync<TimeoutException>(() => session.Work.WaitAsync(session.Token));
        Assert.Equal($"The game's {phase} deadline expired.", error.Message);
        Assert.Contains("bootstrap accepted.", session.Log.ToString());
        Assert.Contains("native greeting sent.", session.Log.ToString());
        Assert.Equal(sendPatch, session.Log.ToString().Contains("native directory sent.", StringComparison.Ordinal));
    }

    /// <summary>An unrecognized bootstrap frame fails instead of being treated as a login request.</summary>
    [Fact]
    public async Task Unknown_opcode_is_fatal_and_is_not_an_optimistic_login()
    {
        await using var session = await Session.StartAsync(GatewayConfigurationTests.Options());
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve("kr-812");
        var bytes = profile.Dialect.EncodeFrame(ushort.MaxValue, "private-identity-material"u8.ToArray());
        profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer).Apply(bytes);
        await game.GetStream().WriteAsync(bytes, session.Token);
        await Assert.ThrowsAsync<PacketFormatException>(() => session.Work.WaitAsync(session.Token));
        var log = session.Log.ToString();
        Assert.Contains("phase=handshake, opcode=65535, payload_bytes=25", log);
        Assert.DoesNotContain("private-identity-material", log);
    }

    /// <summary>Malformed encrypted headers identify the failed phase without exposing received bytes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_modern_header_reports_phase_without_logging_received_material(bool afterDirectory)
    {
        await using var session = await Session.StartAsync(GatewayConfigurationTests.Options("kr-1256"));
        using var game = await session.ConnectGameAsync();
        var profile = CompiledBuildCatalog.Resolve("kr-1256");
        var encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer);
        if (afterDirectory)
        {
            var first = profile.Dialect.EncodeFrame(profile.PatchRequest,
                profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major }));
            encryptor.Apply(first);
            await game.GetStream().WriteAsync(first, session.Token);
            var length = profile.Dialect.PreambleLength(PacketDirection.ServerToClient)
                + profile.Dialect.EncodeFrame(profile.PatchReply, profile.PatchResponse.Write(new Dictionary<string, object?>())).Length
                + profile.Dialect.EncodeFrame(profile.CenterList, BfCenterSelectorPlugin.EncodeDirectory(profile,
                    GatewayConfigurationTests.Options("kr-1256").Centers)).Length;
            await game.GetStream().ReadExactlyAsync(new byte[length], session.Token);
        }
        var invalid = profile.Dialect.EncodeFrame(ushort.MaxValue, "private-diagnostic-material"u8);
        invalid[7] ^= 1;
        encryptor.Apply(invalid);
        await game.GetStream().WriteAsync(invalid, session.Token);
        await Assert.ThrowsAsync<PacketFormatException>(() => session.Work.WaitAsync(session.Token));
        var log = session.Log.ToString();
        Assert.Contains($"bootstrap failure; phase={(afterDirectory ? "selection" : "handshake")}, kind=PacketFormatException.", log);
        Assert.DoesNotContain("private-diagnostic-material", log);
    }

    /// <summary>A one-target launch returns its real endpoint and finishes without a listener.</summary>
    [Fact]
    public async Task Single_target_returns_direct_endpoint_and_leaves_no_resident_capability()
    {
        var options = new CenterSelectorOptions { Build = "cn-279", Centers = [GatewayConfigurationTests.Options().Centers[0]] };
        await using var session = await Session.StartAsync(options);
        Assert.Equal("direct", session.Ready.Rows[0].Mode);
        Assert.Equal(options.Centers[0].Host, session.Ready.Rows[0].Host);
        Assert.Equal(options.Centers[0].Port, session.Ready.Rows[0].Port);
        await session.Work.WaitAsync(session.Token);
    }

    internal sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
        private readonly NamedPipeClientStream _child;
        internal NamedPipeServerStream Parent { get; }
        internal Task Work { get; private set; } = Task.CompletedTask;
        internal GatewayReady Ready { get; private set; } = null!;
        internal StringWriter Log { get; } = new();
        internal CancellationToken Token => _timeout.Token;

        private Session()
        {
            var name = "gateway-test-" + Guid.NewGuid().ToString("N");
            Parent = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _child = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        internal static async Task<Session> StartAsync(CenterSelectorOptions options)
        {
            var session = new Session();
            try
            {
                await Task.WhenAll(session.Parent.WaitForConnectionAsync(session.Token), session._child.ConnectAsync(session.Token));
                session.Work = GatewayHost.RunAsync(session._child, new GatewayLog(session.Log), session.Token);
                var config = new GatewayConfiguration
                {
                    SchemaVersion = 1,
                    Rows = [new GatewayRow { Id = "selector", Plugin = "bf-center-selector", Config = JsonSerializer.SerializeToElement(options, GatewayJsonContext.Strict.CenterSelectorOptions) }],
                };
                await ControlMessageStream.WriteJsonAsync(session.Parent, config, GatewayJsonContext.Strict.GatewayConfiguration, session.Token);
                var body = await ControlMessageStream.ReadAsync(session.Parent, session.Token);
                session.Ready = JsonSerializer.Deserialize(body!, GatewayJsonContext.Strict.GatewayReady)!;
                return session;
            }
            catch { await session.DisposeAsync(); throw; }
        }

        internal async Task<TcpClient> ConnectGameAsync()
        {
            var game = new TcpClient();
            await game.ConnectAsync(Ready.Rows[0].Host, Ready.Rows[0].Port, Token);
            return game;
        }

        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel();
            Parent.Dispose();
            try { await Work.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or PacketFormatException or IOException) { }
            _child.Dispose();
            _timeout.Dispose();
        }
    }
}
