using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Installation;

namespace BubbleFighter.Fantasy.Installation;

/// <summary>The setup assistant's persisted claim: schema 1, one operation id, settings, and the PBKDF2 credential material.</summary>
/// <param name="Schema">The state schema version this format carries.</param>
/// <param name="OperationId">The claim's idempotency key; a repeat begin with the same id is a no-op.</param>
/// <param name="Settings">The validated settings the apply operation will use.</param>
/// <param name="Username">The claimed administrator's name.</param>
/// <param name="Salt">Base64 of the 16-byte credential salt.</param>
/// <param name="PasswordHash">Base64 of the 32-byte PBKDF2 hash.</param>
/// <param name="Installed">Whether the apply operation finished and the deployment is ready.</param>
internal sealed record InstallationDocument(int Schema, Guid OperationId, InstallationSettings Settings,
    string Username, string Salt, string PasswordHash, bool Installed);

/// <summary>
/// Owns the setup assistant's state file inside an operator-private directory. The directory is
/// created under the state root's <c>administration</c> subfolder (the root may hold saves and
/// backups too), access is restricted to the running identity (Windows DACL or Unix 0700), and every
/// path involved — directory, lease, state, temporary — refuses reparse points. The lease file stays
/// on disk after release for the same inode-reuse reason as the SQLite store. Reads are strict:
/// unknown members or a schema that fails its identity checks
/// (base64 salt of 16 bytes, hash of 32) are load errors with a restore-from-backup instruction, not
/// defaults. Saves go through a write-through temporary plus atomic rename, so a crash leaves the
/// previous document readable.
/// </summary>
internal sealed class InstallationStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
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

    /// <summary>Reads the persisted claim or <see langword="null"/> when none exists.</summary>
    /// <exception cref="IOException">Thrown for an empty, foreign-schema or identity-violating document.</exception>
    public InstallationDocument? Load()
    {
        if (!File.Exists(_path)) return null;
        RejectLink(_path);
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = JsonSerializer.Deserialize<InstallationDocument>(stream, Json)
            ?? throw new IOException("Installation state is empty.");
        if (document.Schema != 1 || document.OperationId == Guid.Empty || document.Settings is null ||
            string.IsNullOrWhiteSpace(document.Username) || !IsBase64(document.Salt, 16) || !IsBase64(document.PasswordHash, 32))
            throw new IOException("Installation state has an unsupported or invalid schema. Restore a reviewed local backup.");
        return document;
    }

    /// <summary>Replaces the persisted claim through a write-through temporary and atomic rename.</summary>
    /// <exception cref="IOException">Thrown when a reparse point appears in its path.</exception>
    public void Save(InstallationDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
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
