using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;

namespace BubbleFighter.Fantasy.ScienceLab;

/// <summary>Installs audited legacy synthesis and decomposition with explicit atomic recipe policy.</summary>
[Plugin("science-lab", Category = "items", Names = ["en=Laboratory", "zh-Hans=实验室", "zh-Hant=實驗室", "ko=연구소"], Description = "Atomic native legacy synthesis and decomposition.",
    Optional = [typeof(IClientDataStore), typeof(IInventoryBuildScope), typeof(ILaboratoryInventoryStore),
        typeof(IMessageRouter), typeof(IEquipmentArticleResolver), typeof(IPetEntitlements), typeof(IGameText)],
    OptionalMany = [typeof(IInboundMessageBinding<LaboratoryRequested>), typeof(IMessageSender<LaboratoryRefused>),
        typeof(IMessageSender<LaboratoryCompleted>), typeof(IMessageSender<InventoryReplaced>), typeof(IMessageSender<ServerNotice>)])]
public sealed class ScienceLabPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ScienceLabOptions>();
        if (!options.Enabled) return ValueTask.CompletedTask;
        var scope = context.Services.Get<IInventoryBuildScope>().Scope;
        var catalog = LaboratoryCatalog.Load(context.Services.Get<IClientDataStore>(), options);
        catalog.RequirePolicy(options, scope);
        var bonusPool = catalog.ResolveBonusPool(options, scope);
        if (context.Services.GetAll<IInboundMessageBinding<LaboratoryRequested>>().Count(binding => binding.Protocol == options.Protocol) != 1)
            throw new ConfigException("Science-room recipes require exactly one audited legacy binding.");
        context.Services.TryGet<IEquipmentArticleResolver>(out var articles);
        context.Services.TryGet<IPetEntitlements>(out var pets);
        context.Services.TryGet<IGameText>(out var texts);
        // A refusal explains itself through the build's notice packet when the build has one; the native result stays -1.
        var notices = context.Services.GetAll<IMessageSender<ServerNotice>>().SingleOrDefault(sender => sender.Protocol == options.Protocol);
        var runtime = new LaboratoryRuntime(options, catalog, scope, context.Services.Get<ILaboratoryInventoryStore>(),
            Sender<LaboratoryRefused>(context, options.Protocol), Sender<LaboratoryCompleted>(context, options.Protocol),
            Sender<InventoryReplaced>(context, options.Protocol), TimeProvider.System, Random.Shared.Next, articles, pets, bonusPool,
            context.Logger, notices, texts);
        context.Effect(runtime);
        context.Effect(context.Services.Get<IMessageRouter>().For(context).On<LaboratoryRequested>(options.Protocol, runtime.HandleAsync));
        return ValueTask.CompletedTask;
    }

    private static IMessageSender<T> Sender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).Take(2).ToArray();
        return senders.Length == 1 ? senders[0] : throw new ConfigException($"Science-room recipes require one {typeof(T).Name} sender for {protocol}.");
    }
}
