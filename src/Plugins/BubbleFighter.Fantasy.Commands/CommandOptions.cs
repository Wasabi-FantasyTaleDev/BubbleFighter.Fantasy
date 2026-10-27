using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.Commands;

/// <summary>Limits registrations, input, output and concurrent handler work without an admission queue.</summary>
public sealed class CommandOptions
{
    /// <summary>Gets or sets the maximum active canonical command registrations.</summary>
    [Range(1, 8192)] public int MaxCommands { get; set; } = 512;
    /// <summary>Gets or sets the maximum characters in one text or structured invocation.</summary>
    [Range(64, 65536)] public int MaxInputCharacters { get; set; } = 4096;
    /// <summary>Gets or sets the maximum characters in one handler's result message.</summary>
    [Range(64, 65536)] public int MaxResultCharacters { get; set; } = 16384;
    /// <summary>Gets or sets the maximum supervised invocations; excess calls receive Busy immediately.</summary>
    [Range(1, 4096)] public int MaxConcurrent { get; set; } = 64;
}
