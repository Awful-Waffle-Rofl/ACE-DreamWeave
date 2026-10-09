using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite.Mechanics
{
    /// <summary>
    /// SHIFTING SAFE ZONES. "Rolling balls, boulder lines or falling rocks force the group to move between
    /// safe spots. Use flat ground, mark the safe spots with placed glowing objects, and give players room
    /// for 2 mistakes on instant-death hits." (RoZ round 13.)
    ///
    /// NO WALLS, NO DOORS, NO GEOMETRY. The safe spots are points on a ring about the dig, terrain-snapped at
    /// placement (MlDigsiteProps), which is what makes this work on rolling island terrain rather than only on
    /// a true plane. There is no hazard object at all: the ground that is NOT marked is what hurts, which
    /// needs nothing placed on it.
    ///
    /// "ROOM FOR 2 MISTAKES", MECHANICALLY. The first forgiveness= misses in a window CANNOT kill - the
    /// damage is capped at one point short of the player's current health by construction, not by tuning, so
    /// the guarantee holds at any gear level and any boss level. The miss after them is lethal, with no cap.
    /// One stack decays for each decay= seconds with no miss, so a ten-minute fight does not assemble a death
    /// sentence out of three unrelated mistakes.
    ///
    /// AND THE HIT IT FORGIVES IS ACTUALLY LETHAL. The tester's words are "instant-death hits", so damage=
    /// ships at 1.0, which MlDigsiteBossMechanicRules.ResolveDamage reads as the player's WHOLE max health -
    /// the feature's one damage convention: at or below 1.0 a fraction of max health, above 1.0 a flat
    /// pre-resistance number, the same call the interrupt mechanic's missed hit reads through. A flat number
    /// here made the forgiveness decorative, because a survivable hit cannot be the thing a cap saves you
    /// from. Resistances still apply on top (MlDigsiteMechanicContext.Hit), so a player who has stacked
    /// bludgeon protection can live through the lethal one; that is the same latitude every other hit in
    /// this game gives, and the cap is what guarantees the first two regardless.
    ///
    /// The counter is per player, in memory, and does NOT survive a logout. That is an exploit in the strict
    /// sense - relog to clear a death sentence - and a trivial one, because relogging costs far more time
    /// than the decay window; persisting it would mean a shard write per miss.
    /// </summary>
    public sealed class SafeZonesMechanic : IMlDigsiteMechanic
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MlDigsiteMechanic Kind => MlDigsiteMechanic.SafeZones;

        // Documented defaults, applied for any arg the tunable string leaves out. Never 0, never blank.
        private const double DefaultEvery = 15.0;
        private const double DefaultTelegraph = 4.0;
        private const int DefaultZones = 3;
        private const double DefaultSafeRadius = 5.0;
        private const double DefaultRing = 18.0;
        private const int DefaultForgiveness = 2;
        private const double DefaultDecay = 60.0;

        /// <summary>
        /// 1.0 = the player's whole max health, through MlDigsiteBossMechanicRules.ResolveDamage. See the
        /// class remarks: the forgiveness guarantee only means anything over a hit that would otherwise kill.
        /// </summary>
        private const double DefaultDamage = 1.0;

        public void Tick(MlDigsiteMechanicContext ctx)
        {
            if (!ctx.CadenceDue(ctx.Args.GetDouble("every", DefaultEvery)))
                return;

            var anchor = ctx.Anchor;

            if (anchor == null)
                return;

            var zones = Math.Clamp(ctx.Args.GetInt("zones", DefaultZones), 1, 12);
            var ring = Math.Clamp(ctx.Args.GetDouble("ring", DefaultRing), 2.0, 100.0);
            var telegraph = Math.Clamp(ctx.Args.GetDouble("telegraph", DefaultTelegraph), 1.0, 30.0);

            var marks = WorldEventGeometry.Ring(anchor, (float)ring, zones, 3.0f, ctx.Rng);

            if (marks.Count == 0)
                return;

            // Recorded as the points the resolve measures against, NOT as the marker objects: a marker whose
            // landblock build failed must not turn its safe spot into a lethal one, and a player standing on
            // a spot whose light never appeared has still stood in the right place.
            ctx.State.SetMarkedPoints(marks);

            ctx.Telegraph(marks, safe: true);

            ctx.Say("The ground starts to come apart. Get to the lights!", ChatMessageType.WorldBroadcast);

            ctx.Schedule(telegraph, () => Resolve(ctx));

            log.Debug($"[ML_DIGSITE] {ctx.Encounter} safe zones placed {marks.Count} mark(s) at {ring}m, telegraph {telegraph}s (slot {ctx.Slot})");
        }

        /// <summary>
        /// Resolves one cycle: everyone further than saferadius from the NEAREST mark is hit.
        ///
        /// A cycle that lost its marks (an encounter ending between telegraph and resolve, a set of markers
        /// already consumed) hits NOBODY rather than everybody - the fail-safe direction, because the other
        /// one kills a group for a mechanic that was never shown to them.
        /// </summary>
        private static void Resolve(MlDigsiteMechanicContext ctx)
        {
            if (!ctx.Encounter.RunValid)
                return;

            var marks = ctx.State.TakeMarkedPoints();

            ctx.ClearMarkers();

            if (marks.Count == 0)
                return;

            var safeRadius = Math.Clamp(ctx.Args.GetDouble("saferadius", DefaultSafeRadius), 0.5, 100.0);
            var forgiveness = Math.Clamp(ctx.Args.GetInt("forgiveness", DefaultForgiveness), 0, 20);
            var decay = Math.Clamp(ctx.Args.GetDouble("decay", DefaultDecay), 0.0, 3600.0);
            var authoredDamage = ctx.Args.GetDouble("damage", DefaultDamage);

            var missed = 0;

            foreach (var player in ctx.Participants())
            {
                var loc = player?.Location;

                if (loc == null)
                    continue;

                if (MlDigsiteMechanicGeometry.NearestDistance(loc, marks) <= safeRadius)
                    continue;

                missed++;

                var before = ctx.State.NoteSafeZoneMiss(player.Guid.Full, ctx.Now, decay);
                var lethal = MlDigsiteBossMechanicRules.MissIsLethal(before, forgiveness);

                // Resolved PER PLAYER, because the shipped damage is a fraction of that player's own max
                // health - the whole of it, so a miss this player is not forgiven for kills them.
                var damage = MlDigsiteBossMechanicRules.ResolveDamage(authoredDamage, player.Health?.MaxValue ?? 0);

                ctx.Hit(player, damage, DamageType.Bludgeon, nonLethal: !lethal);

                if (!lethal)
                    ctx.Tell(player, MlDigsiteBossMechanicRules.MissWarning(before + 1, forgiveness));
            }

            if (missed > 0)
                log.Debug($"[ML_DIGSITE] {ctx.Encounter} safe zones caught {missed} player(s) outside every mark");
        }
    }
}
