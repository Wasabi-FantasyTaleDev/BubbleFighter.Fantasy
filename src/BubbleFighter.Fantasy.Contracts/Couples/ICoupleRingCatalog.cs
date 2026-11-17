namespace BubbleFighter.Fantasy.Contracts.Couples;

/// <summary>Resolves couple invitation items to the display rings defined by the selected client's workbook.</summary>
public interface ICoupleRingCatalog
{
    /// <summary>Recognizes a native invitation and returns its display ring, or zero for an explicitly documented absent stock; unrelated items return false.</summary>
    bool TryGetDisplayRing(int invitationItemId, out int displayRingItemId);
}
