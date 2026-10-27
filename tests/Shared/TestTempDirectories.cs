using System.Diagnostics;

namespace BubbleFighter.Fantasy.Tests;

/// <summary>
/// Scratch directories that live as long as the test process: <c>&lt;prefix&gt;-&lt;process id&gt;</c> under the system
/// temp directory, deleted when the process exits. Copies of client files and generated tables are written there,
/// and a run that left them behind used to fill the system drive, so the first use in a process also removes the
/// directories of the same prefixes whose process no longer runs (a crashed or killed run).
/// </summary>
internal static class TestTempDirectories
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Owned = new(StringComparer.OrdinalIgnoreCase);
    private static bool _hooked;

    /// <summary>Gets this process's directory for a prefix, creating it and scheduling its deletion at exit.</summary>
    /// <param name="prefix">A name such as <c>bff-test-clientdata</c>, without the process id.</param>
    /// <returns>The directory path.</returns>
    public static string ForProcess(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Environment.ProcessId);
        lock (Gate)
        {
            if (Owned.Add(path))
            {
                SweepStale(prefix);
                if (!_hooked)
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteOwned();
                    _hooked = true;
                }
            }
            Directory.CreateDirectory(path);
        }
        return path;
    }

    /// <summary>Gets a fresh directory for one test inside this process's directory for a prefix.</summary>
    /// <param name="prefix">The process directory's prefix.</param>
    /// <returns>A new, empty directory.</returns>
    public static string ForTest(string prefix) =>
        Directory.CreateDirectory(Path.Combine(ForProcess(prefix), Guid.NewGuid().ToString("N"))).FullName;

    private static void DeleteOwned()
    {
        lock (Gate)
            foreach (var path in Owned)
                TryDelete(path);
    }

    private static void SweepStale(string prefix)
    {
        foreach (var directory in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(prefix + "-*"))
        {
            if (!int.TryParse(directory.Name.AsSpan(prefix.Length + 1), out var processId)
                || processId == Environment.ProcessId || IsRunning(processId))
                continue;
            TryDelete(directory.FullName);
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Windows reuses process ids: a stale directory can name a live service or another
            // user's process, whose exit state this account may not query (Win32Exception 5).
            // Treating it as still running keeps the directory and the test run alive; the
            // directory is never deleted on a guess.
            return true;
        }
    }

    // A recursive delete removes a link inside the tree without entering it, and a root that is itself a link is
    // never treated as one of these scratch directories. A directory still in use by another file handle is left
    // for the next sweep instead of failing the test run.
    private static void TryDelete(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                info.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
