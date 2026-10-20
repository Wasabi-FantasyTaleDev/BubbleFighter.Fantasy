using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.Centers;

/// <summary>Supplies named native center entries while all entries share the mounted game services.</summary>
[Plugin("centers", Description = "Native center selector metadata; shared backend routing.",
    Requires = [typeof(IMessageRouter)],
    OptionalMany = [typeof(IInboundMessageBinding<CenterStatusRequested>), typeof(IInboundMessageBinding<CenterAvailabilityRequested>),
        typeof(IMessageSender<CenterStatusReplied>), typeof(IMessageSender<CenterAvailabilityReplied>)])]
public sealed class CentersPlugin : IPlugin
{
    private const string ConnectionEntry = "centers.entry";

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<CentersOptions>();
        var status = Sender<CenterStatusReplied>(context, options.Protocol);
        var availability = Sender<CenterAvailabilityReplied>(context, options.Protocol);
        var prefix = context.Row.Id.EndsWith("centers", StringComparison.Ordinal) ? context.Row.Id[..^7] : "";
        var entries = options.Entries.ToDictionary(entry => prefix + entry.ListenerId, StringComparer.Ordinal);
        context.Effect(context.Events.On<ConnectionAcceptedEvent>(e =>
        {
            if (entries.TryGetValue(e.ListenerId, out var entry)) e.Connection.Items[ConnectionEntry] = entry;
        }));
        var messages = context.Services.Get<IMessageRouter>().For(context);
        context.Effect(messages.On<CenterStatusRequested>(options.Protocol, (session, _, ct) =>
        {
            if (!session.Connection.Items.TryGetValue(ConnectionEntry, out var value) || value is not CenterEntryOptions entry)
                throw new InvalidOperationException("Center status request arrived on an unconfigured listener.");
            return status.SendAsync(session, new(entry.Id, entry.Name, entry.Users, entry.Capacity), ct);
        }));
        // TODO: separate center routing and population tracking; these entries share the same backend.
        context.Effect(messages.On<CenterAvailabilityRequested>(options.Protocol,
            (session, _, ct) => availability.SendAsync(session, new(false), ct)));
        return ValueTask.CompletedTask;
    }

    private static IMessageSender<T> Sender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        return senders.Length == 1 ? senders[0] : throw new ConfigException($"Centers requires exactly one sender for {typeof(T).Name} on {protocol}.");
    }
}

/// <summary>Configures the bounded center directory for one client protocol.</summary>
public sealed class CentersOptions : IValidatableOptions
{
    /// <summary>Gets or sets the protocol used by every listener in this directory.</summary>
    [Required] public string Protocol { get; set; } = "";
    /// <summary>Gets or sets the native entries, each identified by its accepting listener row.</summary>
    public CenterEntryOptions[] Entries { get; set; } = [];
    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (Entries.Length is < 1 or > 32 || Entries.Any(entry => entry.Id < 0 || string.IsNullOrWhiteSpace(entry.Name)
            || entry.Name.Length > 128 || string.IsNullOrWhiteSpace(entry.ListenerId) || entry.Users < 0 || entry.Capacity < 1
            || entry.Users > entry.Capacity) || Entries.Select(entry => entry.Id).Distinct().Count() != Entries.Length
            || Entries.Select(entry => entry.ListenerId).Distinct(StringComparer.Ordinal).Count() != Entries.Length)
            yield return new ValidationResult("Center entries require 1..32 unique ids and listeners, bounded names and valid occupancies.", [nameof(Entries)]);
    }
}

/// <summary>One configured native selector entry tied to a listener in the current profile.</summary>
public sealed class CenterEntryOptions
{
    /// <summary>Gets or sets the center identity stored by the native selection window.</summary>
    public int Id { get; set; }
    /// <summary>Gets or sets the center name where the client supports names in its status reply.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the accepting listener row id, before all-profile namespacing.</summary>
    public string ListenerId { get; set; } = "";
    /// <summary>Gets or sets the displayed occupancy until live center population tracking is implemented.</summary>
    public int Users { get; set; }
    /// <summary>Gets or sets the displayed capacity used by the native availability indicator.</summary>
    public int Capacity { get; set; } = 512;
}
