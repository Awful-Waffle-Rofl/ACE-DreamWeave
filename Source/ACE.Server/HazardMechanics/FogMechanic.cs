using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Server.WorldObjects;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The "fog" hazard: a lattice of translucent upright panels across a contained room that players see through
    /// poorly, and (optionally) every non-player creature inside it hidden from the radar. Registered as "fog"; NOT one
    /// of HazardMechanicRegistry.MechanicKinds (no hazard_mech_ settings string; args come from the caller). Every
    /// decision lives in <see cref="FogRules"/>; this class sequences the world calls.
    ///
    /// CONTROLLABLE: /hazard control sends radar (0 off, anything else on) or stop (<see cref="ControlKeys"/>).
    /// HOW THE SHARED INVARIANTS ARE MET. Spawns: ctx.Spawn through HazardPanels.Spec, batched 32 a tick. No damage and
    /// no movement. Radar: ctx.HideFromRadar/RestoreRadar; the FRAMEWORK restores every hidden blip when the run ends
    /// (HazardContext.Teardown), so this class does no cleanup of its own. No PropertyManager read.
    /// </summary>
    public sealed class FogMechanic : IHazardMechanic, IHazardControllable
    {
        /// <summary>The most panels spawned in one tick.</summary>
        public const int SpawnsPerTick = 32;

        private static readonly string[] controlKeys = { "radar", "stop" };

        private FogSettings settings = FogSettings.From(HazardArgs.Empty, null);
        private readonly Queue<FogPanel> queue = new Queue<FogPanel>();
        private int total;
        private int spawned;
        private int skipped;
        private bool radarOn;
        private double nextScanAt;
        private volatile string status = "not started";

        public string Kind => HazardMechanicRegistry.Fog;

        public IReadOnlyList<string> Patterns => Array.Empty<string>();

        public IReadOnlyList<string> ControlKeys => controlKeys;

        public IEnumerable<uint> RequiredWcids(HazardArgs args)
        {
            yield return FogSettings.From(args, null).PanelWcid;
        }

        public string Validate(HazardRegion region, HazardArgs args, string pattern) => FogRules.Validate(region, args);

        public void Start(HazardContext ctx)
        {
            settings = FogSettings.From(ctx.Args, ctx.Region);
            radarOn = settings.Radar;

            foreach (var panel in FogRules.Lattice(ctx.Region, settings.Spacing, settings.Height, settings.BaseZ, settings.Doubled))
                queue.Enqueue(panel);

            total = queue.Count;

            ctx.Log(FormattableString.Invariant(
                $"fog start color={settings.Colour} opacity={settings.Opacity:0.##} spacing={settings.Spacing:0.##} height={settings.Height:0.##} base_z={settings.BaseZ:0.##} doubled={settings.Doubled} panels={total} radar={radarOn} radar_scan={settings.RadarScan:0.##} duration={settings.Duration:0.##}"));

            ctx.Say($"[hazard #{ctx.Run.Id}] A thick fog rolls in.");
            Refresh(ctx);
        }

        public void Tick(HazardContext ctx)
        {
            foreach (var control in ctx.TakeControls())
            {
                switch (control.Key)
                {
                    case "radar":
                        radarOn = control.Value != 0.0;
                        nextScanAt = 0.0;
                        break;
                    case "stop":
                        ctx.End(HazardOutcome.Stopped, "control stop");
                        return;
                    default:
                        ctx.Log($"fog ignores unknown control '{control.Key}'");
                        break;
                }
            }

            if (settings.Duration > 0.0 && ctx.Now >= settings.Duration)
            {
                ctx.End(HazardOutcome.Completed, "duration elapsed");
                return;
            }

            for (var i = 0; i < SpawnsPerTick && queue.Count > 0; i++)
            {
                var panel = queue.Dequeue();

                if (!ctx.HasCell(panel.Centre, false))
                {
                    skipped++;
                    continue;
                }

                var handle = ctx.Spawn(HazardPanels.Spec(settings.PanelWcid, panel.Centre, panel.Rotation, panel.Edge, settings.Opacity, false, "fog-panel"));

                if (handle == null)
                    skipped++;
                else
                    spawned++;
            }

            if (ctx.Now >= nextScanAt)
            {
                nextScanAt = ctx.Now + settings.RadarScan;
                ScanRadar(ctx);
            }

            Refresh(ctx);
        }

        private void ScanRadar(HazardContext ctx)
        {
            var byGuid = new Dictionary<uint, Creature>();
            var inside = new List<uint>();

            if (radarOn)
            {
                foreach (var (creature, _) in ctx.CreaturesInRegion())
                {
                    byGuid[creature.Guid.Full] = creature;
                    inside.Add(creature.Guid.Full);
                }
            }

            var plan = FogRules.RadarPlan(inside, ctx.RadarHidden, radarOn);

            foreach (var guid in plan.Hide)
                ctx.HideFromRadar(byGuid[guid]);

            foreach (var guid in plan.Restore)
                ctx.RestoreRadar(guid);
        }

        private void Refresh(HazardContext ctx)
            => status = string.Format(CultureInfo.InvariantCulture, "panels={0}/{1} skipped={2} hidden={3} radar={4}", spawned, total, skipped, ctx.RadarHidden.Count, radarOn ? "on" : "off");

        public void OnStop(HazardContext ctx, HazardOutcome outcome)
            => ctx.Log(FormattableString.Invariant($"fog stop outcome={outcome} {status}"));

        public string Describe(HazardContext ctx) => status;

        public string Reveal(HazardContext ctx) => null;
    }
}
