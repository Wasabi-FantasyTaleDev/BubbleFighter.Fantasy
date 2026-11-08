using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Wire;

/// <summary>Exercises a published process with independently loaded canonical packet data.</summary>
internal static class NativeSmoke
{
    internal static async Task RunAsync(string repository, string executable)
    {
        var composer = new ProfileComposer(Path.Combine(repository, "profiles"), _ => null, unsetReadsEmpty: true);
        var tables = PacketTables.Load(Path.Combine(repository, "packets"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "src/FantasyTale.Gateway/profiles/bf-center-selector.json")));
        var scenarios = manifest.RootElement.GetProperty("builds").EnumerateArray()
            .Select(identity => (Build: identity.GetString()!, Alias: false, Presentation: false))
            .Concat(manifest.RootElement.GetProperty("selector_identity_builds").EnumerateArray()
                .Select(identity => (Build: identity.GetString()!, Alias: true, Presentation: false)))
            .Concat(manifest.RootElement.GetProperty("selector_identity_builds").EnumerateArray()
                .Select(identity => (Build: identity.GetString()!, Alias: true, Presentation: true)));
        foreach (var scenario in scenarios)
        {
            var build = scenario.Build;
            var rows = composer.Compose(build, []);
            var dialectRow = rows.Single(row => row.Id == "dialect");
            var protocolRow = rows.Single(row => row.Id == "protocol");
            var client = dialectRow.ClientBuild!.Value;
            var profile = new ClientProfile(build, client.Region, client.Major, 0, client.Resource, build);
            string Config(string key) => dialectRow.Config!.GetValueOrDefault(key)?.ToString() ?? "";
            var dialect = WireDialect.Create(profile, WireStream.Parse(Config("frame"), Config("cipher_table_a"), Config("cipher_table_b")));
            var opcodes = OpcodeTableLoader.Load(Path.Combine(repository, "profiles", build, "opcodes.yaml"), profile);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ct = deadline.Token;
            var pipeName = "gateway-native-smoke-" + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("--pipe");
            start.ArgumentList.Add(pipeName);
            var timer = Stopwatch.StartNew();
            using var process = Process.Start(start) ?? throw new IOException("Gateway did not start.");
            var diagnostics = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                var centers = Enumerable.Range(0, scenario.Presentation ? 1 : scenario.Alias ? 6 : 2).Select(index => new
                {
                    id = "choice-" + index,
                    name = "Choice " + index,
                    host = scenario.Alias ? "192.0.2." + (9 + Math.Max(1, index)) : index == 0 ? "192.0.2.10" : "198.51.100.20",
                    port = scenario.Alias || index == 0 ? 7110 : 7210,
                    backend_id = scenario.Alias ? "cloud-" + Math.Max(1, index) : null,
                    native_center_id = scenario.Alias ? (int?)(index + 1) : null,
                }).ToArray();
                var configuration = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema_version = 1,
                    rows = new[] { new { id = "selector", plugin = "bf-center-selector", config = new
                    {
                        build,
                        selector_identity = scenario.Alias ? new
                        {
                            capability = scenario.Presentation ? "bf-native-selector-presentation-v2" : "bf-native-selector-identity-v1",
                            launch_id = "0123456789abcdef0123456789abcdef",
                            presentation = scenario.Presentation ? Enumerable.Range(0, 6).Select(index => new
                            {
                                id = "choice-" + index, name = "Choice " + index, native_center_id = index + 1,
                                binding_id = index == 0 ? "choice-0" : null,
                                unavailable_reason = index == 0 ? null : "offline",
                            }).ToArray() : null,
                        } : null,
                        centers,
                    } } },
                });
                var length = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, configuration.Length);
                await pipe.WriteAsync(length, ct);
                await pipe.WriteAsync(configuration, ct);
                await pipe.ReadExactlyAsync(length, ct);
                var count = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (count is < 1 or > 65536) throw new IOException("Invalid READY size.");
                var response = new byte[count];
                await pipe.ReadExactlyAsync(response, ct);
                using var ready = JsonDocument.Parse(response);
                var readyMs = timer.Elapsed.TotalMilliseconds;
                var endpoint = ready.RootElement.GetProperty("rows")[0];
                var port = endpoint.GetProperty("port").GetInt32();
                if (ready.RootElement.GetProperty("type").GetString() != "ready" || endpoint.GetProperty("mode").GetString() != "bootstrap")
                    throw new IOException("Invalid READY mode.");
                if (scenario.Alias)
                {
                    var mapping = endpoint.GetProperty("selector_identity");
                    if (mapping.GetProperty("capability").GetString() != (scenario.Presentation ? "bf-native-selector-presentation-v2" : "bf-native-selector-identity-v1")
                        || mapping.GetProperty("launch_id").GetString() != "0123456789abcdef0123456789abcdef"
                        || mapping.GetProperty("build").GetString() != build
                        || mapping.GetProperty("entries").GetArrayLength() != centers.Length)
                        throw new IOException("Invalid alias launch binding.");
                    for (var index = 0; index < centers.Length; index++)
                    {
                        var expected = centers[index];
                        var entry = mapping.GetProperty("entries")[index];
                        if (entry.GetProperty("id").GetString() != expected.id
                            || entry.GetProperty("name").GetString() != expected.name
                            || entry.GetProperty("backend_id").GetString() != expected.backend_id
                            || entry.GetProperty("native_center_id").GetInt32() != expected.native_center_id
                            || entry.GetProperty("group").GetInt32() != 0 || entry.GetProperty("sub_key").GetInt32() != index + 1
                            || entry.GetProperty("host").GetString() != expected.host || entry.GetProperty("port").GetInt32() != expected.port)
                            throw new IOException("Invalid ordered native alias mapping.");
                    }
                    if (scenario.Presentation)
                    {
                        var presentation = mapping.GetProperty("presentation");
                        if (presentation.GetArrayLength() != 6) throw new IOException("Missing visible presentation choices.");
                        for (var index = 0; index < 6; index++)
                        {
                            var choice = presentation[index];
                            if (choice.GetProperty("id").GetString() != "choice-" + index
                                || choice.GetProperty("name").GetString() != "Choice " + index
                                || choice.GetProperty("native_center_id").GetInt32() != index + 1
                                || choice.TryGetProperty("host", out _) || choice.TryGetProperty("port", out _)
                                || choice.TryGetProperty("group", out _) || choice.TryGetProperty("sub_key", out _))
                                throw new IOException("Invalid presentation identity or unexpected network field.");
                            if (index == 0
                                ? choice.GetProperty("binding_id").GetString() != "choice-0" || choice.TryGetProperty("unavailable_reason", out _)
                                : choice.GetProperty("unavailable_reason").GetString() != "offline" || choice.TryGetProperty("binding_id", out _))
                                throw new IOException("Invalid presentation availability binding.");
                        }
                    }
                    else if (mapping.TryGetProperty("presentation", out _))
                        throw new IOException("Legacy identity metadata unexpectedly contains presentation rows.");
                }
                else if (endpoint.TryGetProperty("selector_identity", out _))
                    throw new IOException("Legacy READY unexpectedly contains native alias metadata.");
                process.Refresh();
                var workingSet = process.WorkingSet64;
                var privateBytes = process.PrivateMemorySize64;
                if (process.MainWindowHandle != IntPtr.Zero) throw new IOException("Gateway has a visible window.");
                using var game = new TcpClient();
                await game.ConnectAsync("127.0.0.1", port, ct);
                var stream = game.GetStream();
                var preamble = new byte[dialect.PreambleLength(PacketDirection.ServerToClient)];
                await stream.ReadExactlyAsync(preamble, ct);
                string ProtocolConfig(string key) => protocolRow.Config?.GetValueOrDefault(key)?.ToString() ?? "";
                var status = ushort.TryParse(ProtocolConfig("bootstrap_status"), out var configuredStatus) ? configuredStatus : (ushort)2;
                var commandLine = ProtocolConfig("bootstrap_command_line");
                if (!preamble.AsSpan().SequenceEqual(dialect.BuildServerPreamble(status, commandLine)))
                    throw new IOException("Unexpected native greeting.");
                var decoder = new WireStreamDecoder(dialect, PacketDirection.ServerToClient, 16384);
                decoder.Feed(preamble);
                var patchValues = new Dictionary<string, object?> { ["client_version"] = client.Major };
                if (scenario.Alias)
                {
                    var strings = tables.Resolve(build, "gcpFirstConnect").Where(field => field.Type == "wstr").ToArray();
                    patchValues[strings[0].Name] = "Gateway \u4e00";
                    patchValues[strings[^1].Name] = "Tail";
                }
                var patch = PacketCodec.Write(tables, build, "gcpFirstConnect", patchValues);
                if (patch.Length != (scenario.Alias ? 122 : 96)) throw new IOException("Unexpected canonical handshake length.");
                var patchRequestName = ProtocolConfig("patch_request_name");
                if (string.IsNullOrEmpty(patchRequestName)) patchRequestName = "gcpFirstConnect";
                var packet = new List<byte>();
                var startupReplies = new List<(ushort Opcode, byte[] Payload)>();
                if (protocolRow.Config?.GetValueOrDefault("pre_handshake_opcodes") is IEnumerable preHandshake)
                {
                    foreach (var value in preHandshake)
                    {
                        var opcode = opcodes.C2S(value.ToString()!);
                        byte[] body = [];
                        if (manifest.RootElement.GetProperty("bootstrap_exchange_builds").TryGetProperty(build, out var exchanges))
                        {
                            foreach (var exchange in exchanges.EnumerateArray())
                            {
                                if (opcodes.C2S(exchange.GetProperty("request").GetString()!) != opcode) continue;
                                body = PacketCodec.Write(tables, build, exchange.GetProperty("request_layout").GetString()!,
                                    new Dictionary<string, object?> { ["mask"] = 1u, ["checksum_0"] = 0u });
                                var values = exchange.GetProperty("reply_values").EnumerateObject()
                                    .ToDictionary(item => item.Name, item => (object?)item.Value.GetInt64(), StringComparer.Ordinal);
                                startupReplies.Add((opcodes.S2C(exchange.GetProperty("reply").GetString()!),
                                    PacketCodec.Write(tables, build, exchange.GetProperty("reply_layout").GetString()!, values)));
                            }
                        }
                        packet.AddRange(dialect.EncodeFrame(opcode, body));
                    }
                }
                packet.AddRange(dialect.EncodeFrame(opcodes.C2S(patchRequestName), patch));
                var patchReply = PacketCodec.Write(tables, build, "gspRepGamePatch",
                    new Dictionary<string, object?>());
                var modernDirectory = tables.Resolve(build, "GateInfo").Any(field => field.Name == "unknown_4");
                var gates = centers.Select((center, index) =>
                {
                    var address = BinaryPrimitives.ReadUInt32LittleEndian(System.Net.IPAddress.Parse(center.host).GetAddressBytes());
                    return modernDirectory
                        ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                        {
                            ["unknown_4"] = 0, ["unknown_8"] = index + 1, ["unknown_12"] = 0,
                            ["unknown_16"] = address, ["unknown_20"] = center.port,
                        }
                        : new Dictionary<string, object?>
                        {
                            ["key"] = 0, ["sub_key"] = index + 1, ["unknown_12"] = 0,
                            ["address"] = address, ["port"] = center.port,
                        };
                }).ToArray();
                var directory = PacketCodec.Write(tables, build, "gspCenterList", new Dictionary<string, object?> { ["gates"] = gates });
                if (!modernDirectory && Convert.ToHexString(directory) != (scenario.Presentation
                    ? "01000000000000000100000000000000C000020AC61B"
                    : scenario.Alias
                    ? "06000000" + "000000000100000000000000C000020AC61B" + "000000000200000000000000C000020AC61B"
                        + "000000000300000000000000C000020BC61B" + "000000000400000000000000C000020CC61B"
                        + "000000000500000000000000C000020DC61B" + "000000000600000000000000C000020EC61B"
                    : "02000000000000000100000000000000C000020AC61B000000000200000000000000C63364142A1C"))
                    throw new IOException("Legacy directory differs from the pinned wire bytes.");
                var encryptor = dialect.CreateEncryptor(PacketDirection.ClientToServer);
                var packetBytes = packet.ToArray();
                encryptor.Apply(packetBytes);
                var handshake = Stopwatch.StartNew();
                await stream.WriteAsync(packetBytes.AsMemory(0, 2), ct);
                await stream.WriteAsync(packetBytes.AsMemory(2), ct);
                var received = new List<DecodedFrame>();
                var buffer = new byte[4096];
                while (received.Count < startupReplies.Count + 2)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) throw new IOException("Gateway closed before its directory.");
                    received.AddRange(decoder.Feed(buffer.AsSpan(0, read)).OfType<WireStreamItem.Frame>().Select(frame => frame.Value));
                }
                var responseMs = handshake.Elapsed.TotalMilliseconds;
                for (var index = 0; index < startupReplies.Count; index++)
                    if (received[index].Opcode != startupReplies[index].Opcode
                        || !received[index].Payload.AsSpan().SequenceEqual(startupReplies[index].Payload))
                        throw new IOException("Native pre-handshake reply differs from its canonical configuration-preserving payload.");
                if (received.Count != startupReplies.Count + 2 || received[^2].Opcode != opcodes.S2C("gspRepGamePatch") || received[^1].Opcode != opcodes.S2C("gspCenterList")
                    || !received[^2].Payload.AsSpan().SequenceEqual(patchReply)
                    || !received[^1].Payload.AsSpan().SequenceEqual(directory))
                    throw new IOException("Native encrypted reply differs from the pinned directory.");
                var postLoad = new List<byte>();
                if (opcodes.Find(PacketDirection.ClientToServer, "gcpStartGPK") is { } marker)
                    postLoad.AddRange(dialect.EncodeFrame(marker, []));
                var auxiliaryRequests = 0;
                if (manifest.RootElement.GetProperty("bootstrap_observation_builds").TryGetProperty(build, out var observations))
                {
                    foreach (var observation in observations.EnumerateArray())
                    {
                        var name = observation.GetString()!;
                        var body = PacketCodec.Write(tables, build, name, new Dictionary<string, object?>());
                        for (var repeat = 0; repeat < 3; repeat++)
                        {
                            postLoad.AddRange(dialect.EncodeFrame(opcodes.C2S(name), body));
                            auxiliaryRequests++;
                        }
                    }
                }
                var keepAlives = 0;
                if (opcodes.Find(PacketDirection.ClientToServer, "gcpReqKeepAlivePing") is { } ping)
                {
                    var body = PacketCodec.Write(tables, build, "gcpReqKeepAlivePing", new Dictionary<string, object?>());
                    if (body.Length != 0) throw new IOException("Native keep-alive must be empty.");
                    for (; keepAlives < 3; keepAlives++) postLoad.AddRange(dialect.EncodeFrame(ping, body));
                }
                if (postLoad.Count != 0)
                {
                    var bytes = postLoad.ToArray();
                    encryptor.Apply(bytes);
                    await stream.WriteAsync(bytes.AsMemory(0, 3), ct);
                    await stream.WriteAsync(bytes.AsMemory(3), ct);
                }
                if (scenario.Presentation)
                {
                    var stop = "{\"type\":\"stop\"}"u8.ToArray();
                    BinaryPrimitives.WriteInt32LittleEndian(length, stop.Length);
                    await pipe.WriteAsync(length, ct);
                    await pipe.WriteAsync(stop, ct);
                }
                else game.Client.Shutdown(SocketShutdown.Send);
                if (await stream.ReadAsync(buffer, ct) != 0) throw new IOException("Unexpected bootstrap response after the directory.");
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) throw new IOException(await diagnostics);
                Console.WriteLine(JsonSerializer.Serialize(new { build, alias = scenario.Alias, presentation = scenario.Presentation,
                    selector_count = centers.Length, visible_count = scenario.Presentation ? 6 : centers.Length,
                    bootstrap_open_on_stop = scenario.Presentation, ready_ms = Math.Round(readyMs, 2), response_ms = Math.Round(responseMs, 3),
                    working_set_bytes = workingSet, private_bytes = privateBytes, keep_alives = keepAlives,
                    auxiliary_requests = auxiliaryRequests, exit_code = process.ExitCode }));
            }
            finally
            {
                pipe.Dispose();
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
    }
}
