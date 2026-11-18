using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Emulator policy: starting fighters allowed a prototype draw in Zombie Battle G. Always leave a human.</summary>
    [Range(0, 15)]
    public int ZombieOriginPrototypeCount { get; set; } = 1;
}
