using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BubbleFighter.Fantasy.Host;

/// <summary>Waits for redirected standard input without owning a blocking read that outlives server shutdown.</summary>
internal sealed class RedirectedStdin(Stream stream)
{
    private readonly byte[] _byte = new byte[1];

    internal async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (IsReadable())
                return stream.Read(_byte, 0, 1) == 0 ? -1 : _byte[0];
            // Redirected Windows console streams use synchronous reads; probe before reading so cancellation can join.
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private static bool IsReadable()
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = GetStdHandle(-10);
            var type = GetFileType(handle);
            if (type == 0)
                throw new IOException("Managed standard input has an invalid handle.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (type != 3) return true; // Regular files and NUL cannot wait for another pipe writer.
            if (PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
                return available > 0;
            var error = Marshal.GetLastWin32Error();
            if (error is 109 or 233) return true; // Broken/disconnected pipe: the following read consumes EOF.
            throw new IOException("Cannot inspect managed standard input.", new Win32Exception(error));
        }
        if (OperatingSystem.IsLinux())
        {
            var descriptor = new PollDescriptor { FileDescriptor = 0, Events = 1 };
            var result = Poll(ref descriptor, 1, 0);
            if (result >= 0)
            {
                if ((descriptor.ReturnedEvents & (8 | 32)) != 0)
                    throw new IOException("Managed standard input has an invalid or failed descriptor.");
                return result > 0;
            }
            var error = Marshal.GetLastPInvokeError();
            if (error == 4) return false; // EINTR: retry after observing shutdown cancellation.
            throw new IOException("Cannot inspect managed standard input.", new Win32Exception(error));
        }
        throw new PlatformNotSupportedException("Managed standard input is supported on Windows and Linux.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        internal int FileDescriptor;
        internal short Events;
        internal short ReturnedEvents;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(IntPtr handle, IntPtr buffer, uint bufferSize,
        IntPtr bytesRead, out uint totalBytesAvailable, IntPtr bytesLeft);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor descriptors, nuint count, int timeout);
}
