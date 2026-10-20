using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Native;

namespace BubbleFighter.Fantasy.Loader.Native;

/// <summary>Publishes a bounded native module source as an ordinary activation-owned plugin effect.</summary>
[Plugin("loader.native", Description = "Publishes native module definitions from configured roots.", Requires = [typeof(IPluginHost)])]
public sealed class NativeLoaderPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<NativeLoaderConfig>();
        var plan = NativePluginModules.Prepare(options.Roots, options.MaximumDefinitions, options.ExcludedAssemblyNames,
            options.MaximumScannedDirectories, cancellationToken);
        var host = context.Services.Get<IPluginHost>();
        // Registration attaches its ownership effect before invoking this producer.
        await host.RegisterDefinitionSourceAsync(context, options.SourceId, options.MaximumDefinitions,
            plan.CoordinationKeys, plan.LoadAsync, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Configures a native source's pre-acquisition search roots, identity and definition reservation.</summary>
public sealed class NativeLoaderConfig : IValidatableOptions
{
    /// <summary>Gets or sets the existing root directories whose immediate child folders contain module DLLs.</summary>
    [Required, MinLength(1)]
    public string[] Roots { get; set; } = [];

    /// <summary>Gets or sets simple module assembly names already provided by the application's seed catalog.</summary>
    [Required]
    public string[] ExcludedAssemblyNames { get; set; } = [];

    /// <summary>Gets or sets the source identity within this loader activation.</summary>
    [Required]
    public string SourceId { get; set; } = "native";

    /// <summary>Gets or sets the traversal budget counting every child directory, including excluded or nonmodule folders.</summary>
    [Range(1, int.MaxValue)]
    public int MaximumScannedDirectories { get; set; } = 4096;

    /// <summary>Gets or sets the complete definition capacity reserved before any module is loaded.</summary>
    [Range(1, int.MaxValue)]
    public int MaximumDefinitions { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceId))
            yield return new ValidationResult("source_id must not be whitespace.", [nameof(SourceId)]);
        if (Roots is null) yield break;
        if (Roots.Any(string.IsNullOrWhiteSpace))
            yield return new ValidationResult("roots must contain nonempty paths.", [nameof(Roots)]);
        if (Roots.Length > MaximumDefinitions)
            yield return new ValidationResult("roots count must not exceed maximum_definitions.", [nameof(Roots)]);
    }
}
