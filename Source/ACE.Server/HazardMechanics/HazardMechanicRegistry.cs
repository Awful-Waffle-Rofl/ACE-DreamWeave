using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Kind -> a factory for a FRESH mechanic instance per run. The four library mechanics start as
    /// <see cref="StubHazardMechanic"/>; each mechanic package replaces exactly ONE line here with its own class
    /// (e.g. <c>{ Wisps, () =&gt; new WispSquadMechanic() }</c>) and touches nothing else in this file.
    ///
    /// The two tools (move, show) are framework-owned helpers for the /hazard command; they have no settings
    /// string.
    /// </summary>
    public static class HazardMechanicRegistry
    {
        public const string Wisps = "wisps";
        public const string Balls = "balls";
        public const string Beams = "beams";
        public const string Memory = "memory";

        /// <summary>Fire pillars at a rect region's corners with a ramping dwell damage. Startable but not one of the four library <see cref="MechanicKinds"/>: it has no hazard_mech_ settings string (args come from the caller).</summary>
        public const string Zones = "zones";

        /// <summary>Falling-fire rain. Registered as a stub like <see cref="Zones"/>; NOT one of the <see cref="MechanicKinds"/> (its settings row belongs to the world event, not hazard_mech_).</summary>
        public const string Rain = "rain";

        /// <summary>A rising acid/lava sheet in a contained room. Registered like <see cref="Zones"/>: no hazard_mech_ settings string.</summary>
        public const string Fill = "fill";

        /// <summary>A translucent panel lattice that hides radar blips. Registered like <see cref="Zones"/>: no hazard_mech_ settings string.</summary>
        public const string Fog = "fog";

        public const string Move = "move";
        public const string Show = "show";

        private static readonly Dictionary<string, Func<IHazardMechanic>> factories =
            new Dictionary<string, Func<IHazardMechanic>>(StringComparer.OrdinalIgnoreCase)
            {
                { Wisps, () => new WispSquadMechanic() },
                { Balls, () => new RollingBallMechanic() },
                { Beams, () => new StubHazardMechanic(Beams) },
                { Memory, () => new MemoryMechanic() },
                { Zones, () => new ZonesMechanic() },
                { Fill, () => new FillMechanic() },
                { Rain, () => new RainMechanic() },
                { Fog, () => new FogMechanic() },

                { Move, () => new HazardMoveTestMechanic() },
                { Show, () => new HazardShowMechanic() },
            };

        /// <summary>The four library mechanics (each has a hazard_mech_&lt;kind&gt; settings string).</summary>
        public static readonly IReadOnlyList<string> MechanicKinds = new[] { Wisps, Balls, Beams, Memory };

        /// <summary>Every startable kind, mechanics first, then the tools.</summary>
        public static IReadOnlyList<string> Kinds => MechanicKinds.Concat(factories.Keys.Where(k => !MechanicKinds.Contains(k, StringComparer.OrdinalIgnoreCase)).OrderBy(k => k)).ToList();

        public static bool IsTool(string kind) => string.Equals(kind, Move, StringComparison.OrdinalIgnoreCase) || string.Equals(kind, Show, StringComparison.OrdinalIgnoreCase);

        public static bool TryCreate(string kind, out IHazardMechanic mechanic)
        {
            mechanic = null;

            if (string.IsNullOrWhiteSpace(kind) || !factories.TryGetValue(kind.Trim(), out var factory))
                return false;

            mechanic = factory();
            return mechanic != null;
        }
    }

    /// <summary>
    /// A placeholder mechanic: starts, says so, logs, and ends Completed after "stub_seconds" (default 3, clamped
    /// 0.5..60). It spawns nothing and deals no damage. It exists so the framework, the command and the registry
    /// are exercised end to end before the real mechanic lands - and its shape is the minimal template a real one
    /// follows (state in fields, numbers from ctx.Args with default + clamp, time from ctx.Now, lines via ctx.Say).
    /// </summary>
    public sealed class StubHazardMechanic : IHazardMechanic
    {
        private readonly string[] patterns;

        private double endsAt;

        public StubHazardMechanic(string kind, params string[] patterns)
        {
            Kind = kind;
            this.patterns = patterns ?? Array.Empty<string>();
        }

        public string Kind { get; }

        public IReadOnlyList<string> Patterns => patterns;

        public IEnumerable<uint> RequiredWcids(HazardArgs args) => Array.Empty<uint>();

        public string Validate(HazardRegion region, HazardArgs args, string pattern) => null;

        public void Start(HazardContext ctx)
        {
            var seconds = ctx.Args.GetDouble("stub_seconds", 3.0, 0.5, 60.0);
            endsAt = ctx.Now + seconds;

            ctx.Log($"stub start pattern={ctx.Pattern ?? "-"} for {seconds:0.#}s (mechanic not implemented yet)");
            ctx.Say(StubLine(ctx.Run.Id, Kind));
        }

        /// <summary>The one chat line a stub run says: unmistakably "nothing is happening on purpose".</summary>
        public static string StubLine(int runId, string kind)
            => $"[hazard #{runId}] '{kind}' is NOT BUILT YET - nothing will spawn or move. Only /hazard show and /hazard move are live right now.";

        public void Tick(HazardContext ctx)
        {
            if (ctx.Now >= endsAt)
                ctx.End(HazardOutcome.Completed, "stub finished");
        }

        public void OnStop(HazardContext ctx, HazardOutcome outcome) => ctx.Log($"stub stop outcome={outcome}");

        public string Describe(HazardContext ctx) => $"stub, ends at t={endsAt:0.0}s";

        public string Reveal(HazardContext ctx) => null;
    }
}
