using BubbleFighter.Fantasy.Contracts.Installation;

namespace BubbleFighter.Fantasy.Installation;

// This lists local folders only. Authorization is enforced by the same boundary as installation mutations.
internal static class InstallationDirectoryBrowser
{
    public static InstallationDirectoryPage Browse(string? path, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram)
            .Select(d => d.RootDirectory.FullName).ToArray();
        if (string.IsNullOrWhiteSpace(path))
            return new(null, null, roots.Take(limit).Select(p => new InstallationDirectory(p, p)).ToArray(), roots.Length > limit);
        if (path.Length > 2048 || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException("directory_unavailable");
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(path));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!roots.Any(root => current.FullName.StartsWith(root, comparison)))
                throw new ArgumentException("directory_unavailable");
            for (var ancestor = current; ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("directory_unavailable");
            var directories = new List<InstallationDirectory>();
            // Stop enumeration at the response bound, even when entries are filtered; never scan an entire large tree.
            var inspected = 0;
            var truncated = false;
            foreach (var directory in current.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > limit) { truncated = true; break; }
                if ((directory.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0)
                    directories.Add(new(directory.Name, directory.FullName));
            }
            return new(current.FullName, current.Parent?.FullName,
                directories.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray(), truncated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new ArgumentException("directory_unavailable"); }
    }
}
