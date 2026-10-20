using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Installation;

namespace BubbleFighter.Fantasy.Installation;

internal sealed record InstallationDocument(int Schema, Guid OperationId, InstallationSettings Settings,
    string Username, string Salt, string PasswordHash, bool Installed);

internal sealed class InstallationStore : IDisposable
{
    private const int MaximumBytes = 65536;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    };
    private readonly string _path;
    private readonly FileStream _lock;

    public InstallationStore(string directory)
    {
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The installation directory cannot be a symbolic link.");
        // Own only this subdirectory: the operator's state root can also contain saves and backups.
        directory = Path.Combine(directory, "administration");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The administration directory cannot be a symbolic link.");
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var identity = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot resolve the state owner.");
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(directory, "installation.json");
        var lockPath = Path.Combine(directory, "installation.lock");
        RejectLink(lockPath);
        _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public InstallationDocument? Load()
    {
        if (!File.Exists(_path)) return null;
        RejectLink(_path);
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new IOException("Installation state exceeds its size limit.");
        var document = JsonSerializer.Deserialize<InstallationDocument>(stream, Json)
            ?? throw new IOException("Installation state is empty.");
        if (document.Schema != 1 || document.OperationId == Guid.Empty || document.Settings is null ||
            string.IsNullOrWhiteSpace(document.Username) || !IsBase64(document.Salt, 16) || !IsBase64(document.PasswordHash, 32))
            throw new IOException("Installation state has an unsupported or invalid schema. Restore a reviewed local backup.");
        return document;
    }

    public void Save(InstallationDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumBytes) throw new IOException("Installation state exceeds its size limit.");
        var temporary = _path + ".next";
        RejectLink(_path);
        RejectLink(temporary);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsBase64(string? value, int length)
    {
        if (value is null || value.Length > 128) return false;
        Span<byte> buffer = stackalloc byte[64];
        return Convert.TryFromBase64String(value, buffer, out var count) && count == length;
    }

    private static void RejectLink(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Installation state cannot use symbolic links.");
    }

    public void Dispose() => _lock.Dispose();
}
