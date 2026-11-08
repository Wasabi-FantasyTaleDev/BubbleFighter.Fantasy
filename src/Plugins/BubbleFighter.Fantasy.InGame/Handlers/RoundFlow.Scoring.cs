using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    // kr-812 sub_520A20 confirms credit; kr-1256 sub_6FAC30 consumes bubble contact.
    // Direct deaths use kr-1256 sub_6F8280, cn-774 sub_8B9A20 and tw-454 sub_72D8F0.
    private enum KillEvidence { ScoreReport, BubbleContact, DirectDeath }

    private readonly record struct KillClaim(int? ReporterSlot, int? VictimSlot, KillEvidence Evidence,
        bool HasVictimReference = true, bool InitiatorNamed = true);

    // Called under the room gate after the input adapter checks sender ownership and mode admission. All native
    // evidence paths share victim attribution, personal deaths/kills, team totals and the live scoreboard projection.
    private (RoundScoreUpdated? Update, LifeCheck? Ignored, int? Victim) CreditKill(PlayRoom room, KillClaim claim)
    {
        var reporter = claim.ReporterSlot is { } scorer ? ScoreMember(room, scorer) : null;
        var victim = claim.VictimSlot;
        if (claim.Evidence != KillEvidence.ScoreReport)
        {
            var target = victim is { } targetSlot ? LifeActor(room, RoomRegistry.SceneEntity(targetSlot)) : null;
            if (claim.Evidence == KillEvidence.BubbleContact && reporter is null && claim.InitiatorNamed && target is { Life.State: SeatLife.Bubble }
                && GameMode.Resolve(room.ModeId).Family == GameModeFamily.Horde)
            {
                target.Life.Burst();
                _logger.LogInformation("Room {Room} slot {Victim} was popped by an NPC.", room.Id, target.Slot);
                return (null, null, victim);
            }
            LifeCheck? ignored = target is null ? LifeCheck.PopWithoutVictim
                : reporter is null ? LifeCheck.PopWithoutInitiator
                : reporter.Slot == target.Slot ? LifeCheck.PopOfSelf
                : target.Life.State != (claim.Evidence == KillEvidence.BubbleContact ? SeatLife.Bubble : SeatLife.Dead)
                    ? LifeCheck.PopOfUnbubbled
                : !AreOpponents(room, reporter.Team, target.Team)
                    ? LifeCheck.PopWithoutOpposingTeams
                : null;
            if (ignored is not null)
                return (null, ignored, victim);
            if (claim.Evidence == KillEvidence.BubbleContact) target!.Life.Burst();
            if (!target!.Life.CreditDeath()) return (null, null, victim);
        }
        else if (reporter is not null)
        {
            var paired = room.Scoring.TakeVictim(reporter.Slot);
            victim = claim.HasVictimReference ? victim : paired;
            // cn-324 sub_476B90 establishes terminal king evidence; kr-812 sub_430640 names a Captain fall.
            // Both use the shared per-life credit, including duplicate reports from kr-812 sub_520A20.
            if (_options.ZombiePvpModeIds.Contains(room.ModeId) || IsCaptainMode(room) || IsFinalZoneMode(room))
            {
                if (victim is not { } lifeVictim
                    || LifeActor(room, RoomRegistry.SceneEntity(lifeVictim)) is not { } target)
                    return (null, LifeCheck.PopWithoutVictim, victim);
                if (reporter.Slot == target.Slot)
                    return (null, LifeCheck.PopOfSelf, victim);
                if (!AreOpponents(room, reporter.Team, target.Team))
                    return (null, LifeCheck.PopWithoutOpposingTeams, victim);
                target.Life.Die();
                target.Life.Burst();
                if (!target.Life.CreditDeath()) return (null, null, victim);
            }
        }

        if (reporter is null)
            return (null, null, victim);
        var seatedVictim = victim is { } slot && slot != reporter.Slot && ScoreMember(room, slot) is not null ? victim : null;
        if (room.Scoring.AddKill(reporter.Slot, reporter.Team, seatedVictim) is not { } stats)
            return (null, null, victim);
        return (new RoundScoreUpdated(room.Id, reporter.Team, [.. room.Scoring.TeamTotals],
            seatedVictim is { } died ? [stats, room.Scoring.Stats(died)] : [stats], LiveScoreboard(room)), null, victim);
    }

    private bool IsScoringTeam(PlayRoom room, int team) => IsFinalZoneMode(room)
        || (_options.ZombiePvpModeIds.Contains(room.ModeId)
            ? team is RoomFlow.ZombiePvpHumanTeam or RoomFlow.ZombiePvpZombieTeam : team is 1 or 2);

    private bool AreOpponents(PlayRoom room, int first, int second) => IsFinalZoneMode(room)
        || first != second && IsScoringTeam(room, first) && IsScoringTeam(room, second);

    // kr-1256 sub_6F8280, cn-774 sub_8B7CA0 and tw-454 sub_72BEE0 identify the source and victim.
    // Native life evidence and confirmed score reports share counters; release routes name their rescuer in build data.
    private RoundScoreUpdated? CreditAction(PlayRoom room, int? reporterSlot, int? victimSlot, bool rescue, bool lifeEvidence)
    {
        if (reporterSlot is not { } slot || ScoreMember(room, slot) is not { } reporter)
            return null;
        var victim = victimSlot is { } targetSlot ? ScoreMember(room, targetSlot) : null;
        if (rescue && (victim is null || victim.Slot == slot || victim.Team != reporter.Team))
            return null;
        if (lifeEvidence)
        {
            if (victim is null || victim.Slot == slot || !IsScoringTeam(room, reporter.Team) || !IsScoringTeam(room, victim.Team)
                || (!rescue && !AreOpponents(room, reporter.Team, victim.Team)))
                return null;
            var life = LifeActor(room, RoomRegistry.SceneEntity(victim.Slot))!.Life;
            if (!(rescue ? life.CreditRescue() : life.CreditKnockdown())) return null;
        }
        if (!rescue && victimSlot is { } pending)
            room.Scoring.RememberVictim(slot, pending);
        var stats = rescue ? room.Scoring.AddRescue(slot) : room.Scoring.AddKnockdown(slot);
        return stats is null ? null : new RoundScoreUpdated(room.Id, reporter.Team, [.. room.Scoring.TeamTotals], [stats], LiveScoreboard(room));
    }
}
