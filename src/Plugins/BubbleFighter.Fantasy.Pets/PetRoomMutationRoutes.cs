using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets;

/// <summary>Keeps each native pet request in flight until its handler and committed appearance publications return.</summary>
internal static class PetRoomMutationRoutes
{
    internal static async ValueTask RegisterAsync(IPluginContext context, PetsOptions options, PetCatalog catalog,
        IPetEntitlements entitlements, IMessageRoutes routes)
    {
        var rooms = context.Services.GetAll<IRoomPolicyHost>().SingleOrDefault(host => host.Protocol == options.Protocol)
            ?? throw new ConfigException("Pet operations require the room policy host for their game protocol.");
        if (rooms.ClientBuild != options.BuildKey)
            throw new ConfigException("Pet operations and their room policy host must select the same client build.");
        var texts = context.Services.Get<IGameText>();
        var notices = Find<ServerNotice>(context, options.Protocol);
        var chat = Find<RoomChatReceived>(context, options.Protocol);
        if (notices is null && chat is null)
            throw new ConfigException("Pet operations require a notice or room chat sender to explain held starts.");

        PetRoomMutationPolicy? acquired = null;
        await context.AcquireEffectAsync(_ =>
        {
            acquired = new(rooms, options.MaxInFlightSessions, options.MaxHeldRooms, ExplainAsync);
            return ValueTask.FromResult<IAsyncDisposable>(acquired);
        }).ConfigureAwait(false);
        var policy = acquired ?? throw new InvalidOperationException("The pet room policy was not acquired.");
        await context.RunTaskAsync("pet-room-start-resumption", async taskCt =>
            await policy.Completion.WaitAsync(taskCt).ConfigureAwait(false)).ConfigureAwait(false);
        context.Effect(rooms.Register(policy));

        var equipment = Find<EquipmentChangeReplied>(context, options.Protocol);
        if (equipment is not null)
            context.Effect(routes.Use<EquipmentChangeRequested>(options.Protocol, (session, request, next, ct) =>
                IsPet(request) ? GuardAsync(policy, session, request, next,
                    token => equipment.SendAsync(session, new(request, false), token), ct)
                    : next(session, request, ct)));
        else if (context.Services.Get<IMessageRouter>().Handlers.Any(handler => handler.Protocol == options.Protocol
            && handler.MessageType == typeof(EquipmentChangeRequested)))
            throw new ConfigException("Pet equipment middleware requires a typed equipment refusal sender.");

        var uses = Require<InventoryItemUseReplied>(context, options.Protocol);
        context.Effect(routes.Use<InventoryItemUseRequested>(options.Protocol, (session, request, next, ct) =>
            catalog.Items.TryGetValue(request.ItemId, out var food) && food.Function is 19 or 20 or 21 or 39
                ? GuardAsync(policy, session, request, next,
                    token => uses.SendAsync(session, new(1, "") { ItemId = request.ItemId }, token), ct)
                : next(session, request, ct)));
        if (options.ManualLevelUp)
        {
            var levels = Require<PetLevelUpReplied>(context, options.Protocol);
            context.Effect(routes.Use<PetLevelUpRequested>(options.Protocol, (session, request, next, ct) =>
                GuardAsync(policy, session, request, next,
                    token => levels.SendAsync(session, new(request.Target, 1), token), ct)));
        }
        if (context.Services.GetAll<IInboundMessageBinding<PetAbilityChangeRequested>>().Any(binding => binding.Protocol == options.Protocol))
        {
            var abilities = Require<PetAbilityChangeReplied>(context, options.Protocol);
            context.Effect(routes.Use<PetAbilityChangeRequested>(options.Protocol, (session, request, next, ct) =>
                GuardAsync(policy, session, request, next,
                    token => abilities.SendAsync(session, new(request.Target, 1, request.NewSelector), token), ct)));
        }

        bool IsPet(EquipmentChangeRequested request) => request.ItemId is { } itemId ? entitlements.IsPetItem(itemId)
            : PetArticleKeys.IsAddressable(unchecked((ulong)request.Reference));

        async ValueTask ExplainAsync(RoomPolicyRoom room, long requesterSessionId, CancellationToken ct)
        {
            foreach (var seat in room.Seats)
            {
                var session = rooms.Session(seat.SessionId);
                if (session is null || session.Connection.IsClosed) continue;
                var message = texts.Format(session, "pets.start.waiting");
                if (notices is not null)
                    await notices.SendAsync(session, new(message), ct).ConfigureAwait(false);
                else
                    await chat!.SendAsync(session, new(room.RoomId, seat.Slot,
                        texts.Format(session, "pets.system_name"), 1, message), ct).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask GuardAsync<T>(PetRoomMutationPolicy policy, ISession session, T request,
        MessageHandler<T> next, Func<CancellationToken, ValueTask> refuse, CancellationToken ct) where T : IMessage
    {
        ct.ThrowIfCancellationRequested();
        if (!policy.TryBegin(session.Id))
        {
            await refuse(ct).ConfigureAwait(false);
            return;
        }
        try { await next(session, request, ct).ConfigureAwait(false); }
        finally { policy.End(session.Id); }
    }

    private static IMessageSender<T>? Find<T>(IPluginContext context, string protocol) where T : IMessage =>
        context.Services.GetAll<IMessageSender<T>>().SingleOrDefault(sender => sender.Protocol == protocol);

    private static IMessageSender<T> Require<T>(IPluginContext context, string protocol) where T : IMessage =>
        Find<T>(context, protocol) ?? throw new ConfigException($"Pet operations require a {typeof(T).Name} sender for '{protocol}'.");
}
