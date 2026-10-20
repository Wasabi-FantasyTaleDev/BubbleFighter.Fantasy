using System.Text;
using System.Text.Json;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>A plugin channel whose protocol output never produces a line and whose exit is immediate.</summary>
internal sealed class SilentChannel : IPluginChannel
{
    private readonly MemoryStream _input = new();
    private readonly SilentStream _output = new();

    /// <summary>Gets the protocol text the host has written so far.</summary>
    public string Written => Encoding.UTF8.GetString(_input.ToArray());

    public Stream Input => _input;
    public Stream Output => _output;
    public Stream Error { get; } = new MemoryStream();
    public int ExitCode => 0;
    public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
    public void CloseInput() { }
    public void Kill() { }
    public void Dispose() { }

    /// <summary>A readable stream that stays silent until its read is cancelled.</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}

/// <summary>Accepts plugin traffic that the silent channel never produces.</summary>
internal sealed class SilentSink : IInboundSink
{
    public ValueTask<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct) =>
        ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

    public void HandleNotification(string method, JsonElement parameters) { }
}
