using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Diagnostics;

/// <summary>
/// Diagnostics plugin that logs one line per packet and per connection event. Packets
/// without a registered handler are logged at <see cref="LogLevel.Warning"/> with a hex
/// preview of the payload head. An optional bounded rolling file sink mirrors the lines to
/// disk (rotating to <c>.1</c> when <see cref="PacketLogOptions.MaxFileBytes"/> is
/// exceeded).
/// </summary>
[Plugin("diagnostics.packet-log", Description = "One-line-per-packet diagnostics logging.",
    Optional = [typeof(IOpcodeTableRegistry)])]
public sealed class PacketLogPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<PacketLogOptions>();
        if (!options.ConsolePackets && string.IsNullOrWhiteSpace(options.File))
            throw new ConfigException("Packet logging requires a file sink when console_packets is disabled.");
        var previewHandled = options.PreviewHandledOpcodes.ToHashSet();
        var tables = context.Services.TryGet<IOpcodeTableRegistry>(out var registry) ? registry : null;
        var catalog = PacketCatalogNames.Load(options.CatalogDirectory, options.MaxCatalogEntries, options.MaxCatalogFileBytes);
        string Describe(string protocol, PacketDirection direction, ushort opcode)
        {
            var table = tables is not null && tables.TryForProtocol(protocol, out var found) ? found : null;
            return catalog.Describe(protocol, direction, opcode, table);
        }
        var sink = options.File is { Length: > 0 } file
            ? new RollingFileSink(file, options.FileTimeZone, options.MaxFileBytes, options.MaxLogFiles) : null;
        async ValueTask WriteEntry(LogEntry entry)
        {
            if (sink is not null) await sink.WriteLineAsync(entry.Timestamp, entry.Line).ConfigureAwait(false);
            if (!entry.Packet || options.ConsolePackets) context.Logger.Log(entry.Level, "{Line}", entry.Line);
        }
        var queue = new AsyncLogQueue<LogEntry>(options.QueueCapacity,
            TimeSpan.FromMilliseconds(options.ShutdownFlushTimeoutMs), WriteEntry,
            lost => WriteEntry(new(DateTime.UtcNow, LogLevel.Error,
                $"packet-log-overflow dropped={lost}; log queue full; gameplay packets were not dropped", false)),
            () => sink is null ? ValueTask.CompletedTask : new ValueTask(sink.FlushAsync()),
            failure => context.Logger.LogCritical(failure, "Packet log writer failed; file logging stopped."),
            () => sink?.Dispose());
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(queue), cancellationToken).ConfigureAwait(false);
        PacketCapture? capture = null;
        if (options.CaptureEnabled)
        {
            capture = new PacketCapture(options, failure =>
                context.Logger.LogCritical(failure, "Packet capture writer failed; capture stopped."));
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(capture), cancellationToken).ConfigureAwait(false);
            context.Logger.LogInformation("Full packet capture enabled: {Path}, memory limit {Bytes} bytes.",
                options.CaptureFile, options.CaptureMaxQueuedBytes);
        }

        void Log(LogLevel level, string line, bool packet = false)
        {
            queue.Enqueue(new(DateTime.UtcNow, level, line, packet));
        }

        context.Effect(context.Events.On<PacketReceivedEvent>(e =>
        {
            var name = Describe(e.ProtocolName, PacketDirection.ClientToServer, e.Packet.Opcode);
            capture?.Record(e.Session, e.ProtocolName, "c2s", e.Packet.Opcode, name, e.Packet.Payload, e.HasHandler);
            if (e.HasHandler)
            {
                var preview = previewHandled.Contains(e.Packet.Opcode)
                    ? $" preview={HexPreview(e.Packet.Payload.Span, options.PreviewBytes)}" : "";
                Log(LogLevel.Information, $"recv {e.ProtocolName} {name} len={e.Packet.Payload.Length} session={e.Session.Id} conn={e.Session.Connection.RemoteEndPoint}{preview}", packet: true);
            }
            else
            {
                var preview = HexPreview(e.Packet.Payload.Span, options.PreviewBytes);
                Log(LogLevel.Warning, $"recv {e.ProtocolName} {name} len={e.Packet.Payload.Length} session={e.Session.Id} conn={e.Session.Connection.RemoteEndPoint} unhandled preview={preview}", packet: true);
            }
        }));

        context.Effect(context.Events.On<PacketSentEvent>(e =>
        {
            var name = Describe(e.ProtocolName, PacketDirection.ServerToClient, e.Opcode);
            if (capture is not null)
            {
                if (e.Payload.Length != e.PayloadLength)
                    throw new InvalidOperationException("Full packet capture requires the complete sent payload.");
                capture.Record(e.Session, e.ProtocolName, "s2c", e.Opcode, name, e.Payload, null);
            }
            Log(LogLevel.Information, $"send {e.ProtocolName} {name} len={e.PayloadLength} session={e.Session.Id}", packet: true);
        }));

        context.Effect(context.Events.On<ConnectionAcceptedEvent>(e =>
            Log(LogLevel.Information, $"accept listener={e.ListenerId} conn={e.Connection.RemoteEndPoint} id={e.Connection.Id}")));

        context.Effect(context.Events.On<ConnectionClosedEvent>(e =>
            Log(LogLevel.Information, $"close conn={e.Connection.RemoteEndPoint} id={e.Connection.Id} reason={e.Reason}")));

        context.Effect(context.Events.On<ClientVersionMismatchEvent>(e =>
            Log(LogLevel.Warning, $"version-mismatch session={e.Session.Id} reported={e.Reported} expected={e.Expected}")));

    }

    private sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Line, bool Packet);

    internal static void WriteLog(ILogger logger, Action<string>? file, bool consolePackets,
        bool packet, LogLevel level, string line)
    {
        // Packet events run on receive and send loops; a full console queue must not stall those loops.
        file?.Invoke(line);
        if (!packet || consolePackets) logger.Log(level, "{Line}", line);
    }

    private static string ResolveFilePath(string path, string timeZone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var stamp = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return path.Replace("{timestamp}", stamp, StringComparison.Ordinal)
            .Replace("{date}", TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string HexPreview(ReadOnlySpan<byte> payload, int maxBytes)
    {
        var count = Math.Min(payload.Length, Math.Max(0, maxBytes));
        return count == 0 ? "-" : Convert.ToHexString(payload[..count]);
    }

    internal sealed class RollingFileSink : IDisposable
    {
        private string _path;
        private readonly string _templatePath;
        private readonly string _timeZone;
        private string _date;
        private readonly long _maxBytes;
        private StreamWriter? _writer;
        private readonly bool _prefixTimestamp;

        public RollingFileSink(string templatePath, string timeZone, int maxBytes, int maxFiles, bool prefixTimestamp = true)
        {
            _prefixTimestamp = prefixTimestamp;
            _templatePath = templatePath;
            _timeZone = timeZone;
            _date = CurrentDate();
            _path = Path.GetFullPath(ResolveFilePath(templatePath, timeZone));
            _maxBytes = Math.Max(1, maxBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var templateName = Path.GetFileName(templatePath);
            var stampIndex = templateName.IndexOf("{timestamp}", StringComparison.Ordinal);
            if (stampIndex >= 0)
            {
                var name = Path.GetFileName(_path);
                var suffix = templateName[(stampIndex + "{timestamp}".Length)..];
                var prefix = templateName[..stampIndex];
                var directory = new DirectoryInfo(Path.GetDirectoryName(_path)!);
                IEnumerable<FileInfo> HistoricalFiles() => directory.EnumerateFiles(prefix + "*" + suffix)
                    .Where(entry => entry.Name.Length == name.Length
                        && entry.Name.StartsWith(prefix, StringComparison.Ordinal)
                        && entry.Name.EndsWith(suffix, StringComparison.Ordinal)
                        && DateTime.TryParseExact(entry.Name.AsSpan(prefix.Length, 15), "yyyyMMdd_HHmmss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
                var keep = Math.Max(0, maxFiles - 1);
                var newest = new PriorityQueue<string, string>(StringComparer.Ordinal);
                foreach (var entry in HistoricalFiles())
                {
                    if (keep == 0) break;
                    if (newest.Count < keep) newest.Enqueue(entry.Name, entry.Name);
                    else newest.EnqueueDequeue(entry.Name, entry.Name);
                }
                // Retain only the configured newest names; a second streaming pass avoids a directory-sized array.
                var oldestKept = newest.Count == 0 ? null : newest.Peek();
                foreach (var entry in HistoricalFiles())
                {
                    if (oldestKept is not null && StringComparer.Ordinal.Compare(entry.Name, oldestKept) >= 0) continue;
                    entry.Delete();
                    var rotated = entry.FullName + ".1";
                    if (System.IO.File.Exists(rotated)) System.IO.File.Delete(rotated);
                }
            }
        }

        private string CurrentDate() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(_timeZone)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public async ValueTask WriteLineAsync(DateTime timestamp, string line)
        {
            if (_templatePath.Contains("{date}", StringComparison.Ordinal) && CurrentDate() != _date)
            {
                _writer?.Dispose();
                _writer = null;
                _date = CurrentDate();
                _path = Path.GetFullPath(ResolveFilePath(_templatePath, _timeZone));
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            }
            _writer ??= OpenWriter(FileMode.Append);
            if (_writer.BaseStream.Length > _maxBytes)
            {
                _writer.Dispose();
                var rotated = _path + ".1";
                File.Move(_path, rotated, overwrite: true);
                _writer = OpenWriter(FileMode.Create);
            }
            await _writer.WriteLineAsync(_prefixTimestamp ? $"{timestamp:yyyy-MM-ddTHH:mm:ssZ} {line}" : line).ConfigureAwait(false);
        }

        private StreamWriter OpenWriter(FileMode mode) => new(new FileStream(_path, mode,
            FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous), Encoding.UTF8, 65536);

        public Task FlushAsync() => _writer?.FlushAsync() ?? Task.CompletedTask;

        public void Dispose()
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>Configuration for the <see cref="PacketLogPlugin"/> row.</summary>
public sealed class PacketLogOptions
{
    /// <summary>Gets or sets whether complete unencrypted packet payloads are recorded by a separate bounded background writer.</summary>
    public bool CaptureEnabled { get; set; }

    /// <summary>Gets or sets the JSON Lines capture path, separate from normal packet summaries.</summary>
    [Required, MinLength(1)]
    public string CaptureFile { get; set; } = "logs/{date}/capture_{timestamp}.jsonl";

    /// <summary>Gets or sets the maximum pending capture records before explicitly reporting dropped diagnostic records.</summary>
    [Range(16, 65536)]
    public int CaptureQueueCapacity { get; set; } = 4096;

    /// <summary>Gets or sets the total queued payload memory limit; gameplay never waits for capture capacity.</summary>
    [Range(1048576, 536870912)]
    public int CaptureMaxQueuedBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Gets or sets the capture file size before rotation to its single backup.</summary>
    [Range(1024, int.MaxValue)]
    public int CaptureMaxFileBytes { get; set; } = 128 * 1024 * 1024;

    /// <summary>Gets or sets whether packet traffic is also sent to console logging by the background writer.</summary>
    public bool ConsolePackets { get; set; }

    /// <summary>Gets or sets the bounded pending log count; overflow is reported without delaying network traffic.</summary>
    [Range(256, 1048576)]
    public int QueueCapacity { get; set; } = 32768;

    /// <summary>Gets or sets the maximum shutdown wait for queued records to flush before reporting an error.</summary>
    [Range(100, 60000)]
    public int ShutdownFlushTimeoutMs { get; set; } = 10000;

    /// <summary>Gets or sets the profile directory containing research catalogs used only for diagnostic names. Empty disables hints.</summary>
    public string CatalogDirectory { get; set; } = "profiles";

    /// <summary>Gets or sets the maximum total research rows loaded into the diagnostic name index.</summary>
    [Range(1, 100000)]
    public int MaxCatalogEntries { get; set; } = 32768;

    /// <summary>Gets or sets the largest permitted catalog file before YAML parsing.</summary>
    [Range(1024, 16777216)]
    public int MaxCatalogFileBytes { get; set; } = 4194304;

    /// <summary>Gets or sets the rolling log path; timestamp identifies the run and date changes switch the active directory. Empty disables the sink.</summary>
    public string? File { get; set; } = "logs/{date}/packets_{timestamp}.log";

    /// <summary>Gets or sets the timezone used to expand the filename timestamp.</summary>
    [Required]
    public string FileTimeZone { get; set; } = "China Standard Time";

    /// <summary>Gets or sets how many timestamped runs are retained per filename prefix, including the current run.</summary>
    [Range(1, 10000)]
    public int MaxLogFiles { get; set; } = 100;

    /// <summary>Gets or sets the file size at which the log rotates to <c>.1</c>.</summary>
    [Range(1024, int.MaxValue)]
    public int MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Gets or sets how many payload bytes are shown in the unhandled-packet preview.</summary>
    [Range(0, 4096)]
    public int PreviewBytes { get; set; } = 96;

    /// <summary>Gets or sets the inbound opcodes whose handled payloads also receive the bounded hex preview.</summary>
    [Required, MaxLength(64)]
    public ushort[] PreviewHandledOpcodes { get; set; } = [];
}
