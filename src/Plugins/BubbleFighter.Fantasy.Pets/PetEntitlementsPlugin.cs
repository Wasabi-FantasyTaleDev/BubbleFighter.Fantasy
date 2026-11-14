using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets;

/// <summary>Provides pet identity resolution from the inventory row's canonical catalog before equipment activates.</summary>
[Plugin("pets.entitlements", Category = "items", Names = ["en=Pet ownership", "zh-Hans=宠物归属", "zh-Hant=寵物歸屬", "ko=펫 소유"], Description = "Durable pet identities and independent client-build progression.",
    Optional = [typeof(IInventoryMetadataStore), typeof(IClientDataStore), typeof(IPetEntitlementStore), typeof(IInventoryLoadoutStore), typeof(IInventoryBuildScope)], ConditionalProvides = [typeof(IPetEntitlements)])]
public sealed class PetEntitlementsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<PetsOptions>();
        if (!options.Enabled) return ValueTask.CompletedTask;
        var catalog = PetCatalogLoader.Load(context.Services.Get<IClientDataStore>(), options.CatalogPath,
            options.MaxWorkbookBytes, options.MaxCatalogRows, options.MaxCatalogColumns, options.MaxCellCharacters);
        if (options.InitialFullness > catalog.HungerMaximum)
            throw new ConfigException("initial_fullness exceeds this client's native hunger maximum.");
        var scope = context.Services.Get<IInventoryBuildScope>().Scope;
        if (scope.Key != options.BuildKey || scope.SupportedItemIds.Any(item => !catalog.Items.ContainsKey(item)))
            throw new ConfigException("Pet entitlements must use the inventory row's canonical build catalog.");
        var rules = new PetRules(catalog, options, Random.Shared);
        var entitlements = new PetEntitlements(catalog, rules,
            context.Services.Get<IPetEntitlementStore>(), context.Services.Get<IInventoryLoadoutStore>(), scope, TimeProvider.System);
        context.Effect(context.Services.Register<IPetEntitlements>(entitlements));
        var metadataStore = context.Services.Get<IInventoryMetadataStore>();
        context.Effect(context.Events.Intercept<InventoryMetadataInputsRequested, InventoryMetadataPreparationCommand>(async (request, next, ct) =>
        {
            var command = await next(ct).ConfigureAwait(false);
            if (request.Scope.Key != scope.Key) return command;
            if (command.PetInitialization is not null) throw new ConfigException("Inventory metadata has duplicate pet catalogue providers.");
            return command with { PetInitialization = entitlements.CreateInitialization(request.UserNum, request.Now) };
        }));
        async ValueTask<InventorySnapshot> PrepareAsync(long userNum, CancellationToken ct)
        {
            var command = await context.Events.WaterfallAsync<InventoryMetadataInputsRequested, InventoryMetadataPreparationCommand>(
                new(userNum, scope, DateTimeOffset.UtcNow), _ => ValueTask.FromResult(new InventoryMetadataPreparationCommand(userNum, scope)), ct)
                .ConfigureAwait(false);
            return await metadataStore.PrepareInventorySnapshotAsync(command, ct).ConfigureAwait(false);
        }
        context.Effect(context.Events.Intercept<InventoryMetadataPreparing, PetInventorySnapshot?>(async (request, next, ct) =>
        {
            var previous = await next(ct).ConfigureAwait(false);
            if (request.BuildKey != scope.Key || previous is not null) return previous;
            var snapshot = await PrepareAsync(request.UserNum, ct).ConfigureAwait(false);
            return new(snapshot.CharacterBag, snapshot.EquippedBag);
        }));
        context.Effect(context.Events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>(async (request, next, ct) =>
        {
            var previous = await next(ct).ConfigureAwait(false);
            if (request.Protocol != options.Protocol) return previous;
            if (request.IsComplete)
            {
                if (request.CharacterBag.Concat(request.EquippedBag).Any(row => !scope.Supports(row.ItemId)
                    || catalog.Items.TryGetValue(row.ItemId, out var item) && item.Function == PetCatalog.PetFunction
                    && row.Quantity > 0 && !InventoryExpiry.IsExpired(row, DateTimeOffset.UtcNow)
                    && (row.PetIdentity is null || row.Pet?.Find(scope.Key) is null)))
                    throw new InvalidDataException("A complete inventory snapshot lacks canonical pet metadata.");
                return new(request.CharacterBag, request.EquippedBag);
            }
            if (previous is not null) return previous;
            var snapshot = await PrepareAsync(request.UserNum, ct).ConfigureAwait(false);
            return new(snapshot.CharacterBag, snapshot.EquippedBag);
        }));
        return ValueTask.CompletedTask;
    }
}
