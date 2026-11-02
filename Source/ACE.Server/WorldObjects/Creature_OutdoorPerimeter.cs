using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Open World Threads WP3b: the runtime creature perimeter rules (R11-R13) for creatures placed in an outdoor run.
    /// Every hook here starts with a null check on <see cref="P_OutdoorPerimeter"/>, which is null on every creature that
    /// is not an outdoor run's, so the rest of the game pays one field read per call. The rules themselves are
    /// OutdoorPerimeterRules', reached through the binding; nothing here re-derives a distance rule.
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// The outdoor-run perimeter this creature is held by, stamped by ThreadDungeonSpawner before EnterWorld. Purely
        /// in-memory, never persisted and never networked (like P_DungeonRun). Null for every other creature.
        /// </summary>
        public OutdoorCreaturePerimeter P_OutdoorPerimeter;

        /// <summary>
        /// R11(a) and R11(b), run on the monster tick from CheckMissHome (an awake, non-returning creature):
        ///   (a) past R minus the containment margin, the creature goes home;
        ///   (b) a Player AttackTarget or TauntTarget past R is dropped this tick and the creature retargets among the valid
        ///       candidates (FindNextTarget filters them), going home when there are none. Pets and creatures stay valid.
        /// True when it acted (the caller stops there).
        /// </summary>
        internal bool OutdoorPerimeterTick()
        {
            var perimeter = P_OutdoorPerimeter;
            if (perimeter == null)
                return false;

            if (perimeter.ShouldReturnHome(Location))
            {
                MoveToHome();
                return true;
            }

            if (TauntTarget is Player taunter && !perimeter.IsValidTarget(true, taunter.Location))
                TauntTarget = null;

            if (AttackTarget is Player target && !perimeter.IsValidTarget(true, target.Location))
            {
                AttackTarget = null;

                if (!FindNextTarget() && MonsterState != State.Return)
                    MoveToHome();

                return true;
            }

            return false;
        }

        /// <summary>
        /// The candidate filter FindNextTarget applies after the tether filter (R11b, R13). A Player past R is never
        /// kept; an interior-spawned creature needs direct line of sight to take a NEW target. Its current AttackTarget
        /// is exempt from the line-of-sight test (never from the past-R test). TauntTarget is NOT exempt: a taunt
        /// breaks when line of sight is lost, which FindNextTarget's own taunt branch also requires.
        /// Returns the input list unchanged (same reference, no allocation) for a creature with no perimeter.
        /// </summary>
        public List<Creature> FilterOutdoorPerimeterTargets(List<Creature> targets)
        {
            var perimeter = P_OutdoorPerimeter;
            if (perimeter == null || targets == null)
                return targets;

            var attackTarget = AttackTarget;

            return FilterCandidates(perimeter, targets, t => ReferenceEquals(t, attackTarget), t => IsDirectVisible(t));
        }

        /// <summary>The filter itself, pure over its two probes so a test can drive line of sight.</summary>
        internal static List<Creature> FilterCandidates(OutdoorCreaturePerimeter perimeter, List<Creature> targets, Func<Creature, bool> isHeld,
            Func<Creature, bool> isVisible)
        {
            var filtered = new List<Creature>(targets.Count);

            foreach (var target in targets)
            {
                if (target != null && perimeter.AcceptsCandidate(target is Player, target.Location, isHeld(target), () => isVisible(target)))
                    filtered.Add(target);
            }

            return filtered;
        }

        /// <summary>Timers.RunningTime of this creature's last alert-path line-of-sight check (F2 throttle). Runtime only.</summary>
        private double outdoorAlertLosCheckedAt = double.NegativeInfinity;

        /// <summary>
        /// AlertMonster's acquisition gate (R11b, R13): a player past R never wakes this creature, and an interior-spawned
        /// one wakes only for a target in direct line of sight, checked at most once per
        /// OutdoorCreaturePerimeter.AlertLosIntervalS (a ray cast per movement packet otherwise). True with no perimeter.
        /// </summary>
        public bool AcceptsOutdoorPerimeterAlert(Creature alerter)
        {
            var perimeter = P_OutdoorPerimeter;
            if (perimeter == null || alerter == null)
                return true;

            return perimeter.AcceptsAlert(alerter is Player, alerter.Location, Timers.RunningTime, ref outdoorAlertLosCheckedAt,
                () => IsDirectVisible(alerter));
        }

        /// <summary>
        /// The player answerable for <paramref name="source"/>'s damage, and the position R12 judges: a Player at its own
        /// position; a player-owned Pet at the PET's position (a pet inside the circle may fight while its owner stands
        /// outside, and a pet outside deals nothing). Null for every other source.
        /// </summary>
        internal static Player ResponsiblePlayer(WorldObject source, out Position judgedAt)
        {
            judgedAt = null;

            if (source is Player player)
            {
                judgedAt = player.Location;
                return player;
            }

            if (source is Pet pet && pet.P_PetOwner != null)
            {
                judgedAt = pet.Location;
                return pet.P_PetOwner;
            }

            return null;
        }

        /// <summary>
        /// R12 at the damage-intake choke points (TakeDamage, the DoT tick loop's per-caster contributions, and a spell
        /// projectile's direct vital write): true when <paramref name="source"/> is a player, or a player's pet, standing
        /// past R. The refusal line goes to the answerable player through its own 5 s throttle, so DoT ticks cannot spam
        /// it. False for a creature with no perimeter and for every non-player source.
        /// </summary>
        public bool RefusesOutdoorPerimeterDamage(WorldObject source)
        {
            var perimeter = P_OutdoorPerimeter;
            if (perimeter == null || source == null)
                return false;

            var player = ResponsiblePlayer(source, out var at);
            if (player == null || !perimeter.ShouldRefuseDamage(true, at))
                return false;

            player.NotifyOutdoorStrikeRefused();
            return true;
        }

        /// <summary>
        /// R12 for a non-projectile spell (WorldObject.HandleCastSpell): true when a HARMFUL spell would land on a run
        /// creature from <paramref name="caster"/>, a player or a player's pet, standing past R. The pet is judged at its
        /// own position, as at every other intake. A beneficial spell, a target with no perimeter, and a non-player
        /// caster are never refused.
        /// </summary>
        public static bool RefusesOutdoorPerimeterSpell(Creature target, WorldObject caster, bool harmful)
        {
            return harmful && target?.P_OutdoorPerimeter != null && target.RefusesOutdoorPerimeterDamage(caster);
        }

        /// <summary>
        /// R11(c): a run creature that has walked home regains full health on arrival. Called from Sleep() when the
        /// creature was in State.Return, which is how every return-home leg ends.
        /// </summary>
        internal void OnOutdoorPerimeterHomeArrival()
        {
            if (P_OutdoorPerimeter == null || IsDead)
                return;

            var missing = (int)Health.MaxValue - (int)Health.Current;
            if (missing <= 0)
                return;

            var healed = UpdateVitalDelta(Health, missing);
            if (healed > 0)
                DamageHistory.OnHeal((uint)healed);
        }
    }
}
