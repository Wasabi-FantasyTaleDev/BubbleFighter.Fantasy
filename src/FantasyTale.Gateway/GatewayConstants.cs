namespace FantasyTale.Gateway;

/// <summary>Names and limits shared by the Fantasy Tale Gateway control plane.</summary>
internal static class GatewayConstants
{
    /// <summary>The current control message schema.</summary>
    public const int ControlSchemaVersion = 1;

    /// <summary>The only capability mounted by this first implementation.</summary>
    public const string BubbleFighterCenterSelector = "bf-center-selector";

    /// <summary>The compatibility byte budget used when the parent omits its launch-sized control limit.</summary>
    public const int MaxControlMessageBytes = 64 * 1024;

    /// <summary>The maximum length of an entry display name.</summary>
    public const int MaxCenterNameChars = 128;

}
