using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Wire;

/// <summary>Checks the published automatic endpoint using isolated sockets and independently loaded packet tables.</summary>
internal static class NativeAutomaticSmoke
{
    internal static async Task RunAsync(string repository, string executable)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "src/FantasyTale.Gateway/profiles/bf-center-selector.json")));
        foreach (var adapter in manifest.RootElement.GetProperty("automatic_center_builds").EnumerateObject())
        {
            var release = manifest.RootElement.GetProperty("handoff_release_builds").TryGetProperty(adapter.Name, out var value) ? value : (JsonElement?)null;
            await RunBuildAsync(repository, executable, adapter.Name, adapter.Value.GetString()!, release);
        }
    }

    private static async Task RunBuildAsync(string repository, string executable, string build, string promotionName, JsonElement? release)
    {
        var tables = PacketTables.Load(Path.Combine(repository, "packets"));
        var composer = new ProfileComposer(Path.Combine(repository, "profiles"), _ => null, unsetReadsEmpty: true);
        var rows = composer.Compose(build, []);
        var row = rows.Single(entry => entry.Id == "dialect");
        var client = row.ClientBuild!.Value;
        var profile = new ClientProfile(build, client.Region, client.Major, 0, client.Resource, build);
        var opcodes = OpcodeTableLoader.Load(Path.Combine(repository, "profiles", build, "opcodes.yaml"), profile);
        string Config(string key) => row.Config!.GetValueOrDefault(key)?.ToString() ?? "";
        var dialect = WireDialect.Create(profile, WireStream.Parse(Config("frame"), Config("cipher_table_a"), Config("cipher_table_b")));
        using var gatewayManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "src/FantasyTale.Gateway/profiles/bf-center-selector.json")));
        foreach (var scenario in new[] { "redirect", "clock-skew", "singleton", "manual-unavailable-login", "manual-unavailable-uniqueness", "unavailable-login", "unavailable-uniqueness", "refresh-redirect", "withdraw", "uniqueness", "stop", "release-timeout", "login-before-status", "malformed-login" })
        {
            var manualUnavailable = scenario.StartsWith("manual-unavailable", StringComparison.Ordinal);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ct = deadline.Token;
            var name = "gateway-automatic-smoke-" + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("--pipe");
            start.ArgumentList.Add(name);
            using var process = Process.Start(start) ?? throw new IOException("Gateway process did not start.");
            var diagnostics = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                await ControlAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema_version = 1,
                    rows = new[] { new { id = "selector", plugin = "bf-center-selector", config = new
                    {
                        build,
                        centers = new[]
                        {
                            new { id = "first", name = "First", backend_id = "physical-first", native_center_id = 2, host = "192.0.2.10", port = 7110, unavailable_capacity = (int?)null },
                            new { id = "second", name = "Second", backend_id = "physical-second", native_center_id = 3, host = manualUnavailable ? "target.invalid" : "198.51.100.20", port = 7110, unavailable_capacity = manualUnavailable ? (int?)731 : null },
                        },
                        automatic_entry = new
                        {
                            capability = "bf-native-automatic-center-v1", id = "automatic", name = "Automatic", native_center_id = 9,
                            unavailable_capacity = 731,
                            selection_policy = "lowest-rtt-then-load", redirect_timeout_ms = scenario == "release-timeout" ? 300 : 3000,
                            launch_id = "0123456789abcdef0123456789abcdef", grant_id = "process-fixture-grant",
                            grant_expires_at_utc = DateTimeOffset.UtcNow.AddMinutes(5), admitted_center_ids = new[] { "first", "second" },
                            candidates = new[]
                            {
                                new { center_id = "first", healthy = true, rtt_us = 500, users = 0, capacity = 100, observed_at_utc = DateTimeOffset.UtcNow.AddSeconds(scenario == "clock-skew" ? -34 : 0), status_revision = "first-1", expires_at_utc = DateTimeOffset.UtcNow.AddSeconds(scenario == "clock-skew" ? -19 : 60) },
                                new { center_id = "second", healthy = true, rtt_us = 200, users = 10, capacity = 100, observed_at_utc = DateTimeOffset.UtcNow.AddSeconds(scenario == "clock-skew" ? -34 : 0), status_revision = "second-1", expires_at_utc = DateTimeOffset.UtcNow.AddSeconds(scenario == "clock-skew" ? -19 : 60) },
                            }.Where(candidate => !scenario.StartsWith("unavailable", StringComparison.Ordinal)
                                && (scenario != "singleton" && !manualUnavailable || candidate.center_id == "first")).ToArray(),
                        },
                    } } },
                }));
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, ct);
                var count = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (count is < 1 or > 65536) throw new IOException("Invalid READY size.");
                var body = new byte[count];
                await pipe.ReadExactlyAsync(body, ct);
                using var ready = JsonDocument.Parse(body);
                var endpoint = ready.RootElement.GetProperty("rows")[0];
                using var bootstrap = new TcpClient();
                await bootstrap.ConnectAsync(endpoint.GetProperty("host").GetString()!, endpoint.GetProperty("port").GetInt32(), ct);
                var bootstrapEncryptor = dialect.CreateEncryptor(PacketDirection.ClientToServer);
                var bootstrapDecoder = new WireStreamDecoder(dialect, PacketDirection.ServerToClient, 16384);
                var firstConnect = PacketCodec.Write(tables, build, "gcpFirstConnect", new Dictionary<string, object?> { ["client_version"] = client.Major });
                var protocolRow = rows.Single(entry => entry.Id == "protocol");
                var patchRequest = protocolRow.Config?.GetValueOrDefault("patch_request_name")?.ToString() ?? "gcpFirstConnect";
                await SendAsync(bootstrap, bootstrapEncryptor, opcodes.C2S(patchRequest), firstConnect);
                var directory = await ReadAsync(bootstrap, bootstrapDecoder, 2);
                if (directory[0].Opcode != opcodes.S2C("gspRepGamePatch") || directory[1].Opcode != opcodes.S2C("gspCenterList")
                    || directory[1].Payload.Length != 58 || BinaryPrimitives.ReadUInt32LittleEndian(directory[1].Payload) != 3
                    || !directory[1].Payload.AsSpan(16, 4).SequenceEqual(new byte[] { 127, 0, 0, 1 }))
                    throw new IOException("Automatic directory differs from native field positions.");
                if (gatewayManifest.RootElement.GetProperty("bootstrap_exchange_builds").TryGetProperty(build, out var startupExchanges))
                {
                    foreach (var exchange in startupExchanges.EnumerateArray())
                    {
                        var requestValues = new Dictionary<string, object?> { ["mask"] = 1u, ["checksum_0"] = 0u };
                        var request = PacketCodec.Write(tables, build, exchange.GetProperty("request_layout").GetString()!,
                            requestValues);
                        var replyValues = exchange.GetProperty("reply_values").EnumerateObject()
                            .ToDictionary(item => item.Name, item => (object?)item.Value.GetInt64(), StringComparer.Ordinal);
                        var expectedReply = PacketCodec.Write(tables, build, exchange.GetProperty("reply_layout").GetString()!, replyValues);
                        await SendAsync(bootstrap, bootstrapEncryptor, opcodes.C2S(exchange.GetProperty("request").GetString()!), request);
                        var response = await ReadAsync(bootstrap, bootstrapDecoder, 1);
                        if (response[0].Opcode != opcodes.S2C(exchange.GetProperty("reply").GetString()!)
                            || !response[0].Payload.AsSpan().SequenceEqual(expectedReply))
                            throw new IOException("Native startup response did not preserve the client's configuration.");
                    }
                }
                using var automatic = new TcpClient();
                await automatic.ConnectAsync(IPAddress.Loopback, BinaryPrimitives.ReadUInt16LittleEndian(directory[1].Payload.AsSpan(20, 2)), ct);
                var encryptor = dialect.CreateEncryptor(PacketDirection.ClientToServer);
                var decoder = new WireStreamDecoder(dialect, PacketDirection.ServerToClient, 16384);
                var marker = opcodes.All.SingleOrDefault(entry => entry.Direction == PacketDirection.ClientToServer && entry.Name == "gcpStartGPK");
                if (marker is not null) await SendAsync(automatic, encryptor, marker.Value, []);
                if (scenario != "login-before-status")
                {
                    await SendAsync(automatic, encryptor, opcodes.C2S("gcpReqCenterUserCnt"), []);
                    var status = await ReadAsync(automatic, decoder, 1);
                    var expected = PacketCodec.Write(tables, build, "CenterStatus", new Dictionary<string, object?>
                    {
                        ["center_id"] = 9, ["name"] = "自动选择（First）",
                        ["users"] = scenario.StartsWith("unavailable", StringComparison.Ordinal) ? 1462 : scenario == "singleton" || manualUnavailable ? 0 : 10,
                        ["capacity"] = scenario.StartsWith("unavailable", StringComparison.Ordinal) ? 731 : 100,
                    });
                    if (status[0].Opcode != opcodes.S2C("gspRepCenterUserCnt") || !status[0].Payload.AsSpan().SequenceEqual(expected))
                        throw new IOException("Automatic native status mismatch.");
                }
                if (manualUnavailable)
                {
                    if (!directory[1].Payload.AsSpan(52, 4).SequenceEqual(new byte[] { 127, 0, 0, 1 }))
                        throw new IOException("Failed manual destination was omitted or not projected locally.");
                    using var failed = new TcpClient();
                    await failed.ConnectAsync(IPAddress.Loopback, BinaryPrimitives.ReadUInt16LittleEndian(directory[1].Payload.AsSpan(56, 2)), ct);
                    var failedEncryptor = dialect.CreateEncryptor(PacketDirection.ClientToServer);
                    using var failedDecoder = new WireStreamDecoder(dialect, PacketDirection.ServerToClient, 16384);
                    if (marker is not null) await SendAsync(failed, failedEncryptor, marker.Value, []);
                    await SendAsync(failed, failedEncryptor, opcodes.C2S("gcpReqCenterUserCnt"), []);
                    var failedStatus = await ReadAsync(failed, failedDecoder, 1);
                    var expectedFailed = PacketCodec.Write(tables, build, "CenterStatus", new Dictionary<string, object?>
                    {
                        ["center_id"] = 3, ["name"] = "Second", ["users"] = 1462, ["capacity"] = 731,
                    });
                    if (failedStatus[0].Opcode != opcodes.S2C("gspRepCenterUserCnt") || !failedStatus[0].Payload.AsSpan().SequenceEqual(expectedFailed))
                        throw new IOException("Failed manual destination lost its native identity or unavailable status.");
                    if (scenario == "manual-unavailable-uniqueness")
                    {
                        await SendAsync(failed, failedEncryptor, opcodes.C2S("gcpReqUniquenessCheck"), new byte[4]);
                        var retired = await ReadAsync(failed, failedDecoder, 1);
                        if (retired[0].Opcode != opcodes.S2C("gspRepUniquenessCheck") || !retired[0].Payload.AsSpan().SequenceEqual(new byte[] { 1, 0, 0, 0 }))
                            throw new IOException("Failed manual destination did not retire through uniqueness.");
                    }
                    else
                    {
                        await SendAsync(failed, failedEncryptor, opcodes.C2S("gcpReqLogin"), PacketCodec.Write(tables, build,
                            "PreloginRedirectRequest", new Dictionary<string, object?>
                            {
                                ["credential"] = "private-process-fixture-ticket", ["adapters"] = Array.Empty<IReadOnlyDictionary<string, object?>>(),
                            }));
                        if (await failed.GetStream().ReadAsync(new byte[1], ct) != 0)
                            throw new IOException("Failed manual destination emitted a login response or redirect.");
                    }
                    if (process.HasExited) throw new IOException("Failed manual auxiliary terminated healthy Automatic selection.");
                }
                if (scenario.StartsWith("unavailable", StringComparison.Ordinal))
                {
                    if (scenario == "unavailable-uniqueness")
                    {
                        await SendAsync(automatic, encryptor, opcodes.C2S("gcpReqUniquenessCheck"), new byte[4]);
                        var refused = await ReadAsync(automatic, decoder, 1);
                        if (refused[0].Opcode != opcodes.S2C("gspRepUniquenessCheck")
                            || !refused[0].Payload.AsSpan().SequenceEqual(new byte[] { 1, 0, 0, 0 }))
                            throw new IOException("Unavailable auxiliary did not retire through native uniqueness.");
                        automatic.Dispose();
                    }
                    else
                    {
                        var refusedLogin = PacketCodec.Write(tables, build, "PreloginRedirectRequest", new Dictionary<string, object?>
                        {
                            ["credential"] = "private-process-fixture-ticket", ["adapters"] = Array.Empty<IReadOnlyDictionary<string, object?>>(),
                        });
                        await SendAsync(automatic, encryptor, opcodes.C2S("gcpReqLogin"), refusedLogin);
                        if (await automatic.GetStream().ReadAsync(new byte[1], ct) != 0)
                            throw new IOException("Unavailable auxiliary emitted an unauthorized redirect.");
                    }
                    if (process.HasExited) throw new IOException("Unavailable auxiliary terminated manual selection.");
                    bootstrap.Dispose();
                }
                else if (scenario == "stop")
                {
                    await ControlAsync("{\"type\":\"stop\"}"u8.ToArray());
                }
                else if (scenario == "uniqueness")
                {
                    await SendAsync(automatic, encryptor, opcodes.C2S("gcpReqUniquenessCheck"), new byte[4]);
                    var reply = await ReadAsync(automatic, decoder, 1);
                    if (reply[0].Opcode != opcodes.S2C("gspRepUniquenessCheck") || !reply[0].Payload.AsSpan().SequenceEqual(new byte[] { 1, 0, 0, 0 }))
                        throw new IOException("Native auxiliary cleanup mismatch.");
                    automatic.Dispose();
                    bootstrap.Dispose();
                }
                else
                {
                    if (scenario is "refresh-redirect" or "withdraw")
                        await RefreshAsync(1, scenario == "withdraw", "accepted");
                    var login = PacketCodec.Write(tables, build, "PreloginRedirectRequest", new Dictionary<string, object?>
                    {
                        ["credential"] = "private-process-fixture-ticket", ["adapters"] = Array.Empty<IReadOnlyDictionary<string, object?>>(),
                    });
                    await SendAsync(automatic, encryptor, opcodes.C2S("gcpReqLogin"), scenario == "malformed-login" ? new byte[3] : login);
                    if (scenario is "redirect" or "clock-skew" or "singleton" or "refresh-redirect" or "release-timeout" || manualUnavailable)
                    {
                        var redirect = await ReadAsync(automatic, decoder, 2);
                        var address = scenario is "refresh-redirect" or "singleton" || manualUnavailable ? new byte[] { 192, 0, 2, 10 } : new byte[] { 198, 51, 100, 20 };
                        var expectedMove = PacketCodec.Write(tables, build, "ChannelMoveOffer", new Dictionary<string, object?>
                        {
                            ["result"] = 0, ["ipv4_word"] = BinaryPrimitives.ReadUInt32LittleEndian(address), ["port"] = 7110,
                        });
                        if (redirect[0].Opcode != opcodes.S2C("gspRepMoveChannel") || redirect[1].Opcode != opcodes.S2C(promotionName)
                            || !redirect[0].Payload.AsSpan().SequenceEqual(expectedMove) || redirect[1].Payload.Length != 0)
                            throw new IOException("Native redirect burst mismatch.");
                        if (scenario == "refresh-redirect") await RefreshAsync(2, true, "finished");
                        if (scenario is "redirect" or "clock-skew" or "singleton" or "refresh-redirect" || manualUnavailable)
                        {
                            if (release is { } releaseConfig)
                            {
                                await SendAsync(automatic, encryptor, opcodes.C2S(releaseConfig.GetProperty("request").GetString()!), []);
                                var completed = await ReadAsync(automatic, decoder, 1);
                                if (completed[0].Opcode != opcodes.S2C(releaseConfig.GetProperty("reply").GetString()!) || completed[0].Payload.Length != 0)
                                    throw new IOException("Native source completion mismatch.");
                            }
                            automatic.Dispose();
                        }
                    }
                    else if (scenario == "withdraw")
                    {
                        if (await automatic.GetStream().ReadAsync(new byte[1], ct) != 0 || process.HasExited)
                            throw new IOException("Unavailable automatic Center failed to preserve the manual bootstrap.");
                        await RefreshAsync(2, true, "finished");
                        bootstrap.Dispose();
                    }
                }
                await process.WaitForExitAsync(ct);
                if ((scenario is "redirect" or "clock-skew" or "singleton" or "refresh-redirect" || manualUnavailable)
                    && await bootstrap.GetStream().ReadAsync(new byte[1], ct) != 0)
                    throw new IOException("Completed automatic source release did not retire the bootstrap connection.");
                var log = await diagnostics;
                var expectedExit = scenario is "release-timeout" or "login-before-status" or "malformed-login" ? 1 : 0;
                if (process.ExitCode != expectedExit || log.Contains("private-process-fixture-ticket", StringComparison.Ordinal))
                    throw new IOException("Automatic process exit or credential redaction mismatch: " + log);
                Console.WriteLine(JsonSerializer.Serialize(new { build, automatic = true, scenario, exit_code = process.ExitCode, credential_redacted = true }));
            }
            catch (Exception error)
            {
                pipe.Dispose();
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                throw new IOException("Automatic process scenario failed: " + scenario + "; diagnostics: " + await diagnostics, error);
            }
            finally
            {
                pipe.Dispose();
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }

            async Task ControlAsync(byte[] body)
            {
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
                await pipe.WriteAsync(header, ct);
                await pipe.WriteAsync(body, ct);
            }
            async Task RefreshAsync(long revision, bool empty, string expected)
            {
                await ControlAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "refresh-automatic", row_id = "selector", launch_id = "0123456789abcdef0123456789abcdef",
                    grant_id = "process-fixture-grant", revision,
                    candidates = empty ? [] : new[]
                    {
                        new { center_id = "first", healthy = true, rtt_us = 0, users = 25, capacity = 100,
                            observed_at_utc = DateTimeOffset.UtcNow, status_revision = "first-2", expires_at_utc = DateTimeOffset.UtcNow.AddMinutes(1) },
                    },
                }));
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, ct);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 1 or > 65536) throw new IOException("Invalid refresh acknowledgement size.");
                var body = new byte[length];
                await pipe.ReadExactlyAsync(body, ct);
                using var result = JsonDocument.Parse(body);
                if (result.RootElement.GetProperty("type").GetString() != "automatic-refresh-result"
                    || result.RootElement.GetProperty("row_id").GetString() != "selector"
                    || result.RootElement.GetProperty("revision").GetInt64() != revision
                    || result.RootElement.GetProperty("status").GetString() != expected)
                    throw new IOException("Automatic refresh acknowledgement mismatch.");
            }
            async Task SendAsync(TcpClient socket, IStreamTransform encryptor, ushort opcode, byte[] body)
            {
                var bytes = dialect.EncodeFrame(opcode, body);
                encryptor.Apply(bytes);
                await socket.GetStream().WriteAsync(bytes.AsMemory(0, 2), ct);
                await socket.GetStream().WriteAsync(bytes.AsMemory(2), ct);
            }
            async Task<List<DecodedFrame>> ReadAsync(TcpClient socket, WireStreamDecoder decoder, int expected)
            {
                var result = new List<DecodedFrame>();
                var buffer = new byte[4096];
                while (result.Count < expected)
                {
                    var read = await socket.GetStream().ReadAsync(buffer, ct);
                    if (read == 0) throw new IOException("Gateway closed before the expected native frames.");
                    result.AddRange(decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(frame => frame.Value));
                }
                if (result.Count != expected) throw new IOException("Unexpected extra native response.");
                return result;
            }
        }
    }
}
