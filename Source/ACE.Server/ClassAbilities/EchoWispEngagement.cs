using System;

using ACE.Entity.Enum;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Whether a monster is in an Echo Cast wisp's caster's fight, so the wisp may move on to it (design "Targeting",
    /// rule 3, owner ruling 2026-11-23). WISP ONLY: Hunter's Echo, combat pets and the Tank's taunt pulse keep the
    /// narrower PetEngagement rule, which this calls and never changes.
    ///
    /// ENGAGED = any of:
    ///  - <see cref="PetEngagement.IsEngagedWithOwner(Creature, Player)"/>: the monster is attacking the caster or one of
    ///    the caster's pets (plus that rule's never-attacks exception);
    ///  - the monster is attacking a member of the caster's fellowship, or a pet a member owns;
    ///  - the caster has damaged the monster (the caster is in its DamageHistory).
    /// A monster nobody is fighting stays off-limits unless /summonaggro is on (the controller's switch). Players are
    /// never targets (the controller's cheap checks). The wisp's held or starting target is never put through this.
    ///
    /// Why the widening: on stage (2026-11-23) a wisp beside a World Event fight never cast, because the monsters near
    /// it were attacking the caster's fellow, and the old rule only counted the caster and its own pets.
    ///
    /// THREADS. Runs on the caster's action queue (EchoWisp.CastOnOwnerThread). The fellowship test reads two
    /// Player.Fellowship references and compares them (single reference reads, atomic in .NET; no member list is
    /// built, no lock taken, nothing allocated). The damage test is the existing DamageHistory.HasDamager, one
    /// dictionary lookup by guid with no allocation. That dictionary is written by whichever thread lands a hit on the
    /// monster, as it already is in ACE, so the lookup is guarded: a read that collides with a write (an
    /// InvalidOperationException or IndexOutOfRangeException from the unsynchronized Dictionary) is treated as "not
    /// damaged", and the next cast asks again.
    /// </summary>
    public static class EchoWispEngagement
    {
        public static bool IsEngaged(Creature monster, Player caster)
        {
            if (monster == null || caster == null)
                return false;

            // the caster, the caster's pets, and the never-attacks exception: the shared rule, unchanged
            if (PetEngagement.IsEngagedWithOwner(monster, caster))
                return true;

            // read once: a second read could observe a different target
            var target = monster.AttackTarget;

            if (target != null && IsFellowSide(target, caster))
                return true;

            return WasDamagedBy(monster, caster);
        }

        /// <summary>
        /// Whether <paramref name="target"/> is a member of <paramref name="caster"/>'s fellowship (never the caster itself:
        /// that is PetEngagement's arm) or a pet one of them owns. False when the caster is in no fellowship.
        /// </summary>
        internal static bool IsFellowSide(WorldObject target, Player caster)
        {
            var fellowship = caster?.Fellowship;

            if (fellowship == null)
                return false;

            var side = target as Player ?? (target as Pet)?.P_PetOwner;

            return side != null && !ReferenceEquals(side, caster) && ReferenceEquals(side.Fellowship, fellowship);
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> is a legal Restore (Heal, Revitalize) wisp target by side (owner, 2026-11-23):
        /// the caster itself, or a player in the caster's fellowship (the two Player.Fellowship references compared, as
        /// <see cref="IsFellowSide"/> does). Never a pet or a monster, and false for everyone else when the caster is in
        /// no fellowship.
        /// </summary>
        internal static bool IsCasterOrFellow(Player candidate, Player caster)
        {
            if (candidate == null || caster == null)
                return false;

            if (ReferenceEquals(candidate, caster))
                return true;

            var fellowship = caster.Fellowship;

            return fellowship != null && ReferenceEquals(candidate.Fellowship, fellowship);
        }

        /// <summary>
        /// Whether a Restore wisp may heal <paramref name="candidate"/> by PvP state (<see cref="EchoWispMath.IsPvpSafeRestoreTarget"/>,
        /// class abilities never reach PvP): the caster always; anyone else only while NPK (PlayerKillerStatus), with no
        /// PKTimerActive, not IsInPvpMatch, and not ClassAbilitySuppressed (the PK facet rule or an arena mask). The SAME
        /// predicate for the pick (EchoWisp.RestoreVerdict), the trigger (Player.TryTriggerEchoWisp) and the cast guard
        /// (Player.CastAsEchoWisp), so they cannot disagree. The caster's own state is never read: a suppressed caster's
        /// wisps end ("suppressed", EchoWispMath.EndReason).
        /// </summary>
        internal static bool IsPvpSafeRestoreTarget(Player candidate, Player caster)
        {
            if (candidate == null)
                return false;

            if (ReferenceEquals(candidate, caster))
                return true;

            return EchoWispMath.IsPvpSafeRestoreTarget(false, candidate.PlayerKillerStatus == PlayerKillerStatus.NPK, candidate.PKTimerActive,
                candidate.IsInPvpMatch, candidate.ClassAbilitySuppressed);
        }

        /// <summary>Whether <paramref name="caster"/> appears in <paramref name="monster"/>'s damage history.</summary>
        internal static bool WasDamagedBy(Creature monster, Player caster)
        {
            var history = monster?.DamageHistory;

            if (history == null || caster == null)
                return false;

            try
            {
                return history.HasDamager(caster);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IndexOutOfRangeException)
            {
                // a lookup that raced a write on any attacker's thread: not damaged this time, asked again next cast
                return false;
            }
        }
    }
}
