using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Settings;

/// <summary>Replaces the authenticated player's customized controls, or removes overrides when defaults are requested.</summary>
/// <param name="UseDefaults">True only when the native client has no nondefault controls.</param>
/// <param name="Mappings">The complete ordered set of nondefault controls; empty when defaults are requested.</param>
public sealed record CustomizedKeysUpdated(bool UseDefaults, IReadOnlyList<CustomizedKey> Mappings) : IMessage;

/// <summary>Restores a player's saved controls after the login scene is installed.</summary>
/// <param name="ApplyMappings">Whether the client should apply the supplied overrides, independent of the update's defaults flag.</param>
/// <param name="Mappings">The complete saved override list, or an empty list when no overrides are saved.</param>
public sealed record CustomizedKeysRestored(bool ApplyMappings, IReadOnlyList<CustomizedKey> Mappings) : IMessage;
