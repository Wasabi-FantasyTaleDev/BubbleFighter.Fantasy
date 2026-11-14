namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>
/// Asked, with <see cref="IEventBus.Bail{TEvent, TResult}"/>, before an idle activation group is unloaded. A row of
/// the group that still holds live state (open rooms, a running round, a transfer in progress) answers with a short
/// description such as <c>2 room(s)</c> and the group stays loaded; rows with nothing to keep answer
/// <see langword="null"/>. This way "no connection" is checked against the rows' own state, not assumed.
/// </summary>
/// <param name="Group">The group about to be unloaded: the <see cref="PluginRow.ActivationGroup"/> of its rows.</param>
public sealed record ActivationGroupIdleQuery(string Group) : IEvent;
