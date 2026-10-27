using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.RoomCore;

namespace BubbleFighter.Fantasy.InGame.Tests;

internal static class RoomWireSupport
{
    internal static MapMetaBlob TrainingMap() => MapMetaBlob.Empty() with
    {
        Unknown4 = 40,
        Unknown44 = "training",
    };

    /// <summary>
    /// The waiting-room setting as the live client sends it: <c>kind</c> 1,
    /// which the registry normalizes to the room-stage gate through
    /// <see cref="SessionSettingBlob.ForRoom"/>. Tests that build a room
    /// through that normalization path use this.
    ///
    /// </summary>
    internal static SessionSettingBlob WaitingSetting() => new(
        0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
        Kind: 1,
        TrainingMap(),
        0, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0);

    /// <summary>
    /// A waiting-room setting whose <c>MapMeta</c> carries a RESOLVED map key in the <c>unknown68</c>
    /// slot, which is the slot the S2C 510 stage enter echoes: <c>unknown44</c> may still hold a <c>Random*</c>
    /// marker that only the client can resolve.
    /// </summary>
    internal static SessionSettingBlob WaitingSettingWithMap() => new(
        0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
        Kind: SessionSettingBlob.MainMenuKind,
        TrainingMap() with { Unknown68 = "rel_Village00_boomhill", Unknown116 = "Boomhill" },
        SessionSettingBlob.RoomStateInRoom, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0);

    /// <summary>
    /// A waiting-room setting with a RESOLVED map key AND a non-training mode, so
    /// the room is an ordinary created room rather than the training ground. The
    /// stage enter belongs to ordinary rooms only - the training ground sends its
    /// own from the create path.
    ///
    /// </summary>
    internal static SessionSettingBlob OrdinarySettingWithMap() => new(
        0, 0, string.Empty, 0, 0, 0, 0, string.Empty,
        Kind: SessionSettingBlob.MainMenuKind,
        TrainingMap() with
        {
            Unknown4 = 0,
            Unknown68 = "rel_Village00_boomhill",
            Unknown116 = "Boomhill",
        },
        SessionSettingBlob.RoomStateInRoom, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0);

    /// <summary>
    /// A C2S 503 / 107 style rule record with the create-time values the
    /// client forces for zombie modes (EndGameTimeOver 180000, DeathMatchKill
    /// 20, DeathMatchTime 300000, TimeAttackTime 420000).
    ///
    /// </summary>
    internal static RoomRuleRecord SampleRules() => new(
        180000, 0, 0, 0, 0, 0, 20, 300000, 420000, 0, 0, 0, 0, 0);
}
