using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Installation;

/// <summary>Local installation authority, remote bootstrap policy and bounded authentication work.</summary>
public sealed class InstallationOptions : IValidatableOptions
{
    /// <summary>Gets or sets the absolute, writable directory that survives release upgrades.</summary>
    [Required]
    public string StateDirectory { get; set; } = "";
    /// <summary>Gets or sets the explicit secret required to claim an installation from a non-loopback connection.</summary>
    [MaxLength(256)]
    public string BootstrapToken { get; set; } = "";
    /// <summary>Gets or sets the maximum duration of a real database probe or initialization.</summary>
    [Range(1, 600)]
    public int DatabaseTimeoutSeconds { get; set; } = 30;
    /// <summary>Gets or sets the maximum number of directories returned by one folder-picker request.</summary>
    [Range(1, 500)]
    public int DirectoryEntryLimit { get; set; } = 100;
    /// <summary>Gets or sets the byte limit for a client-data upload, including multipart framing.</summary>
    [Range(typeof(long), "1048576", "8589934592")]
    public long ClientDataUploadBytes { get; set; } = 8589934592;
    /// <summary>Gets or sets the total extracted bytes allowed in one client-data import.</summary>
    [Range(typeof(long), "1048576", "34359738368")]
    public long ClientDataExtractedBytes { get; set; } = 17179869184;
    /// <summary>Gets or sets the retained import storage quota; temporary archive bytes also count during extraction.</summary>
    [Range(typeof(long), "1048576", "68719476736")]
    public long ClientDataStorageBytes { get; set; } = 68719476736;
    /// <summary>Gets or sets the maximum file or ZIP-entry count in one import.</summary>
    [Range(1, 65534)]
    public int ClientDataFileLimit { get; set; } = 32768;
    /// <summary>Gets or sets the maximum retained import copies, including incomplete copies left by a process interruption.</summary>
    [Range(1, 256)]
    public int ClientDataCopyLimit { get; set; } = 64;
    /// <summary>Gets or sets the maximum time spent receiving and validating an import.</summary>
    [Range(10, 3600)]
    public int ClientDataImportSeconds { get; set; } = 1200;
    /// <summary>Gets or sets the maximum concurrent live administration sessions.</summary>
    [Range(1, 256)]
    public int SessionCapacity { get; set; } = 32;
    /// <summary>Gets or sets the absolute session lifetime in minutes.</summary>
    [Range(1, 1440)]
    public int SessionMinutes { get; set; } = 60;
    /// <summary>Gets or sets the number of rejected login attempts permitted per authentication window.</summary>
    [Range(1, 100)]
    public int LoginAttempts { get; set; } = 5;
    /// <summary>Gets or sets the authentication rate window length in seconds.</summary>
    [Range(1, 3600)]
    public int LoginWindowSeconds { get; set; } = 30;
    /// <summary>Gets or sets the maximum tracked login source addresses; a full table evicts the oldest window.</summary>
    [Range(1, 100000)]
    public int LoginAddressCapacity { get; set; } = 1024;
    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Path.IsPathFullyQualified(StateDirectory)) yield return new("state_directory must be absolute.", [nameof(StateDirectory)]);
        if (BootstrapToken.Length is > 0 and < 32) yield return new("bootstrap_token needs at least 32 characters.", [nameof(BootstrapToken)]);
    }
}
