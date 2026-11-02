using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using ACE.Server.WorldObjects;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The "fill" hazard: a sheet of panels rising through a contained room (acid or lava) with wisps riding just
    /// under the surface; everyone below the surface takes a flat, resist-mitigated hit every tick. Registered as
    /// "fill"; NOT one of HazardMechanicRegistry.MechanicKinds (no hazard_mech_ settings string; args come from the
    /// caller). THIS CLASS ONLY ADAPTS A <see cref="HazardContext"/> TO THE DIRECTOR'S PORT; every decision lives in
    /// <see cref="FillRules"/>/<see cref="FillSurface"/> and the run in <see cref="FillDirector{THandle, TPlayer}"/>.
    ///
    /// CONTROLLABLE: /hazard control sends rate, target, set_z, damage_scale or stop (<see cref="ControlKeys"/>).
    /// HOW THE SHARED INVARIANTS ARE MET. Spawns: ctx.Spawn (panels through HazardPanels.Spec). Movement: ctx.MoveLegs,
    /// ctx.StopLeg. Damage: ctx.HitPlayerFlat. Geometry: ctx.Region only. No PropertyManager read.
    /// </summary>
    public sealed class FillMechanic : IHazardMechanic, IHazardControllable, IHazardLevelSource
    {
        private static readonly string[] controlKeys = { "rate", "target", "set_z", "damage_scale", "stop" };

        private FillDirector<HazardHandle, Player> director;

        private HazardContext levelCtx;

        public string Kind => HazardMechanicRegistry.Fill;

        public IReadOnlyList<string> Patterns => Array.Empty<string>();

        public IReadOnlyList<string> ControlKeys => controlKeys;

        public IEnumerable<uint> RequiredWcids(HazardArgs args)
        {
            var s = FillSettings.From(args, null);

            yield return s.PanelWcid;

            if (s.Wisps > 0)
                yield return s.WispWcid;
        }

        public string Validate(HazardRegion region, HazardArgs args, string pattern) => FillRules.Validate(region, args);

        public void Start(HazardContext ctx)
        {
            var settings = FillSettings.From(ctx.Args, ctx.Region);

            director = new FillDirector<HazardHandle, Player>(ctx.Region, settings, new ContextPort(ctx, settings));
            levelCtx = ctx;

            ctx.Log(FormattableString.Invariant(
                $"fill start element={settings.Element} color={settings.Colour} floor_z={settings.FloorZ:0.##} max_z={settings.MaxZ:0.##} start_z={settings.StartZ:0.##} rate={settings.Rate:0.###} tick={settings.Tick:0.##} damage={settings.Damage:0.##}+{settings.DamagePerM:0.##}/m max {settings.DamageMax:0.##} scale={settings.DamageScale:0.##} damage_type={settings.DamageType} opacity={settings.Opacity:0.##} doubled={settings.Doubled} wisps={settings.Wisps} resnap={settings.Resnap:0.##} duration={settings.Duration:0.##}"));

            director.Start(ctx.Now);
        }

        public void Tick(HazardContext ctx) => director?.Tick(ctx.Now);

        public void OnStop(HazardContext ctx, HazardOutcome outcome)
        {
            levelCtx = null;

            ctx.Log(FormattableString.Invariant($"fill stop outcome={outcome} hits={director?.Hits ?? 0} tiles_missed={director?.TilesMissed ?? 0} wisps_missed={director?.WispsMissed ?? 0}"));
        }

        /// <summary>The surface z at the run clock; false (0.0) before Start and after OnStop. Landblock thread only.</summary>
        public bool TryGetLevel(out double level)
        {
            if (director == null || levelCtx == null)
            {
                level = 0.0;
                return false;
            }

            level = director.SurfaceAt(levelCtx.Now);
            return true;
        }

        public string Describe(HazardContext ctx) => director != null ? director.Describe() : "starting";

        public string Reveal(HazardContext ctx) => null;

        /// <summary>The director's world: one HazardContext.</summary>
        private sealed class ContextPort : IFillPort<HazardHandle, Player>
        {
            private readonly HazardContext ctx;
            private readonly FillSettings s;

            public ContextPort(HazardContext ctx, FillSettings s)
            {
                this.ctx = ctx;
                this.s = s;
            }

            public Random Rng => ctx.Rng;

            public HazardHandle SpawnPanel(Vector3 centre, float edge, bool faceDown, bool moving)
                => ctx.Spawn(HazardPanels.Spec(s.PanelWcid, centre, faceDown ? HazardPanels.FaceDown : HazardPanels.FaceUp, edge, s.Opacity, moving, moving ? "fill-surface" : "fill-under"));

            public HazardHandle SpawnWisp(Vector3 at)
                => ctx.Spawn(new HazardSpawnSpec
                {
                    Wcid = s.WispWcid,
                    Position = at,
                    Scale = s.WispScale,
                    Moving = true,
                    Tag = "fill-wisp",
                });

            public bool HasCell(Vector3 point) => ctx.HasCell(point);

            public bool Alive(HazardHandle handle) => handle != null && handle.Alive;

            public int Move(IReadOnlyList<(HazardHandle Handle, Vector3 End)> group, double seconds)
                => ctx.MoveLegs(group, seconds, new HazardMoveOptions { Follow = HazardFollow.Straight });

            public void Halt(HazardHandle handle) => ctx.StopLeg(handle);

            public void Destroy(HazardHandle handle)
            {
                if (handle != null)
                    ctx.Destroy(handle);
            }

            public IReadOnlyList<(Player Player, Vector3 Feet)> Players()
            {
                var list = ctx.PlayersInRegion();
                var result = new List<(Player, Vector3)>(list.Count);

                foreach (var entry in list)
                    result.Add((entry.Player, entry.Position));

                return result;
            }

            public int HitFlat(HazardHandle source, Player player, double flat)
                => ctx.HitPlayerFlat(source, player, flat, s.DamageType);

            public IReadOnlyList<HazardControl> TakeControls() => ctx.TakeControls();

            public void Say(string line) => ctx.Say($"[hazard #{ctx.Run.Id}] {line}");

            public void Log(string line) => ctx.Log(line);

            public void End(HazardOutcome outcome, string reason) => ctx.End(outcome, reason);
        }
    }
}
