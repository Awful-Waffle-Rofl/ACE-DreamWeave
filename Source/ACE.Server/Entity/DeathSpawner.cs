using System;
using System.Reflection;

using ACE.Common;
using ACE.Server.Factories;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Places what a <see cref="DeathSpawnPlan"/> asked for. The impure half of the death-spawn primitive:
    /// the decision (what, how many, how far) is <see cref="DeathSpawnPlan.Decide"/> and is unit tested;
    /// this part touches the world and is not.
    ///
    /// Called once per death, from Creature.OnDeath, beside the KillQuest block. That method's own
    /// onDeathEntered latch is what makes it exactly once per death, so nothing here needs a latch of its
    /// own.
    ///
    /// EVERY PROPERTY WRITE HAPPENS BEFORE EnterWorld, the rule every hand-spawner in this fork follows
    /// (MlDigsiteSpawner, MlRelariaSpawner, WorldEventSpawner.TryPlace): there must be no instant in which a
    /// landblock save, or a client's create packet, sees one of these as an ordinary persistable monster.
    /// </summary>
    public static class DeathSpawner
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Reads the weenie properties off <paramref name="dying"/> and places the result. A no-op, and
        /// exactly ONE PropertyInt read, for every creature that does not author a death spawn - which is
        /// every creature in the game bar a handful, and this runs on the death path, so that is the budget.
        ///
        /// THE SHORT-CIRCUIT BELOW IS WHAT MAKES THAT SENTENCE TRUE. C# evaluates every argument before the
        /// call, so handing all three reads straight to <see cref="DeathSpawnPlan.Decide"/> would cost three
        /// dictionary lookups on every creature death server-wide rather than one. Decide still rejects an
        /// absent wcid on its own - that is its contract and its unit tests cover it - so this is purely a
        /// cost guard and removing it would change nothing but the bill.
        ///
        /// Never throws: a death that cannot place its adds must still be a death. Every failure is logged
        /// and swallowed.
        /// </summary>
        public static void TrySpawn(Creature dying)
        {
            if (dying == null)
                return;

            var wcid = dying.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.DeathSpawnWcid);

            if (wcid == null || wcid.Value <= 0)
                return;

            var plan = DeathSpawnPlan.Decide(
                wcid,
                dying.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.DeathSpawnCount),
                dying.GetProperty(ACE.Entity.Enum.Properties.PropertyFloat.DeathSpawnRadius));

            if (!plan.ShouldSpawn)
                return;

            try
            {
                Spawn(dying, plan);
            }
            catch (Exception ex)
            {
                log.Error($"[DEATHSPAWN] {dying.Name} (0x{dying.Guid.Full:X8}) death spawn {plan} threw", ex);
            }
        }

        /// <summary>
        /// The placement itself, exposed for an admin/test path that wants to drive a plan directly.
        /// Returns how many bodies actually landed; a partial result is a real outcome rather than an error,
        /// because a cramped room can refuse placements.
        /// </summary>
        public static int Spawn(Creature dying, DeathSpawnPlan plan)
        {
            if (dying == null || !plan.ShouldSpawn)
                return 0;

            // The dying creature's OWN position, which carries its landblock INSTANCE - a realm copy of a
            // landblock is a different place with the same geometry, so this is what keeps the adds in the
            // same dungeon as the fight that produced them.
            var anchor = dying.Location;

            if (anchor == null)
            {
                log.Warn($"[DEATHSPAWN] {dying.Name} (0x{dying.Guid.Full:X8}) died with no Location; {plan} skipped");
                return 0;
            }

            var rng = new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

            var points = WorldEventGeometry.Disc(anchor, plan.Radius, plan.Count, rng);

            var placed = 0;

            for (var i = 0; i < plan.Count; i++)
            {
                var scattered = i < points.Count ? points[i] : new Position(anchor);

                if (TryPlaceOne(dying, plan, scattered, anchor))
                    placed++;
            }

            log.Info($"[DEATHSPAWN] {dying.Name} (0x{dying.Guid.Full:X8}) at {anchor.ToLOCString()} placed {placed} of {plan}");

            return placed;
        }

        /// <summary>
        /// One body. Tries the scattered point first and falls back ONCE to the dying creature's exact
        /// position, because a scatter is a nicety and a shell swap that produced no boss is a broken
        /// encounter. The fallback matters most indoors: WorldEventGeometry offsets X/Y without recomputing
        /// an indoor cell id, so a point a few metres away can sit outside the room's geometry.
        /// </summary>
        private static bool TryPlaceOne(Creature dying, DeathSpawnPlan plan, Position scattered, Position anchor)
        {
            if (TryCreateAt(dying, plan, scattered))
                return true;

            return TryCreateAt(dying, plan, new Position(anchor));
        }

        private static bool TryCreateAt(Creature dying, DeathSpawnPlan plan, Position position)
        {
            if (position == null)
                return false;

            var wo = WorldObjectFactory.CreateNewWorldObject(plan.Wcid);

            if (wo == null)
            {
                log.Error($"[DEATHSPAWN] {dying.Name} names death spawn wcid {plan.Wcid}, which failed to create; " +
                          "is its weenie applied to this world database?");
                return false;
            }

            // Outdoors the sampled point carries the DYING creature's Z, which is only correct where it was
            // standing, and a creature spawned in mid-air falls. Indoors there is no terrain to consult and
            // the cell's own floor is the answer, so the snap is skipped and WorldObject.AddPhysicsObj's own
            // AdjustDungeon call resolves the cell during EnterWorld.
            if (!position.Indoors)
            {
                try
                {
                    position.AdjustMapCoords();
                }
                catch (Exception ex)
                {
                    log.Error($"[DEATHSPAWN] terrain snap failed at {position.ToLOCString()}", ex);
                    wo.Destroy();
                    return false;
                }
            }

            wo.Location = position;

            // ---- every write, before EnterWorld ---------------------------------------------------------

            // The persistence exclusion. A death spawn has no generator and stands on an ordinary PERSISTENT
            // landblock, so without this an add left standing at unload would be saved and would walk back
            // into the world on every later activation. See WorldObject.IsTransientSpawn.
            wo.IsTransientSpawn = true;

            bool entered;

            try
            {
                entered = wo.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DEATHSPAWN] EnterWorld threw for wcid {plan.Wcid} at {position.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                wo.Destroy();
                return false;
            }

            return true;
        }
    }
}
