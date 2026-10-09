using System;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The immutable in-memory tag a defender crystal carries (Docs/Pvp/ATTACK-DEFEND.md "Crystals", "Match tag"): which match owns
    /// it, its index in the planned crystal list (0 .. N-1) and the team that defends it. Set once before the crystal enters the world
    /// and never saved: the crystal exists only in its match's own copy.
    /// </summary>
    public sealed record BattlegroundObjectiveTag(Guid MatchId, int Index, int DefendingTeam);

    /// <summary>Why <see cref="BattlegroundObjectiveGate"/> allowed or refused an action against an objective.</summary>
    public enum ObjectiveGateReason
    {
        /// <summary>The action may proceed.</summary>
        Allowed,

        /// <summary>The target carries no objective tag, so this gate does not apply to it.</summary>
        NoTag,

        /// <summary>The actor is not bound to any match.</summary>
        ActorUnbound,

        /// <summary>The actor is bound to a different match than the objective's.</summary>
        OtherMatch,

        /// <summary>The actor's match is not Live.</summary>
        MatchNotLive,

        /// <summary>The actor is dead or waiting in the pen.</summary>
        ActorRespawning,

        /// <summary>The actor is on the defending team.</summary>
        OwnTeam,

        /// <summary>The action is not harmful (a heal, a buff, a beneficial spell).</summary>
        NotHarmful,

        /// <summary>
        /// Sequential crystals: an earlier crystal still stands, so this one is sealed (Docs/Pvp/ATTACK-DEFEND.md "Sequential crystals").
        /// Returned only to an attacker making a harmful action, the one case that would otherwise have been allowed.
        /// </summary>
        CrystalSealed
    }

    /// <summary>
    /// The pure permission decision for acting on a defender crystal (Docs/Pvp/ATTACK-DEFEND.md "Who may damage it"). Allows ONLY a
    /// harmful action by a player bound to the objective's own match, Live and not respawning, on a team other than the defending
    /// one, against a crystal that is vulnerable (always, or in sequential mode only the current one); everything else, beneficial
    /// actions included, is refused with a reason the caller turns into text. Reads no clock, no world state and no setting.
    /// </summary>
    public static class BattlegroundObjectiveGate
    {
        public static ObjectiveGateReason Evaluate(BattlegroundObjectiveTag tag, PvpPlayerBinding actor, bool harmful)
        {
            if (tag == null)
                return ObjectiveGateReason.NoTag;

            if (actor == null || actor.Match == null)
                return ObjectiveGateReason.ActorUnbound;

            if (actor.Match.MatchId != tag.MatchId)
                return ObjectiveGateReason.OtherMatch;

            if (actor.State != PvpMatchState.Live)
                return ObjectiveGateReason.MatchNotLive;

            if (actor.Respawning)
                return ObjectiveGateReason.ActorRespawning;

            if (actor.TeamIndex == tag.DefendingTeam)
                return ObjectiveGateReason.OwnTeam;

            if (!harmful)
                return ObjectiveGateReason.NotHarmful;

            // Sequential crystals: only the match's current crystal takes damage. The sequence hangs off the match the actor is bound to
            // (already shown above to be the objective's own match) and is read lock-free; null is any-order play.
            var sequence = actor.Match.CrystalSequence;

            if (sequence != null && !sequence.IsVulnerable(tag.Index))
                return ObjectiveGateReason.CrystalSealed;

            return ObjectiveGateReason.Allowed;
        }

        /// <summary>True when <paramref name="reason"/> lets the action proceed.</summary>
        public static bool IsAllowed(ObjectiveGateReason reason) => reason == ObjectiveGateReason.Allowed;
    }
}
