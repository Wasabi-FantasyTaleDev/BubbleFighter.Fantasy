using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.Dialect.Messages;

internal sealed partial class SceneSeedAuthority
{
    /// <inheritdoc />
    public MatchCriticalSeedUpdate WithPublishedEquipment(MatchCriticalSeedUpdate current, MatchSeedEquipment equipment)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(equipment);
        if (current.Equipment.ModeId != equipment.ModeId)
            throw new InvalidOperationException("A local equipment view cannot change the running round mode.");
        var catalog = RequiredWeapons();
        if (!catalog.ViewRefreshesWeapons && current.Equipment.Equals(equipment)) return current;
        // cn-324 sub_8084A0 preserves installed slots; cn-406 sub_871590 rebuilds them under its native feature gate.
        var installed = catalog.ViewRefreshesWeapons ? catalog.EffectiveSpecifications(equipment)
            : catalog.EffectiveSpecifications(current.Equipment,
                current.EffectivePrimarySpecification, current.EffectiveSidearmSpecification);
        var candidate = current with { Equipment = equipment, EffectivePrimarySpecification = installed.Primary,
            EffectiveSidearmSpecification = installed.Sidearm };
        _ = catalog.Contributions(equipment, installed.Primary, installed.Sidearm, candidate.TimedEffectActive);
        return candidate;
    }

    private sealed class CriticalRevision(MatchCriticalSeedUpdate request)
    {
        internal MatchCriticalSeedUpdate Request { get; } = request;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <inheritdoc />
    public int TimedEffectDuration(MatchSeedEquipment equipment)
    {
        var attributes = RequiredWeapons().Attributes(equipment);
        return attributes.TimedEligible ? attributes.DurationMs : 0;
    }

    /// <inheritdoc />
    public void Validate(MatchCriticalSeedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.RoomId <= 0 || update.Generation <= 0 || update.Revision <= 0)
            throw new InvalidOperationException("Critical revision requires a positive room, generation and revision.");
        _ = RequiredWeapons().Contributions(update.Equipment, update.EffectivePrimarySpecification,
            update.EffectiveSidearmSpecification, update.TimedEffectActive);
    }

    /// <summary>Publishes only a changed critical probability; duplicate input revisions retain their original result.</summary>
    public async ValueTask SendAsync(ISession session, MatchCriticalSeedUpdate message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.Profile.Id != build)
            throw new InvalidOperationException("Critical revision belongs to another client profile.");
        Validate(message);
        var contribution = RequiredWeapons().Contributions(message.Equipment, message.EffectivePrimarySpecification,
            message.EffectiveSidearmSpecification, message.TimedEffectActive);
        Issuance issuance;
        CriticalRevision revision;
        (double Primary, double Sidearm) probabilities;
        var ownsRevision = false;
        var changed = false;
        lock (_gate)
        {
            if (!_issued.TryGetValue(session.Id, out issuance!) || issuance.Request.RoomId != message.RoomId
                || issuance.Request.Generation != message.Generation || issuance.Request.EntityId != message.EntityId)
                throw new InvalidOperationException("Critical revision has no matching opening issuance for this relay session.");
            if (!issuance.Completion.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("Critical revision requires a successful opening issuance.");
            var baseline = Chances.RoundStartChance(issuance.Request.PreviousRound);
            probabilities = (Math.Clamp(baseline + contribution.Primary, 0, 1),
                Math.Clamp(baseline + contribution.Sidearm, 0, 1));
            if (issuance.Revision is { } previous && previous.Request == message)
                revision = previous;
            else
            {
                if (issuance.Revision is { } pending && !pending.Completion.Task.IsCompletedSuccessfully)
                    throw new InvalidOperationException("A pending or failed critical revision cannot be superseded.");
                if (message.Revision != checked((issuance.Revision?.Request.Revision ?? 0) + 1))
                    throw new InvalidOperationException("Critical revisions must be consecutive within their opening issuance.");
                revision = new(message);
                issuance.Revision = revision;
                ownsRevision = true;
                changed = issuance.LastChances != probabilities;
            }
        }
        if (ownsRevision)
        {
            try
            {
                if (changed)
                {
                    var payload = Critical(message.EntityId, probabilities.Primary, probabilities.Sidearm);
                    ct.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (!_issued.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, issuance)
                            || !ReferenceEquals(current.Revision, revision))
                            throw new InvalidOperationException("Critical revision lost its relay-session issuance.");
                    }
                    // cn-324 sub_460820 / cn-406 sub_422F60 install a critical table independently of the other families.
                    await session.SendAsync(CriticalOpcode, payload, ct).ConfigureAwait(false);
                }
                lock (_gate) issuance.LastChances = probabilities;
                revision.Completion.TrySetResult();
            }
            catch (OperationCanceledException failure)
            {
                revision.Completion.TrySetCanceled(failure.CancellationToken);
            }
            catch (Exception failure)
            {
                revision.Completion.TrySetException(failure);
            }
        }
        // Installation resets every slot cursor (cn-324 sub_4A91A0); equal probabilities need no new table.
        await revision.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private WeaponCriticalCatalog RequiredWeapons() => weapons
        ?? throw new InvalidOperationException("Live critical revisions require the build's audited weapon catalog.");
}
