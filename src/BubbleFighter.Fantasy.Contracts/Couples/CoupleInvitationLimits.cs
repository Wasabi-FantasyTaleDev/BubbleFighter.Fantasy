namespace BubbleFighter.Fantasy.Contracts.Couples;

/// <summary>Bounds the transient invitations and duplicate-answer receipts of a durable couple provider.</summary>
public sealed class CoupleInvitationLimits
{
    /// <summary>Gets or sets the maximum pending invitations across this provider instance.</summary>
    public int MaxPendingInvitations { get; set; } = 1024;

    /// <summary>Gets or sets the maximum pending invitations involving one account.</summary>
    public int MaxPendingPerAccount { get; set; } = 16;

    /// <summary>Gets or sets the maximum retained completed invitation outcomes.</summary>
    public int MaxCompletedInvitations { get; set; } = 1024;

    /// <summary>Gets or sets the maximum invitation text length in UTF-16 code units.</summary>
    public int MaxMessageLength { get; set; } = 512;

    /// <summary>Rejects bounds that cannot be enforced without excessive per-process retention.</summary>
    public void Validate()
    {
        if (MaxPendingInvitations is < 1 or > 65536 || MaxPendingPerAccount is < 1 or > 1024
            || MaxCompletedInvitations is < 1 or > 65536 || MaxMessageLength is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(CoupleInvitationLimits), "Invalid couple invitation limits.");
    }
}
