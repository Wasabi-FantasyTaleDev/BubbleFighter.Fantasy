using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Admin;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.WebUi;

/// <summary>
/// Publishes the built administration web interface as <see cref="IAdminWebContent"/>, and the
/// release names it shows for client builds as <see cref="IBuildReleaseLabels"/>. The files are
/// embedded in this assembly, so the row needs no files next to the server; the <c>admin.http</c>
/// row picks both services up and serves them.
/// </summary>
[Plugin("webui", Description = "Administration web interface.", Provides = [typeof(IAdminWebContent), typeof(IBuildReleaseLabels)])]
public sealed class WebUiPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<WebUiOptions>();
        var content = WebContentFiles.FromAssembly(typeof(WebUiPlugin).Assembly);
        IAdminWebContent published = options.TutorialDirectory.Length == 0 ? content : new TutorialWebContent(content, options);
        context.Effect(context.Services.Register<IAdminWebContent>(published));
        context.Effect(context.Services.Register<IBuildReleaseLabels>(new BuildReleaseLabels(options.Releases)));
        context.Logger.LogInformation("Row '{RowId}' serves {Count} web interface files and {Releases} build release names.",
            context.Row.Id, content.Count, options.Releases.Count);
        return ValueTask.CompletedTask;
    }
}
