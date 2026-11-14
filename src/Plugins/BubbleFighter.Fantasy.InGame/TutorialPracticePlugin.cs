using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame;

/// <summary>Saves successful supplementary practice and restores its native completion marker at login and selection.</summary>
[Plugin("tutorial.practice", Category = "gameplay", Names = ["en=Tutorials and practice", "zh-Hans=教程与练习", "zh-Hant=教學與練習", "ko=튜토리얼과 연습"], Requires = [typeof(IMessageRouter), typeof(ITutorialPracticeStore)],
    RequiresMany = [typeof(IMessageSender<TutorialPracticeProgress>)])]
public sealed class TutorialPracticePlugin : IPlugin
{
    /// <summary>Mounts owned practice middleware and login publication for this profile's audited map.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<TutorialPracticeOptions>();
        _ = new TutorialPracticeKey(options.ClientProfile, 1, options.MapKey);
        if (options.Protocol != options.ClientProfile + "/game")
            throw new ConfigException("Tutorial practice protocol must be the configured client profile's game connection.");
        var flow = new TutorialPracticeFlow(context.Services.Get<ITutorialPracticeStore>(),
            PlaySenders.Required<TutorialPracticeProgress>(context, options.Protocol), options.ClientProfile, options.MapKey);
        var routes = context.Services.Get<IMessageRouter>().For(context);
        context.Effect(routes.Use<TutorialStageCleared>(options.Protocol, async (session, request, next, ct) =>
        {
            await next(session, request, ct).ConfigureAwait(false);
            await flow.ClearAsync(session, request, ct).ConfigureAwait(false);
        }));
        context.Effect(routes.Use<TutorialSelectionOpening>(options.Protocol, async (session, request, next, ct) =>
        {
            await next(session, request, ct).ConfigureAwait(false);
            await flow.PublishAsync(session, ct).ConfigureAwait(false);
        }));
        context.Effect(context.Events.OnAsync<LoginMenuInstalled>(async (login, ct) =>
        {
            if (login.Protocol == options.Protocol)
                await flow.PublishAsync(login.Session, ct).ConfigureAwait(false);
        }));
        return ValueTask.CompletedTask;
    }
}

internal sealed class TutorialPracticeOptions
{
    /// <summary>Gets or sets the native game protocol served by this row.</summary>
    [Required]
    public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the build profile used to isolate saved practice identities.</summary>
    [Required]
    public string ClientProfile { get; set; } = "";

    /// <summary>Gets or sets the audited supplementary practice's MapMeta key.</summary>
    [Required]
    public string MapKey { get; set; } = "";
}

internal sealed class TutorialPracticeFlow(ITutorialPracticeStore store, IMessageSender<TutorialPracticeProgress> sender,
    string profile, string mapKey)
{
    internal async ValueTask ClearAsync(ISession session, TutorialStageCleared request, CancellationToken ct)
    {
        if (!string.Equals(request.MapKey, mapKey, StringComparison.OrdinalIgnoreCase)) return;
        var (user, _) = SessionPlayer.RequireIdentity(session);
        await store.RecordClearAsync(new(profile, user, mapKey), ct).ConfigureAwait(false);
        await sender.SendAsync(session, new(true), ct).ConfigureAwait(false);
    }

    internal async ValueTask PublishAsync(ISession session, CancellationToken ct)
    {
        var (user, _) = SessionPlayer.RequireIdentity(session);
        var cleared = await store.HasClearedAsync(new(profile, user, mapKey), ct).ConfigureAwait(false);
        await sender.SendAsync(session, new(cleared), ct).ConfigureAwait(false);
    }
}
