using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// The configuration of a wire dialect row: which profile it serves and the stream data that build's
/// binary fixes. The row's own <c>client_build</c> supplies region, executable major and resource revision.
/// </summary>
public sealed class WireDialectOptions : IValidatableOptions
{
    /// <summary>Gets or sets the profile id the dialect serves (the protocol name before <c>/game</c>).</summary>
    [Required]
    public string ProfileId { get; set; } = "";

    /// <summary>Gets or sets the frame layout: <c>modern_8</c>, <c>legacy_5</c> or <c>legacy_10</c>.</summary>
    [Required]
    public string Frame { get; set; } = "";

    /// <summary>Gets or sets the build's first cipher table in hexadecimal; empty for the shared pair.</summary>
    public string CipherTableA { get; set; } = "";

    /// <summary>Gets or sets the build's second cipher table in hexadecimal; empty for the shared pair.</summary>
    public string CipherTableB { get; set; } = "";

    /// <summary>Gets or sets a one-line description of the build.</summary>
    public string Description { get; set; } = "";

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        ValidationResult? failure = null;
        try
        {
            WireStream.Parse(Frame, CipherTableA, CipherTableB);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            failure = new ValidationResult(ex.Message, [nameof(Frame), nameof(CipherTableA), nameof(CipherTableB)]);
        }
        if (failure is not null)
            yield return failure;
    }

    /// <summary>Creates the dialect these options describe for one client build.</summary>
    /// <param name="build">The row's client build.</param>
    /// <returns>The dialect.</returns>
    public WireDialect CreateDialect(ClientBuild build) =>
        WireDialect.Create(new ClientProfile(ProfileId, build.Region, build.Major, 0, build.Resource,
                string.IsNullOrWhiteSpace(Description) ? $"{build.Region} client, ClientVersion_Major {build.Major}" : Description),
            WireStream.Parse(Frame, CipherTableA, CipherTableB));
}

/// <summary>
/// A dialect provider row that publishes the one dialect its configuration describes, so a client build
/// is added with profile data instead of a class. Region plugins derive from it only to keep their ids.
/// </summary>
public abstract class ConfiguredDialectPlugin : IPlugin, IWireDialectProvider
{
    private IReadOnlyList<IWireDialect> _dialects = [];

    /// <inheritdoc />
    public IReadOnlyList<IWireDialect> Dialects => _dialects;

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<WireDialectOptions>();
        var build = context.Row.ClientBuild
            ?? throw new ConfigException($"Dialect row '{context.Row.Id}' requires the profile's client_build.");
        _dialects = [options.CreateDialect(build)];
        context.Effect(context.Services.RegisterMany<IWireDialectProvider>(this));
        return ValueTask.CompletedTask;
    }
}
