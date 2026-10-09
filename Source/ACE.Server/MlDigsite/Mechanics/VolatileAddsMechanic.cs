using System;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite.Mechanics
{
    /// <summary>
    /// VOLATILE ADDS. "Fast, weak adds chase individual players and explode when they die with a ring spell,
    /// so players have to kill them away from the group." (RoZ round 13.)
    ///
    /// The pressure is positional, not numeric: one add dying next to you is survivable, three dying in the
    /// middle of the pack is not. The add is the ONE shared add weenie (MlDigsiteRoster's Add band), made
    /// fast and frail by the SET's addhp/addspeed rather than by a weenie of its own.
    ///
    /// SHAPE. The cadence half runs on the driver tick and places adds. The detonation half hangs off the
    /// death hook that ALREADY EXISTS for every digsite creature - Creature.Die calls
    /// MlDigsiteManager.OnEncounterCreatureDied, which routes the new MlDigsiteDeathKind.Add here - so nothing
    /// in core combat was touched to build this.
    ///
    /// THE FUSE IS A DRIVER STEP, not an ActionChain and not a delayed landblock action. The add is gone by
    /// the time it burns; the encounter may end before it does; and a step on the driver's own queue is
    /// dropped wholesale by MlDigsiteBossMechanics.Stop, which an ActionChain would not be.
    ///
    /// NOT A HOTSPOT. The "ring spell" is drawn as markers and resolved by explicit radial iteration, because
    /// a Hotspot's damage radius is fixed by its model's CylSphere and is scale-invariant - see
    /// MlDigsiteMechanicContext.RadialHit.
    /// </summary>
    public sealed class VolatileAddsMechanic : IMlDigsiteMechanic
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MlDigsiteMechanic Kind => MlDigsiteMechanic.Volatile;

        // Documented defaults, applied for any arg the tunable string leaves out. Never 0, never blank.
        private const double DefaultEvery = 20.0;
        private const int DefaultCount = 3;
        private const int DefaultMaxLive = 6;
        private const double DefaultRadius = 6.0;
        private const double DefaultDamage = 400.0;
        private const double DefaultFuse = 2.0;

        /// <summary>
        /// The detonation's own marker burst (design section 5.1 step 6): markers on the edge of the circle
        /// that was just hit, held for BurstSeconds and then taken back out. Presentation only - the damage
        /// has already been applied by the time these are placed, and nothing reads them. They are what turns
        /// "you took 400 fire" into "that corpse just went off, and THAT is how big it was", which is the
        /// whole tell the next add teaches.
        /// </summary>
        private const int BurstMarkers = 6;
        private const double BurstSeconds = 2.0;

        public void Tick(MlDigsiteMechanicContext ctx)
        {
            if (!ctx.CadenceDue(ctx.Args.GetDouble("every", DefaultEvery)))
                return;

            var count = Math.Clamp(ctx.Args.GetInt("count", DefaultCount), 1, 12);
            var maxLive = Math.Clamp(ctx.Args.GetInt("maxlive", DefaultMaxLive), 1, 40);

            // A group that cannot clear the adds must not be buried under an unbounded pile of them: a cycle
            // that would exceed the live cap is SKIPPED, not trimmed, so the pressure stays legible.
            if (ctx.Encounter.LiveAddCount >= maxLive)
            {
                log.Debug($"[ML_DIGSITE] {ctx.Encounter} volatile cycle skipped: {ctx.Encounter.LiveAddCount} add(s) already alive (maxlive {maxLive})");
                return;
            }

            var placed = 0;

            for (var i = 0; i < count; i++)
            {
                if (!ctx.Encounter.RunValid)
                    break;

                if (MlDigsiteSpawner.TrySpawn(ctx.Encounter, MlDigsiteRole.Add, ctx.Rng) != null)
                    placed++;
            }

            log.Info($"[ML_DIGSITE] {ctx.Encounter} volatile adds placed={placed} (asked {count}, slot {ctx.Slot})");

            if (placed > 0)
                ctx.Say("Something small and quick tears its way up out of the loose earth.", ChatMessageType.WorldBroadcast);
        }

        /// <summary>
        /// One add has died. Runs on the LANDBLOCK thread inside Creature.Die, while the add still has its
        /// Location - the same slot MlDigsiteManager.ResolveKeptSiraluunDrops already reads a dying creature's
        /// live biota from.
        ///
        /// It captures the position, announces the fuse and SCHEDULES the detonation; it never applies damage
        /// itself, so no damage pass ever runs from inside a death.
        ///
        /// GATED ON THE SET carrying this mechanic at all: an add spawned by an immune phase in a set that has
        /// no volatile slot is a gate to be killed, not a bomb, and detonating it would punish exactly the
        /// thing the phase is asking players to do.
        ///
        /// Reads its numbers from MlDigsiteTunables' own parsed args rather than from a context, because a
        /// death hook has no tick and therefore no context. That is still the system's single PropertyManager
        /// read point, not a second one.
        /// </summary>
        public static void OnAddDied(MlDigsiteEncounter encounter, MlDigsiteBossMechanicState state, Creature add)
        {
            var set = state.Set;

            if (set.Main != MlDigsiteMechanic.Volatile && set.Secondary != MlDigsiteMechanic.Volatile)
                return;

            var location = add.Location;

            if (location == null)
                return;

            var args = MlDigsiteTunables.BossRushVolatileArgs;

            var fuse = Math.Clamp(args.GetDouble("fuse", DefaultFuse), 0.0, 30.0);
            var radius = Math.Clamp(args.GetDouble("radius", DefaultRadius), 0.5, 100.0);
            var damage = Math.Max(0.0, args.GetDouble("damage", DefaultDamage));

            // A COPY. The dying creature's Position is its live location object, and Die() is about to move
            // the creature through its death chain; a step holding the reference would resolve somewhere else.
            var centre = new Position(location);
            var name = add.Name;

            MlDigsiteProps.PlayScriptOn(add, PlayScript.EnchantUpRed);

            MlDigsiteManager.Announce(encounter, $"{name} swells and begins to glow!", ChatMessageType.WorldBroadcast);

            state.Schedule(DateTime.UtcNow + TimeSpan.FromSeconds(fuse), () => Detonate(encounter, centre, radius, damage));

            log.Debug($"[ML_DIGSITE] {encounter} volatile add 0x{add.Guid.Full:X8} armed: {radius}m / {damage} in {fuse}s");
        }

        /// <summary>
        /// The detonation, on the world-thread driver tick. Re-checks the encounter, because a step is
        /// scheduled seconds before it runs and an encounter can end in between - MlDigsiteBossMechanics.Stop
        /// drops the queue outright, and this guard covers the tick that races it.
        /// </summary>
        private static void Detonate(MlDigsiteEncounter encounter, Position centre, double radius, double damage)
        {
            if (encounter == null || !encounter.RunValid)
                return;

            var boss = encounter.ObjectiveCreature;

            if (boss == null || boss.IsDestroyed || boss.IsDead)
                return;

            var ctx = new MlDigsiteMechanicContext(encounter, encounter.BossMechanics, boss, DateTime.UtcNow,
                MlDigsiteMechanicSlot.Main, MlDigsiteMechanic.Volatile, MlDigsiteTunables.BossRushVolatileArgs, null);

            var hit = ctx.RadialHit(centre, radius, damage, DamageType.Fire);

            // The burst goes up AFTER the hit and comes down on its own step. Through Burst rather than
            // Telegraph, because a telegraph call would take down the drum or safe-zone markers the other
            // slot of sets 2 and 4 may have standing at this instant.
            ctx.Burst(centre, radius, BurstMarkers, BurstSeconds);

            if (hit > 0)
                log.Debug($"[ML_DIGSITE] {encounter} volatile detonation hit {hit} player(s) within {radius}m");
        }
    }
}
