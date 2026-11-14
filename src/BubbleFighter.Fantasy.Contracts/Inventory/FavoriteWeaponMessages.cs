using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Tells the owner that a weapon's Favorite grade changed after a committed award, shown as a chat line. It is only a notice:
/// the client never reads points or grade from it, and the bar follows the next inventory refresh (kr-1256 sub_9D0E60).
/// </summary>
/// <param name="ItemId">The weapon item the notice names.</param>
/// <param name="OldGrade">The grade before the award, zero for Normal through three for Master.</param>
/// <param name="NewGrade">The grade after the award, using the same numbering.</param>
public sealed record FavoriteWeaponGradeChanged(int ItemId, int OldGrade, int NewGrade) : IMessage;