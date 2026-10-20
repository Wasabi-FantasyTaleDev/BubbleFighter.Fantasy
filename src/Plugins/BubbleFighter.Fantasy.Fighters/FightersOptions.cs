using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Fighters;

/// <summary>
/// Configuration for the <see cref="FightersPlugin"/> row: the client path of the fighter catalog and its
/// size cap. The catalog belongs to the row's client data (the <c>clientdata</c> row), and the shared
/// <c>dialect.messages</c> row serves native creation, so this row carries no protocol or client version of
/// its own.
/// </summary>
public sealed class FightersOptions : IValidatableOptions
{
    /// <summary>
    /// Gets or sets the client path of <c>FighterConfig.xml</c> (worksheet <c>FighterConfig</c>,
    /// <c>FighterConfig:HorzTable</c>), read from the build's client data through <c>IClientDataStore</c>;
    /// kr-1256 holds it at <c>Config/FighterConfig.xml</c>. Empty fails validation.
    /// </summary>
    [Required]
    public string FighterConfigClientPath { get; set; } = "Config/FighterConfig.xml";

    /// <summary>Gets or sets the inclusive catalog file-size cap in bytes.</summary>
    [Range(1, 200_000_000)]
    public long MaxFighterConfigBytes { get; set; } = 134_217_728;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (string.IsNullOrWhiteSpace(FighterConfigClientPath))
        {
            yield return new ValidationResult(
                "fighter_config_client_path is required. It names Config/FighterConfig.xml in the build's client data.",
                [nameof(FighterConfigClientPath)]);
        }
    }
}
