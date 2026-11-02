using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The Ember Wisp squad: wisps glide at the region's hazard height (on the Pinnacle, just above the floor -
    /// ground + HazardRegionPresets.PinnacleWispOffset, owner ruling 2026-10-18), settle into a formation, hold
    /// still (the telegraph), then each casts a fire pillar where it stands. Registered as "wisps"; usable anywhere
    /// a <see cref="HazardRegion"/> is (rect, circle, ad hoc, outdoor), by /hazard or by a boss or world event
    /// through HazardManager.Start.
    ///
    /// A CYCLE is: glide in unison (<c>move</c> s) to the formation, hold (<c>hold</c> s, the reaction time the telegraph
    /// gives), burst (<c>burst</c> s of pillar), rest (<c>rest</c> s); <c>cycles</c> of them, then the run ends Completed. A
    /// sweep cycle is several such steps, one row each. The patterns are in <see cref="WispFormations"/>, the numbers
    /// in <see cref="WispSettings"/>, every decision that is not geometry in <see cref="WispRules"/>, the state machine in
    /// <see cref="WispScript"/>, the burning pillars in <see cref="WispBurnBook"/>, and the run itself (actions to world
    /// calls) in <see cref="WispDirector{THandle}"/>. THIS CLASS ONLY ADAPTS A <see cref="HazardContext"/> TO THE DIRECTOR'S PORT.
    ///
    /// HOW THE SHARED INVARIANTS ARE MET. Spawns: ctx.Spawn (wisps) and ctx.SpawnTimedEffect (pillars). Movement: ONE
    /// ctx.MoveLegs per step for the whole formation, never per-wisp timers. Damage: ONE call (ctx.HitPlayers, element ctx.DamageType, default text) from the
    /// director, with radius pillar_radius and the one-hit-per-source window (+infinity; every burst is a new pillar host). Geometry: ctx.Region only. No PropertyManager
    /// read. WISPS NEVER ENTER SOLIDS: every target goes through HazardGround.PlaceWisp (WispRules.Place) - the wisp glides
    /// to its Stand point, the pillar fires at its Pillar point.
    ///
    /// DEAD HANDLES, one policy for every pattern: a wisp lost to /reload-landblock is dropped from the squad (its spot
    /// is left empty, its burst is skipped) and the run carries on; when none is left the run ends Aborted ("all wisps lost").
    /// A pillar host that will not spawn is retried once at the wisp's stand point; if that fails too nothing is damaged.
    /// </summary>
    public sealed class WispSquadMechanic : IHazardMechanic
    {
        private WispSettings settings = WispSettings.From(HazardArgs.Empty);
        private WispDirector<HazardHandle> director;

        public string Kind => HazardMechanicRegistry.Wisps;

        public IReadOnlyList<string> Patterns => WispRules.Patterns;

        public IEnumerable<uint> RequiredWcids(HazardArgs args)
            => new[] { WispSettings.From(args).WispWcid, HazardDefaults.EffectHostWcid };

        public string Validate(HazardRegion region, HazardArgs args, string pattern) => WispRules.Validate(region, args, pattern) ?? HazardContext.BadDamageType(args);

        public void Start(HazardContext ctx)
        {
            settings = WispSettings.From(ctx.Args);

            var spots = WispFormations.SpawnPoints(ctx.Region, settings.Count, ctx.EdgeInset, ctx.Rng,
                p => ctx.HasCell(new Vector3(p.X, p.Y, ctx.HazardZ(p.X, p.Y))));

            var wisps = new List<HazardHandle>();

            foreach (var p in spots)
            {
                var handle = ctx.Spawn(new HazardSpawnSpec
                {
                    Wcid = settings.WispWcid,
                    Position = ctx.PointFor(p.X, p.Y, HazardFollow.HazardHeight),
                    Scale = settings.Scale,
                    Moving = true,
                    Tag = "wisp",
                    HitRadius = settings.PillarRadius,
                });

                if (handle != null)
                    wisps.Add(handle);
            }

            if (wisps.Count == 0)
            {
                ctx.Say($"[hazard #{ctx.Run.Id}] The wisps could not be placed: no open spot at wisp height in this region.");
                ctx.End(HazardOutcome.Aborted, "no wisp could be spawned");
                return;
            }

            // follow=1: ONE extra wisp, at the first spawn point, that trails the first player in the region (not part of the crew)
            HazardHandle follower = null;

            if (settings.Follow && spots.Count > 0)
            {
                follower = ctx.Spawn(new HazardSpawnSpec
                {
                    Wcid = settings.WispWcid,
                    Position = ctx.PointFor(spots[0].X, spots[0].Y, HazardFollow.HazardHeight),
                    Scale = settings.Scale,
                    Moving = true,
                    Tag = "wisp-follower",
                    HitRadius = settings.PillarRadius,
                });

                if (follower == null)
                    ctx.Log("follower wisp could not be spawned; the run goes on without it");
            }

            director = new WispDirector<HazardHandle>(ctx.Region, settings, ctx.Pattern, new ContextPort(ctx, settings), wisps, follower);

            ctx.Log(FormattableString.Invariant(
                $"wisps start pattern={ctx.Pattern} n={wisps.Count}/{settings.Count} cycles={settings.Cycles} move={settings.Move:0.##}->{settings.MoveEnd ?? settings.Move:0.##} hold={settings.Hold:0.##}->{settings.HoldEnd ?? settings.Hold:0.##} burst={settings.Burst:0.##} rest={settings.Rest:0.##}->{settings.RestEnd ?? settings.Rest:0.##} hold_floor={settings.HoldFloor} follow={(follower != null ? "lag " + settings.FollowLag.ToString("0.##", CultureInfo.InvariantCulture) + "s on the crew beat" : "off")} fire={settings.Fire} pillar_radius={settings.PillarRadius:0.##} dir={settings.Dir} escape_speed={settings.EscapeSpeed:0.##} ceiling_beam={settings.CeilingBeam:0.##}"));

            director.Start();

            var holdText = settings.HoldEnd.HasValue ? $"{settings.Hold:0.#} s, tightening to {settings.HoldEnd.Value:0.#} s," : $"{settings.Hold:0.#} s";

            var when = settings.Fire == WispFire.Unison
                ? $"{holdText} after its wisp stops"
                : $"at least {holdText} after its wisp stops";

            ctx.Say(FormattableString.Invariant(
                $"[hazard #{ctx.Run.Id}] Ember wisps rise ({ctx.Pattern}, {settings.Cycles} cycle(s)). Watch where they settle: each fire pillar bursts {when}."));
        }

        public void Tick(HazardContext ctx) => director?.Tick(ctx.Now);

        public void OnStop(HazardContext ctx, HazardOutcome outcome)
            => ctx.Log(FormattableString.Invariant($"wisps stop outcome={outcome} cycle={director?.Cycle ?? 0}/{settings.Cycles} pillars={director?.Pillars ?? 0} hits={director?.Hits ?? 0} unseen={director?.Unseen ?? 0}"));

        public string Describe(HazardContext ctx) => director != null ? director.Describe() : "starting";

        public string Reveal(HazardContext ctx) => null;

        /// <summary>The director's world: one HazardContext.</summary>
        private sealed class ContextPort : IWispPort<HazardHandle>
        {
            private readonly HazardContext ctx;
            private readonly WispSettings s;

            public ContextPort(HazardContext ctx, WispSettings s)
            {
                this.ctx = ctx;
                this.s = s;
            }

            public Random Rng => ctx.Rng;

            public int? LiveCount => ctx.LiveCount;

            public HazardHandle SpawnWisp(Vector3 point)
                => ctx.Spawn(new HazardSpawnSpec
                {
                    Wcid = s.WispWcid,
                    Position = point,
                    Scale = s.Scale,
                    Moving = true,
                    Tag = "wisp",
                    HitRadius = s.PillarRadius,
                });

            public bool Alive(HazardHandle handle) => handle != null && handle.Alive;

            public Vector3 PositionAt(HazardHandle handle, double now) => handle.PositionAt(now);

            public Vector3 PointFor(float x, float y) => ctx.PointFor(x, y, HazardFollow.HazardHeight);

            public bool HasCell(Vector3 point) => ctx.HasCell(point);

            public void MoveLegs(IReadOnlyList<(HazardHandle Handle, Vector3 End)> group, double seconds, HazardMoveOptions options)
                => ctx.MoveLegs(group, seconds, options);

            public bool MoveLeg(HazardHandle handle, Vector3 end, double seconds)
                => ctx.MoveLeg(handle, end, seconds, new HazardMoveOptions { Follow = HazardFollow.HazardHeight });

            public double TickSeconds => HazardTunables.TickMs / 1000.0;

            public bool TryFollowTarget(out Vector3 position, out uint targetId)
            {
                var inRegion = ctx.PlayersInRegion();

                if (inRegion.Count == 0)
                {
                    position = default;
                    targetId = 0;
                    return false;
                }

                position = inRegion[0].Position;
                targetId = inRegion[0].Player.Guid.Full;
                return true;
            }

            public HazardHandle SpawnPillar(Vector3 point, double seconds)
                => ctx.SpawnTimedEffect(point, s.Script, Quaternion.Identity, seconds, 1f, "pillar");

            // a beam mark stands on the floor, so it is turned to rise from it (unturned, its mesh hangs under the floor);
            // any other mark_script keeps the identity orientation
            public HazardHandle SpawnDestMark(Vector3 point, double seconds)
                => ctx.SpawnTimedEffect(new Vector3(point.X, point.Y, ctx.GroundZ(point.X, point.Y)), s.MarkScript,
                    MemoryLayout.FloorOrientation(s.MarkScript), seconds, 1f, "dest-mark", grounded: true);

            public HazardHandle SpawnCeilingBeam(Vector3 pillar, double seconds)
            {
                var ceilingZ = ctx.Region.CeilingZ;

                if (!ceilingZ.HasValue)
                    return null;

                // the host hangs just under the ceiling and the mesh falls from it (identity orientation). Scale 1: a
                // particle effect ignores the host's scale (owner, 2026-10-24), so the beam is a cue from the ceiling,
                // not a column that must reach the floor
                var host = MemoryLayout.CeilingBeamPoint(new Vector2(pillar.X, pillar.Y), ceilingZ.Value);

                return ctx.SpawnTimedEffect(host, s.BeamScript, MemoryLayout.BeamOrientation(false), seconds, 1f, "ceiling-beam", false);
            }

            public void Schedule(double seconds, Action step) => ctx.Schedule(seconds, step);

            public double Now => ctx.Now;

            public bool HasCeiling => ctx.Region.CeilingZ.HasValue;

            public void Destroy(HazardHandle handle)
            {
                if (handle != null)
                    ctx.Destroy(handle);
            }

            public Vector3 RestOf(HazardHandle handle) => handle.Rest;

            public int Strike(HazardHandle source, Vector3 centre, float radius, double window)
                => ctx.HitPlayers(source, centre, radius, window, damageType: ctx.DamageType);

            public void Say(string line) => ctx.Say($"[hazard #{ctx.Run.Id}] {line}");

            public void Log(string line) => ctx.Log(line);

            public void End(HazardOutcome outcome, string reason) => ctx.End(outcome, reason);
        }
    }
}
