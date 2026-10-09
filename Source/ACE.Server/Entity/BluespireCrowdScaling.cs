using System;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Bluespire ladder rung 6: a creature marked with PropertyBool 9063 BluespireCrowdScaled gets more
    /// health and more damage the more players are actually in the dungeon with it.
    ///
    /// SCOPED BY LANDBLOCK INSTANCE, NEVER BY RADIUS. A dungeon is the unit here - everyone inside it is in
    /// the fight whether or not they are standing in the same room - and a realm copy of a landblock is a
    /// different place with the same geometry, so the instance is what distinguishes one group's run from
    /// another's. That is the one deliberate departure from WorldEventAudienceSampler, which is anchored on
    /// an outdoor point and therefore has to use a radius; the player FILTER is that sampler's, unchanged.
    ///
    /// RESOLVED AT EACH CREATURE'S OWN SPAWN MOMENT, which is what "the same pattern as the world-event
    /// scaling" means: WorldEvent re-resolves the crowd multiplier per wave, at spawn, rather than tracking
    /// it. The accepted consequence is that a creature spawned into an empty room keeps its empty-room stats
    /// until it dies. This faithfully reproduces the world-event behaviour and is a ratified accepted risk.
    ///
    /// DELIBERATELY NOT THE THREADS GROUP-SCALING SHAPE, which locks its roster at run start. Those are the
    /// wrong semantics for an open dungeon anyone may walk into.
    /// </summary>
    public static class BluespireCrowdScaling
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Applies crowd scaling to one creature, if it opted in. Called from WorldObject.EnterWorld BEFORE
        /// the object reaches a landblock, so the writes land while nothing can see a half-scaled creature.
        ///
        /// The whole cost on every other creature spawn in the game is the PropertyBool read on the first
        /// line. Never throws: a creature that could not be scaled is still a creature.
        ///
        /// The caller excludes Player before calling (Player derives from Creature, and EnterWorld is on the
        /// login and teleport paths too). This method does not re-test it: one owner for that rule is
        /// better than two that can drift, and the call site is where the cost of the test is paid.
        /// </summary>
        public static void TryApply(Creature creature)
        {
            if (creature == null || creature.GetProperty(PropertyBool.BluespireCrowdScaled) != true)
                return;

            try
            {
                Apply(creature);
            }
            catch (Exception ex)
            {
                log.Error($"[BLUESPIRE] crowd scaling threw for wcid {creature.WeenieClassId} (0x{creature.Guid.Full:X8})", ex);
            }
        }

        private static void Apply(Creature creature)
        {
            var location = creature.Location;

            if (location == null)
                return;

            var players = CountInInstance(location.Instance);

            var startAt = (int)Math.Clamp(PropertyManager.GetLong("bluespire_ladder_d6_crowd_start_at").Item, 0, int.MaxValue);
            var perPlayer = PropertyManager.GetDouble("bluespire_ladder_d6_crowd_per_player").Item;
            var healthCap = PropertyManager.GetDouble("bluespire_ladder_d6_crowd_health_cap").Item;

            // CrowdHealthDef.Resolve UNCHANGED - the self-sanitising {startAt, perParticipant, cap} resolver
            // the world events use: min(cap, 1 + perParticipant * max(0, count - startAt)), floored at 1.0,
            // with a sub-1.0 cap read as "off" rather than as "make them weaker than authored". The STATIC
            // overload on purpose: the instance one consults the world_events_* live overrides, which have
            // nothing to do with this ladder.
            var healthMult = CrowdHealthDef.Resolve(startAt, perPlayer, players, healthCap);

            var damageRating = DamageRatingAddend(players, startAt,
                PropertyManager.GetLong("bluespire_ladder_d6_crowd_dr_per_player").Item,
                PropertyManager.GetLong("bluespire_ladder_d6_crowd_dr_cap").Item);

            ApplyHealth(creature, healthMult);

            if (damageRating != 0)
                creature.DamageRating = (creature.DamageRating ?? 0) + damageRating;

            log.Debug($"[BLUESPIRE] crowd scaling wcid={creature.WeenieClassId} players={players} " +
                      $"health=x{healthMult:F2} damageRating=+{damageRating}");
        }

        /// <summary>
        /// The DamageRating ADDEND for a crowd of <paramref name="players"/> (pure, so it is unit testable).
        /// DamageRating is an additive percentage rating on this server, so this is "+N percent damage",
        /// which is why it is an addend on the creature's authored rating rather than a multiplier on it -
        /// the same shape ThreadDungeonSpawner.StampedDamageRating uses.
        ///
        /// A non-positive per-player value or a non-positive cap turns the damage axis off entirely and
        /// leaves the health axis alone, so the two can be tuned independently. The product saturates at
        /// int.MaxValue rather than wrapping.
        /// </summary>
        public static int DamageRatingAddend(int players, int startAt, long perPlayer, long cap)
        {
            if (perPlayer <= 0 || cap <= 0)
                return 0;

            var over = Math.Max(0, players - startAt);

            if (over == 0)
                return 0;

            var total = (long)Math.Min((double)over * perPlayer, int.MaxValue);

            return (int)Math.Min(total, Math.Min(cap, int.MaxValue));
        }

        /// <summary>
        /// Raises maximum health by <paramref name="mult"/> and refills, exactly as
        /// WorldEventSpawner.ApplyHealthMultiplier does, via the shared pure
        /// <see cref="WorldEventSpawner.ScaledStartingValue"/>. A multiplier at or below 1.0 is a no-op.
        ///
        /// Health only: damage is the separate DamageRating axis above, and defences and level are left
        /// exactly as the weenie authored them, so a crowded room is the same fight for longer rather than
        /// a different fight.
        /// </summary>
        private static void ApplyHealth(Creature creature, double mult)
        {
            if (!(mult > 1.0))
                return;

            var baseMax = creature.Health.MaxValue;

            var scaled = WorldEventSpawner.ScaledStartingValue(creature.Health.StartingValue, baseMax, mult);

            if (scaled == creature.Health.StartingValue)
                return;

            creature.Health.StartingValue = scaled;
            creature.Health.Current = creature.Health.MaxValue;
        }

        /// <summary>
        /// Whether one online character counts toward the crowd (pure, so it is unit testable without a
        /// live Player - no test on this server may build one).
        ///
        /// Three exclusions, and each is a separate rule:
        ///   - a DIFFERENT landblock instance is a different place, even at identical coordinates, so a
        ///     player in another realm copy of the same dungeon is not in this fight. A player with no
        ///     Location at all (null <paramref name="playerInstance"/>) is between places and is excluded
        ///     for the same reason.
        ///   - STAFF, meaning AccessLevel.Sentinel and above, via WorldEventAudienceSampler's own predicate
        ///     called rather than copied: an admin standing in a dungeon to watch a run must not make it
        ///     harder for the people fighting it. Advocates DO count, and a null access level counts, which
        ///     is the safe reading - the exclusion is for known staff, never for anything merely unknown.
        ///   - the DEAD, the same guard WorldEventAudienceSampler.CountAliveNear uses: a field of corpses is
        ///     a wipe, not a crowd.
        /// </summary>
        public static bool CountsTowardCrowd(AccessLevel? accessLevel, bool isDead, uint? playerInstance, uint creatureInstance)
        {
            if (playerInstance == null || playerInstance.Value != creatureInstance)
                return false;

            if (!WorldEventAudienceSampler.CountsTowardAudience(accessLevel))
                return false;

            return !isDead;
        }

        /// <summary>
        /// Online players in <paramref name="instance"/> who count toward the crowd, per
        /// <see cref="CountsTowardCrowd"/>. The one non-pure piece, isolated here for exactly that reason.
        ///
        /// Fails to 0 on an exception, and that direction is the safe one HERE: a zero crowd only ever makes
        /// the fight easier. (WorldEventAudienceSampler.CountAliveNear fails to int.MaxValue instead,
        /// because a presence count is deciding whether to END a run, where zero is the dangerous answer.)
        /// </summary>
        public static int CountInInstance(uint instance)
        {
            var count = 0;

            try
            {
                foreach (var player in PlayerManager.GetAllOnline())
                {
                    if (player == null)
                        continue;

                    if (CountsTowardCrowd(player.Session?.AccessLevel, player.IsDead, player.Location?.Instance, instance))
                        count++;
                }
            }
            catch (Exception ex)
            {
                log.Error("[BLUESPIRE] crowd scan failed; treating the dungeon as empty", ex);
                return 0;
            }

            return count;
        }
    }
}
