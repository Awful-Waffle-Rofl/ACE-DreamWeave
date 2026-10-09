using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite.Mechanics
{
    /// <summary>
    /// DRUM CADENCE. "The boss drums 1, 2 or 3 beats, each with a chat message and a sound. After a short
    /// delay the count triggers a different attack (for example a ring, a wall or a ball volley)." (RoZ round
    /// 13.)
    ///
    /// This is the "learn the tell" set: the damage is a slow bleed for a group that never moves rather than
    /// a wipe, because the point is that the beat count is readable BEFORE the shape lands.
    ///
    /// SHAPE. One cadence schedules everything it will ever do up front, as beats on the driver's step queue:
    /// beat i at i * beatgap, the telegraph on the last beat, the resolve at resolve seconds after it. Every
    /// step is dropped wholesale if the encounter ends, and the steps come back from the queue IN ORDER, so a
    /// tick that catches two at once still runs beat 2 before the resolve.
    ///
    /// 1 SECOND GRANULARITY IS THE CONSTRAINT (owner ruling 2026-09-20): beatgap and resolve are quantised to
    /// the digsite's existing 1 s tick, so beatgap=1 and resolve=2 are exact and anything finer is not
    /// expressible. A second, faster clock would be real per-tick cost across every live encounter for
    /// marginal feel.
    ///
    /// THE TELEGRAPH IS DRAWN AND ALSO SPOKEN. Markers can be missed - a player whose client has not received
    /// the create packet sees no glow - so the shape is named in chat as well, which is what makes the
    /// mechanic fair rather than merely visible.
    /// </summary>
    public sealed class DrumCadenceMechanic : IMlDigsiteMechanic
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MlDigsiteMechanic Kind => MlDigsiteMechanic.Drums;

        // Documented defaults, applied for any arg the tunable string leaves out. Never 0, never blank.
        private const double DefaultEvery = 25.0;
        private const double DefaultBeatGap = 1.0;
        private const double DefaultResolve = 2.0;
        private const double DefaultDamage = 500.0;
        private const double DefaultRingRadius = 8.0;
        private const double DefaultWallWidth = 6.0;
        private const double DefaultVolleyRadius = 4.0;
        private const int DefaultVolleyTargets = 3;

        // Round 17: the Volley's burning ground. Owner ruling: 150 Fire damage every second for 8 seconds.
        private const double DefaultBurnDamage = 150.0;
        private const double DefaultBurnSeconds = 8.0;
        private const double DefaultBurnInterval = 1.0;

        /// <summary>How many markers a drawn shape uses. Presentation only; nothing decides a hit from them.</summary>
        private const int RingMarkers = 8;
        private const int WallMarkers = 7;

        public void Tick(MlDigsiteMechanicContext ctx)
        {
            if (!ctx.CadenceDue(ctx.Args.GetDouble("every", DefaultEvery)))
                return;

            var beats = MlDigsiteBossMechanicRules.RollBeats(ctx.Rng);
            var shape = MlDigsiteBossMechanicRules.ShapeForBeats(beats);

            var beatGap = Math.Clamp(ctx.Args.GetDouble("beatgap", DefaultBeatGap), 0.0, 30.0);
            var resolve = Math.Clamp(ctx.Args.GetDouble("resolve", DefaultResolve), 0.0, 30.0);

            log.Info($"[ML_DIGSITE] {ctx.Encounter} drum cadence beats={beats} shape={shape} (slot {ctx.Slot})");

            for (var i = 1; i <= beats; i++)
            {
                var beat = i;

                ctx.Schedule(beatGap * (i - 1), () =>
                {
                    ctx.Say($"{ctx.Boss.Name} strikes the drum. ({beat})", ChatMessageType.WorldBroadcast);
                    ctx.Sound(Sound.UI_Drums);
                    ctx.Motion((MotionCommand)MlDigsiteTunables.BossRushDrumMotion);

                    // Round 18 owner ruling: the beat colour also counts the beat (blue/green/gold), so a
                    // player whose client missed the chat line can still read the count off the boss itself.
                    MlDigsiteProps.PlayScriptOn(ctx.Boss, MlDigsiteBossMechanicRules.DrumBeatPlayScript(beat));
                });
            }

            // The telegraph goes up ON the last beat, so the beat count is the whole tell right up until the
            // shape is drawn - a player who counted can already be moving.
            ctx.Schedule(beatGap * (beats - 1), () => Telegraph(ctx, shape));

            // The resolve's own wall-clock moment, captured now: a step runs with THIS tick's context, whose Now
            // is the cadence tick, so anything the resolve schedules (the Volley's burn) is timed from here.
            var resolveOffset = beatGap * (beats - 1) + resolve;
            var resolveAt = ctx.Now + TimeSpan.FromSeconds(resolveOffset);

            ctx.Schedule(resolveOffset, () => Resolve(ctx, shape, resolveAt));
        }

        /// <summary>
        /// Draws the rolled shape and names it. The marks a VOLLEY resolves against are captured here, not at
        /// resolve time - the whole point of a volley is that a player can step off their own mark, which they
        /// could not do if the mark followed them.
        /// </summary>
        private static void Telegraph(MlDigsiteMechanicContext ctx, MlDigsiteDrumShape shape)
        {
            if (!ctx.Encounter.RunValid || ctx.Boss.IsDestroyed || ctx.Boss.IsDead)
                return;

            var centre = ctx.Boss.Location;

            if (centre == null)
                return;

            switch (shape)
            {
                case MlDigsiteDrumShape.Ring:
                {
                    var radius = Math.Clamp(ctx.Args.GetDouble("ringradius", DefaultRingRadius), 1.0, 100.0);

                    ctx.Telegraph(WorldEventGeometry.Ring(centre, (float)radius, RingMarkers, 0.0f, ctx.Rng));
                    ctx.Say("The beat closes in a ring around it. Get outside the lights!", ChatMessageType.WorldBroadcast);
                    break;
                }

                case MlDigsiteDrumShape.Wall:
                {
                    var width = Math.Clamp(ctx.Args.GetDouble("wallwidth", DefaultWallWidth), 1.0, 100.0);
                    var heading = centre.GetCurrentDir();

                    // The drawn span and the hit region read the SAME factor - see WallLengthFactor, and
                    // InsideWall's own remarks for what it cost when they were not the same number.
                    ctx.Telegraph(MlDigsiteMechanicGeometry.Line(centre, heading,
                        (float)(width * MlDigsiteBossMechanicRules.WallLengthFactor), WallMarkers));
                    ctx.Say("The beat runs out in a line. Step off the lights!", ChatMessageType.WorldBroadcast);
                    break;
                }

                default:
                {
                    var marks = RollVolleyMarks(ctx);

                    ctx.Telegraph(marks);
                    ctx.Say(marks.Count > 0
                        ? "The beat splits and comes down in pieces. Move off your own mark!"
                        : "The beat splits and finds nobody to land on.", ChatMessageType.WorldBroadcast);
                    break;
                }
            }
        }

        /// <summary>
        /// Resolves the shape. Recomputed from the boss's CURRENT position for the ring and the wall, because
        /// both are centred on the boss and the boss can have moved during the telegraph - which is exactly
        /// the tell a player uses to read where the ring will be.
        ///
        /// The volley is the exception: it resolves at the POSITIONS that were marked, which is what makes
        /// stepping off a mark work at all.
        /// </summary>
        private static void Resolve(MlDigsiteMechanicContext ctx, MlDigsiteDrumShape shape, DateTime resolveAt)
        {
            if (!ctx.Encounter.RunValid || ctx.Boss.IsDestroyed || ctx.Boss.IsDead)
                return;

            var centre = ctx.Boss.Location;

            if (centre == null)
                return;

            var damage = Math.Max(0.0, ctx.Args.GetDouble("damage", DefaultDamage));
            var hit = 0;

            switch (shape)
            {
                case MlDigsiteDrumShape.Ring:
                {
                    if (TryCastShape(ctx, MlDigsiteTunables.BossRushDrumRingSpell, ProjectileSpellType.Ring, shape))
                        break;

                    var radius = Math.Clamp(ctx.Args.GetDouble("ringradius", DefaultRingRadius), 1.0, 100.0);

                    hit = ctx.RadialHit(centre, radius, damage, DamageType.Bludgeon);
                    break;
                }

                case MlDigsiteDrumShape.Wall:
                {
                    if (TryCastShape(ctx, MlDigsiteTunables.BossRushDrumWallSpell, ProjectileSpellType.Wall, shape))
                        break;

                    var width = Math.Clamp(ctx.Args.GetDouble("wallwidth", DefaultWallWidth), 1.0, 100.0);
                    var heading = centre.GetCurrentDir();
                    var origin = centre.ToGlobal();

                    // The SAME span the telegraph drew, end to end. Passing it is what confines the hit to
                    // the shape the players were shown instead of to an infinite line through the boss.
                    var length = width * MlDigsiteBossMechanicRules.WallLengthFactor;

                    foreach (var player in ctx.Participants())
                    {
                        var loc = player?.Location;

                        if (loc == null || loc.Instance != centre.Instance)
                            continue;

                        var point = loc.ToGlobal();

                        if (!MlDigsiteBossMechanicRules.InsideWall(origin.X, origin.Y, heading.X, heading.Y, point.X, point.Y, width, length))
                            continue;

                        ctx.Hit(player, damage, DamageType.Bludgeon);
                        hit++;
                    }

                    break;
                }

                default:
                {
                    var radius = Math.Clamp(ctx.Args.GetDouble("volleyradius", DefaultVolleyRadius), 1.0, 100.0);

                    var marks = ctx.State.TakeMarkedPoints();

                    foreach (var mark in marks)
                        hit += ctx.RadialHit(mark, radius, damage, DamageType.Bludgeon);

                    // Round 17: the marks do not go out - they catch fire. The telegraph's own markers are
                    // taken off the shared list (so the next telegraph cannot take them down early) and handed
                    // to the burn, which removes them itself when it ends.
                    StartBurn(ctx, marks, ctx.State.TakeMarkers(), radius, resolveAt);

                    if (hit > 0)
                        log.Debug($"[ML_DIGSITE] {ctx.Encounter} drum {shape} hit {hit} player(s)");

                    return;
                }
            }

            ctx.ClearMarkers();

            if (hit > 0)
                log.Debug($"[ML_DIGSITE] {ctx.Encounter} drum {shape} hit {hit} player(s)");
        }

        /// <summary>
        /// ROUND 17 OWNER RULING: each Volley mark becomes BURNING GROUND. After the (unchanged) resolve hit,
        /// every player standing within volleyradius of any mark takes burndamage Fire damage once per
        /// burninterval, for burnseconds - occupancy RE-CHECKED ON EVERY TICK, so stepping out stops it and
        /// stepping back in resumes it. Shipped 150 every 1 s for 8 s.
        ///
        /// DRIVER-SIDE TICKS, NOT A PHYSICS HOTSPOT, for three reasons. (1) It is this driver's invariant 7:
        /// all mechanic damage goes through RadialHit/Hit, never a Hotspot. (2) A Hotspot's damage radius comes
        /// from its Setup's CylSphere and is scale-invariant (MlDigsiteMechanicContext.RadialHit's remarks), so
        /// volleyradius could not be honoured by one; measuring the radius in C# is what makes the burn cover
        /// exactly the ground the resolve hit covered. (3) Every tick is a step on the encounter's own queue,
        /// which the encounter's end drops wholesale (MlDigsiteBossMechanics.Stop), and which a unit test can
        /// drive deterministically (ScheduleBurn).
        ///
        /// VISIBLE FOR THE WHOLE DURATION: the patch is the telegraph's own marker - the hazard marker,
        /// ml_digsite_bossrush_hazard_marker_wcid, shipped as 1002665 "Pillar of Fire", a column of fire - kept
        /// standing instead of being cleared at the resolve, and removed by the burn's last step. A marker wcid
        /// of 0 leaves the burn running with no visual, exactly as it already leaves the telegraph.
        ///
        /// COST: one online-player walk (Participants) per tick while a burn is live - 8 walks per Volley at the
        /// shipped values, against the 1 s tick; the RadialHit cost note on MlDigsiteBossMechanics is about
        /// calling it on the CADENCE check of every tick, which this does not do.
        /// </summary>
        private static void StartBurn(MlDigsiteMechanicContext ctx, IReadOnlyList<Position> marks, List<WorldObjects.WorldObject> markers,
            double radius, DateTime resolveAt)
        {
            var damage = Math.Max(0.0, ctx.Args.GetDouble("burndamage", DefaultBurnDamage));
            var duration = Math.Clamp(ctx.Args.GetDouble("burnseconds", DefaultBurnSeconds), 0.0, 60.0);
            var interval = Math.Clamp(ctx.Args.GetDouble("burninterval", DefaultBurnInterval), 1.0, 30.0);

            var encounter = ctx.Encounter;
            var standing = markers ?? new List<WorldObjects.WorldObject>();

            void RemoveMarkers()
            {
                for (var i = 0; i < standing.Count; i++)
                    MlDigsiteProps.Remove(standing[i]);
            }

            if (marks == null || marks.Count == 0 || !(damage > 0.0))
            {
                RemoveMarkers();
                return;
            }

            // A private copy: the tick closures outlive this call.
            var patches = new List<Position>(marks);

            var ticks = ScheduleBurn(ctx.State, resolveAt, duration, interval,
                tick => BurnTick(ctx, patches, radius, damage, tick),
                RemoveMarkers);

            if (ticks == 0)
            {
                RemoveMarkers();
                return;
            }

            log.Info($"[ML_DIGSITE] {encounter} drum Volley burning ground: {patches.Count} patch(es) radius={radius:0.#}m {damage:0} Fire every {interval:0.#}s x{ticks}");
        }

        /// <summary>
        /// Queues a burn's ticks and its end on the driver's step queue: tick i (1-based) at
        /// <paramref name="start"/> + i * interval, then <paramref name="end"/> at start + duration - queued
        /// AFTER the last tick, so on the tick they share the damage lands before the visual goes. Returns how
        /// many ticks were queued (MlDigsiteBossMechanicRules.BurnTickCount); 0 queues nothing at all.
        ///
        /// Pure scheduling on the state object, with no world access, so the per-second contract is tested
        /// directly (MlDigsiteRozR17Tests) by draining the queue a second at a time.
        /// </summary>
        internal static int ScheduleBurn(MlDigsiteBossMechanicState state, DateTime start, double durationSeconds, double intervalSeconds,
            Action<int> tick, Action end)
        {
            var count = MlDigsiteBossMechanicRules.BurnTickCount(durationSeconds, intervalSeconds);

            if (state == null || tick == null || count == 0)
                return 0;

            for (var i = 1; i <= count; i++)
            {
                var n = i;
                state.Schedule(start + TimeSpan.FromSeconds(intervalSeconds * i), () => tick(n));
            }

            if (end != null)
                state.Schedule(start + TimeSpan.FromSeconds(durationSeconds), end);

            return count;
        }

        /// <summary>
        /// One burn tick: every participant standing in any patch RIGHT NOW takes the tick's Fire damage, once,
        /// through ctx.Hit (the boss as the source, the player's own landblock queue, the same resistance mod
        /// the resolve hit gets). The encounter having ended, or the boss being gone, ends the burn quietly -
        /// and the encounter's own Stop drops every remaining tick anyway.
        /// </summary>
        private static void BurnTick(MlDigsiteMechanicContext ctx, IReadOnlyList<Position> patches, double radius, double damage, int tick)
        {
            if (!ctx.Encounter.RunValid || ctx.Boss.IsDestroyed || ctx.Boss.IsDead)
                return;

            var hit = 0;

            foreach (var player in ctx.Participants())
            {
                if (!MlDigsiteBossMechanicRules.InsideAnyBurn(player?.Location, patches, radius))
                    continue;

                ctx.Hit(player, damage, DamageType.Fire);
                hit++;
            }

            if (hit > 0)
                log.Debug($"[ML_DIGSITE] {ctx.Encounter} drum Volley burn tick {tick} hit {hit} player(s)");
        }

        /// <summary>
        /// Round 18 owner ruling: the Ring and Wall shapes resolve as REAL retail spells - RoZ playtest
        /// feedback that nothing was visibly cast, so the shapes read as invisible damage. The boss AND every
        /// live Boss Rush add (MlDigsiteEncounter.LiveAddSnapshot - never the wave list) each cast
        /// <paramref name="spellId"/> centred on themselves, up to ml_digsite_bossrush_drum_max_casters
        /// casters total (the boss counts as one of that total). <paramref name="spellId"/> of 0, or one that
        /// does not classify as <paramref name="expected"/> (SpellProjectile.GetProjectileSpellType), takes
        /// no action here and returns false, telling the caller to run the ORIGINAL geometric damage for this
        /// shape instead - never throws either way. The telegraph markers are unaffected regardless of which
        /// path runs; only the damage delivery changes.
        ///
        /// TARGET: one participant, drawn once per resolve and shared by every caster this resolve, so the
        /// group dodges ONE ring/wall pattern together rather than N independent ones from N casters. Ring's
        /// 360-degree spread makes this barely matter there; Wall aims its line of projectiles at this point.
        /// No participant present leaves the target null, which CreateSpellProjectiles already handles for a
        /// non-player caster (falls back to the caster's own forward vector - WorldObject_Magic.cs).
        ///
        /// Returns true when the spell-cast path was taken, whether or not any caster turned out to be alive
        /// to cast it (the boss is always alive here - Resolve's own guard already returned before this runs
        /// otherwise) - the caller must not ALSO apply the geometric hit in that case.
        /// </summary>
        private static bool TryCastShape(MlDigsiteMechanicContext ctx, long spellId, ProjectileSpellType expected, MlDigsiteDrumShape shape)
        {
            var actualType = spellId > 0 ? SpellProjectile.GetProjectileSpellType((uint)spellId) : ProjectileSpellType.Undef;

            if (!MlDigsiteBossMechanicRules.IsValidDrumSpell(spellId, actualType, expected))
            {
                if (spellId != 0)
                {
                    log.Warn($"[ML_DIGSITE] {ctx.Encounter} drum {shape} spell id {spellId} is not a {expected} spell " +
                        $"(classified {actualType}) - falling back to geometric damage");
                }

                return false;
            }

            var spell = new Spell((uint)spellId);

            if (spell.NotFound)
            {
                log.Warn($"[ML_DIGSITE] {ctx.Encounter} drum {shape} spell id {spellId} did not load - falling back to geometric damage");
                return false;
            }

            var casters = MlDigsiteBossMechanicRules.SelectDrumCasters(ctx.Boss, ctx.Encounter.LiveAddSnapshot(), (int)MlDigsiteTunables.BossRushDrumMaxCasters);

            if (casters.Count == 0)
                return true;

            var target = PickTarget(ctx);

            for (var i = 0; i < casters.Count; i++)
                ctx.CastSpellProjectiles(casters[i], spell, target);

            log.Debug($"[ML_DIGSITE] {ctx.Encounter} drum {shape} cast {spell.Name} ({spellId}) from {casters.Count} caster(s)");

            return true;
        }

        /// <summary>One participant, drawn at random, shared by every caster of one drum resolve. Null if none are present.</summary>
        private static WorldObject PickTarget(MlDigsiteMechanicContext ctx)
        {
            var candidates = ctx.Participants();

            if (candidates.Count == 0)
                return null;

            return candidates[ctx.Rng.Next(candidates.Count)];
        }

        /// <summary>
        /// Picks the volley's marks: up to volleytargets distinct participants' CURRENT positions, drawn
        /// without replacement so two marks never land on one player. The positions are recorded on the driver
        /// state so the resolve reads the same points that were drawn.
        /// </summary>
        private static IReadOnlyList<Position> RollVolleyMarks(MlDigsiteMechanicContext ctx)
        {
            var wanted = Math.Clamp(ctx.Args.GetInt("volleytargets", DefaultVolleyTargets), 1, 20);

            var candidates = ctx.Participants();
            var marks = new List<Position>();

            for (var i = 0; i < wanted && candidates.Count > 0; i++)
            {
                var index = ctx.Rng.Next(candidates.Count);
                var loc = candidates[index].Location;

                candidates.RemoveAt(index);

                if (loc != null)
                    marks.Add(new Position(loc));
            }

            ctx.State.SetMarkedPoints(marks);

            return marks;
        }
    }
}
