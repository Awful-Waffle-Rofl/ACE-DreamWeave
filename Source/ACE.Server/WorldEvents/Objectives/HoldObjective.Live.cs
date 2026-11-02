using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// What the Hold wave cadence reads from and sends through (HoldObjective.TickCadence). Production wraps the run's spawner
    /// and wave step; a test supplies a fake, so the cadence is driven without a landblock. World thread.
    /// </summary>
    internal interface IHoldCadenceHost
    {
        /// <summary>At least one knight has been placed.</summary>
        bool KnightsLanded { get; }

        /// <summary>Wave monsters still standing.</summary>
        int LiveCount { get; }

        /// <summary>The source's own wave interval (wave_interval_seconds 0 falls back to it).</summary>
        double SourceWaveIntervalSeconds { get; }

        /// <summary>Sends one wave through the run's wave step; true when a pick was placed.</summary>
        bool SendWave(double now, bool initial, string trigger, WorldEventObjectiveWave wave);

        /// <summary>A wave went out: the portals, the wave-start line and the phase lines.</summary>
        void AfterWave(int waveNumber, int target, HoldWavePlan plan);
    }

    /// <summary>
    /// World Events Hold H6: the production half of <see cref="HoldObjective"/>. With a run attached
    /// (<see cref="AttachEvent"/>, done by WorldEventObjectiveFactory) the objective places the knights and their scene at
    /// OnStaged, runs its own wave cadence (a wave-based hold replaces the core's), sends the finale boss alone and its escort
    /// after it, times the finale's adds, announces each wave and phase, and drives the run's ticks
    /// (WorldEventHoldRun.TickRuntime). It also answers the run-shaping hooks (adjacent landblocks, arena and participant
    /// radius, idle window, local lines, staff counting, outcome tokens, the claim policy). Without a run attached (every
    /// unit test that builds the objective from plain funcs) none of this runs and the objective is the H0 rules alone.
    ///
    /// THREADING: every member here runs on the world thread (IWorldEventObjective's contract). The spawner places what it
    /// is handed on the landblock's own thread; the run's ticks enqueue every creature change on that creature's landblock.
    /// </summary>
    public sealed partial class HoldObjective
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The wave resolve grace when the tuning gives none (wave_resolve_grace_seconds' own default).</summary>
        public const double DefaultWaveResolveGraceSeconds = 15.0;

        private WorldEvent evt;

        private readonly Random rng = new Random();

        private WorldEventHoldWavePlanner planner;

        private bool stagedOnce;
        private WorldEventHoldTuning previewTuning;

        private int wavesStarted;
        private double lastWaveStartedAt;
        private int lastWaveSize;
        private double nextAttemptAt;

        private PendingEscort escort;

        private uint finaleBossWcid;
        private LevelBand? lastBand;
        private double lastHealthMult = 1.0;
        private double lastAddsAt;

        /// <summary>The rest of the final wave, waiting for its turn after the boss.</summary>
        private sealed class PendingEscort
        {
            public double At;
            public WavePick Pick;
            public HoldWavePlan Plan;
            public int WaveIndex;
            public LevelBand Band;
            public double HealthMult;
        }

        /// <summary>
        /// World thread, once, before the run stages (WorldEventObjectiveFactory): the run this objective serves. Turns on
        /// every production behaviour in this file.
        /// </summary>
        public void AttachEvent(WorldEvent worldEvent) => evt = worldEvent;

        /// <summary>
        /// The tuning a hook reads: the staged tuning once OnStaged ran, else one preview resolve for the goal
        /// (WorldEventHoldRun.PreviewTuning), kept until OnStaged. WorldEvent.Stage reads the arena radius, the idle window
        /// and the adjacent-landblock hold before OnStaged.
        /// </summary>
        private WorldEventHoldTuning HookTuning => stagedOnce ? Run.Tuning : (previewTuning ??= Run.PreviewTuning(goal));

        // ---- knights --------------------------------------------------------------------------------------------------

        /// <summary>Every knight ever placed: the spawner's adopted set for a production run, else the constructor's func.</summary>
        private IReadOnlyCollection<uint> PlacedGuids()
        {
            if (evt != null)
                return evt.Spawner.KnightGuids;

            return protectedGuids() ?? Array.Empty<uint>();
        }

        /// <summary>The placed knights still standing: the spawner's live set for a production run, else the constructor's func.</summary>
        private IReadOnlyCollection<uint> LiveGuids()
        {
            if (evt != null)
                return evt.Spawner.LiveKnights.Select(k => k.Guid.Full).ToList();

            return liveProtectedGuids() ?? Array.Empty<uint>();
        }

        /// <summary>The standing knights' health ("Envoy Draithon 80%, ..."), for {targetHealth} and the wave-start line.</summary>
        private string LiveTargetHealth()
        {
            try
            {
                return WorldEventHoldRules.TargetHealthSummary(evt.Spawner.LiveKnights.Select(c => (c.Name, c.Health.Current, c.Health.MaxValue)));
            }
            catch
            {
                return "";
            }
        }

        // ---- waves ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// The wave target for <paramref name="participants"/>: wave_count_* over protect.waves (WorldEventHoldRuntimeTuning.WaveCount),
        /// 0 for a time-only hold.
        /// </summary>
        private int ResolveWaveTarget(int participants)
        {
            var scaled = HookTuning.Runtime.WaveCount(Protect?.Waves);

            return scaled?.Resolve(Math.Max(0, participants)) ?? 0;
        }

        /// <summary>A production run's wave counts: waves this objective started, and what the spawner has seen of them.</summary>
        private HoldWaveCounts LiveWaveCounts()
        {
            var spawner = evt.Spawner;
            var grace = Run.Tuning.WaveResolveGraceSeconds > 0 ? Run.Tuning.WaveResolveGraceSeconds : DefaultWaveResolveGraceSeconds;

            return new HoldWaveCounts(wavesStarted, spawner.LandedWaveCount, spawner.ClearedWaveCount, spawner.LiveCount,
                WorldEventHoldRules.ResolvedWaves(wavesStarted, spawner.ResolvedWaveCount, now, lastWaveStartedAt, grace, escort != null));
        }

        /// <summary>True when wave <paramref name="waveNumber"/> (1-based) is the last counted wave.</summary>
        public bool IsFinalWave(int waveNumber) => IsWaveBased && waveNumber >= TargetWaves;

        // ---- OnStaged and Tick (production) ------------------------------------------------------------------------------

        /// <summary>World thread, from OnStaged after the run's settings were staged: the planner, the voice and the knights.</summary>
        private void StageLive(double stagedAt)
        {
            planner = new WorldEventHoldWavePlanner(goal, Run.Tuning, rng);

            Run.AttachVoice(new Voice(evt));
            Run.SetParticipantTest(evt.IsPresenceParticipant);

            try
            {
                evt.Spawner.SpawnProtected(evt, Protect, evt.Composition?.AnchorPosition, Run, AreaPlan);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} hold knights spawn threw", ex);
            }
        }

        /// <summary>World thread, every Active tick: the spawner's views, this objective's cadence, then the run's ticks.</summary>
        private void TickLive(double tickNow)
        {
            try
            {
                evt.Spawner.PublishHoldTo(Run);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} hold publish threw", ex);
            }

            if (IsWaveBased)
            {
                try
                {
                    TickCadence(tickNow);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} hold wave cadence threw", ex);
                }
            }

            Run.TickRuntime(tickNow, evt.Spawner.ClearedWaveCount);
        }

        /// <summary>
        /// The Hold wave cadence (replaces WorldEvent.TickWaves for a wave-based hold): the finale escort and adds first, then
        /// the next counted wave - the first once the knights have landed and first_wave_delay_seconds have passed (or after a
        /// placement grace on top of that), every later one by WorldEventHoldRules.HoldNextWaveDue. No wave after the last
        /// counted one has started; the field is then left to clear.
        /// </summary>
        private void TickCadence(double tickNow)
        {
            var host = Host;

            if (host == null)
                return;

            var runtime = Run.Tuning.Runtime;

            TickEscort(tickNow);
            TickFinaleAdds(tickNow);

            if (escort != null)
                return;

            var target = TargetWaves;

            if (!WorldEventHoldRules.WantsAnotherWave(wavesStarted, target) || tickNow < nextAttemptAt)
                return;

            string trigger;

            if (wavesStarted == 0)
            {
                var since = Math.Max(0, tickNow - activeAt());
                var delay = Math.Max(0, runtime.FirstWaveDelaySeconds);
                var landed = host.KnightsLanded;

                if (!WorldEventHoldRules.FirstWaveMayStart(landed && since >= delay, since, delay + PlacementGraceSeconds))
                    return;

                trigger = "initial";
            }
            else
            {
                var live = host.LiveCount;
                var interval = runtime.EffectiveWaveIntervalSeconds(host.SourceWaveIntervalSeconds);

                if (!WorldEventHoldRules.HoldNextWaveDue(tickNow, lastWaveStartedAt, interval, runtime.BreatherSeconds,
                        live, lastWaveSize, runtime.NextWaveAliveShare, runtime.NextWaveRequiresBoth, runtime.MaxWaveIntervalSeconds))
                    return;

                trigger = live == 0 ? "cleared" : "interval";
            }

            SendWave(tickNow, trigger, host);
        }

        /// <summary>
        /// One counted wave through the run's ordinary wave step (WorldEvent.SpawnObjectiveWave): sized by wave_size_* over
        /// protect.waveSize with protect.maxAlive, the final wave final_wave_bonus bigger, an assault wave carrying its
        /// corrupted on top, then planned and placed by <see cref="PlaceWave"/>, its portals flared and its lines said.
        ///
        /// Whether this is the final wave is read FRESH at every use, never captured before the send: the wave step re-samples
        /// the audience (OnAudienceResampled) before it sizes the pick, and the target is read after that.
        /// </summary>
        private void SendWave(double tickNow, string trigger, IHoldCadenceHost host)
        {
            var waveNumber = wavesStarted + 1;
            var protect = Protect;
            var runtime = Run.Tuning.Runtime;
            var spawnerTuning = Run.Tuning.Spawner;

            bool FinalNow() => waveNumber >= TargetWaves;

            int AssaultExtra() => !FinalNow() && spawnerTuning.HasWaveMix && spawnerTuning.AssaultMode
                && WorldEventHoldRules.IsAssaultWave(waveNumber, spawnerTuning.AssaultEveryWaves, spawnerTuning.AssaultFromWave, false)
                ? Math.Clamp(spawnerTuning.AssaultCount, 1, 5)
                : 0;

            HoldWavePlan plan = null;
            var size = 0;

            var wave = new WorldEventObjectiveWave
            {
                SizingTheme = theme => theme?.WithHoldSizing(runtime.WaveSize(protect?.WaveSize), protect?.MaxAlive ?? 0),
                ExtraQuantity = theme => (FinalNow() ? WorldEventHoldRules.FinalWaveBonus(theme?.EffectiveWaveCountBase ?? 0, runtime.FinalWaveBonus) : 0) + AssaultExtra(),
                Place = pick => plan = PlaceWave(pick, waveNumber, TargetWaves, out size),

                // The wave-start line (WaveStartText) and the phase lines replace the theme's generic wave chatter.
                SkipThemeWaveLine = true
            };

            bool placed;

            try
            {
                placed = host.SendWave(tickNow, wavesStarted == 0, trigger, wave);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={Run.RunId} hold wave {waveNumber} threw", ex);
                placed = false;
            }

            var target = TargetWaves;

            // A production wave whose planner gave no plan placed nothing: retry, never advance the wave counter. (A test host
            // never runs Place, so it has no plan by design.)
            if (!placed || (plan == null && evt != null))
            {
                // The alive cap (or an empty roster) left nothing to send: try again after a breather, not every second.
                nextAttemptAt = tickNow + Math.Max(5, runtime.BreatherSeconds);
                log.Info($"[WORLDEVENT] run={Run.RunId} hold wave {waveNumber} of {target} not sent ({trigger}); retrying at +{nextAttemptAt - tickNow:0}s");
                return;
            }

            var final = waveNumber >= target;

            wavesStarted = waveNumber;
            lastWaveStartedAt = tickNow;
            lastWaveSize = size;
            LastWaveWasFinal = final;
            Run.SetWaveNumber(waveNumber);

            log.Info($"[WORLDEVENT] run={Run.RunId} hold wave {waveNumber} of {target} ({trigger}){(final ? " final" : "")} size={size} plan={plan}");

            try
            {
                host.AfterWave(waveNumber, target, plan);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={Run.RunId} hold wave {waveNumber} announcement threw", ex);
            }
        }

        // ---- the cadence's view of the run (a seam, so the cadence is testable without a landblock) ---------------------

        private IHoldCadenceHost cadenceHost;

        /// <summary>The cadence's host: the test host when one was attached, else the production run's (null without a run).</summary>
        private IHoldCadenceHost Host => cadenceHost ??= evt != null ? new EventCadenceHost(this) : null;

        /// <summary>Waves this objective has started (sent). World thread.</summary>
        internal int WavesStarted => wavesStarted;

        /// <summary>Whether the last wave sent was the final counted wave. World thread.</summary>
        internal bool LastWaveWasFinal { get; private set; }

        /// <summary>Test seam: the cadence reads and sends through <paramref name="host"/> instead of a live run.</summary>
        internal void AttachCadenceHostForTest(IHoldCadenceHost host) => cadenceHost = host;

        /// <summary>Test seam: one cadence tick.</summary>
        internal void TickCadenceForTest(double tickNow) => TickCadence(tickNow);

        /// <summary>The production host: the run's spawner, its wave step and its lines.</summary>
        private sealed class EventCadenceHost : IHoldCadenceHost
        {
            private readonly HoldObjective o;

            public EventCadenceHost(HoldObjective objective) => o = objective;

            public bool KnightsLanded => o.evt.Spawner.HoldKnightsLanded;

            public int LiveCount => o.evt.Spawner.LiveCount;

            public double SourceWaveIntervalSeconds => o.evt.Composition?.Source?.EffectiveWaveIntervalSeconds ?? 0;

            public bool SendWave(double now, bool initial, string trigger, WorldEventObjectiveWave wave) =>
                o.evt.SpawnObjectiveWave(now, initial, trigger, wave);

            public void AfterWave(int waveNumber, int target, HoldWavePlan plan)
            {
                o.Run.PulsePortals(o.evt.Spawner.LiveSceneObjects);

                WorldEventAnnouncer.Local(o.evt, o.WaveStartText(waveNumber));

                o.AnnounceWave(plan, waveNumber, target);
            }
        }

        /// <summary>
        /// The pick the run handed over: planned (WorldEventHoldWavePlanner.BuildPlan) and placed at the portals. A finale with a
        /// boss sends the boss alone and holds the rest of the wave back for finale_lead_seconds. <paramref name="size"/> is
        /// how many monsters the wave sends in all, for the cadence's "most of the wave is dead" rule.
        /// </summary>
        private HoldWavePlan PlaceWave(WorldEventObjectiveWavePick p, int waveNumber, int target, out int size)
        {
            size = 0;

            if (evt == null || planner == null)
                return null;

            var pick = p.Pick ?? WavePick.Empty;
            var roster = evt.Composition?.Roster;
            IReadOnlyList<FamilyMember> members = roster?.Members ?? new List<FamilyMember>();

            var champion = ChampionPick.None;
            string championName = null;

            if (waveNumber >= target)
                champion = PickFinaleChampion(roster, out championName);

            var request = new HoldWaveRequest
            {
                WaveNumber = waveNumber,
                TargetWaves = target,
                Pick = pick,
                Knights = KnightSlots(),
                RangedPool = WorldEventRangedMembers.Pool(members, p.Band),
                IsCaster = WorldEventRangedMembers.IsCasterWcid,
                Members = members,
                Champion = champion,
                ChampionName = championName
            };

            var plan = planner.BuildPlan(request);
            var anchors = WaveAnchors();

            lastBand = p.Band;
            lastHealthMult = p.HealthMult;

            if (plan.FinaleBossOnly)
            {
                evt.Spawner.SpawnHoldWave(evt, WavePick.Empty, anchors, p.WaveIndex, p.Band, p.HealthMult, plan, Run);

                var escortPlan = planner.BuildEscortPlan(plan, pick);
                var lead = Math.Clamp(Run.Tuning.Spawner.FinaleLeadSeconds, 0, 30);

                escort = new PendingEscort { At = p.Now + lead, Pick = pick, Plan = escortPlan, WaveIndex = p.WaveIndex, Band = p.Band, HealthMult = p.HealthMult };
                finaleBossWcid = plan.ChampionWcid;

                log.Info($"[WORLDEVENT] run={evt.RunId} hold finale: boss {plan.ChampionName} sent alone; the rest of the wave ({pick.Total}) follows in {lead:0.#} s");

                size = 1 + pick.Total;
            }
            else
            {
                evt.Spawner.SpawnHoldWave(evt, pick, anchors, p.WaveIndex, p.Band, p.HealthMult, plan, Run);

                size = plan.KeepCount is int keep ? Math.Min(pick.Total, Math.Max(1, keep)) : pick.Total;
            }

            return plan;
        }

        /// <summary>
        /// The finale's boss: finale_boss_wcid when set, else the roster's champion for the audience shifted by
        /// finale_level_offset, else the nearest-level member promoted (RosterSelector.PickFinaleBossFallback).
        /// </summary>
        private ChampionPick PickFinaleChampion(FamilyDef roster, out string name)
        {
            var spawnerTuning = Run.Tuning.Spawner;
            var audience = ShiftAudience(evt.Audience, spawnerTuning.FinaleLevelOffset);

            var champion = spawnerTuning.FinaleBossWcid is uint forced
                ? new ChampionPick(forced, false)
                : WorldEventRosterSelector.PickChampion(roster, audience);

            if (champion.Wcid == 0)
                champion = WorldEventRosterSelector.PickFinaleBossFallback(roster, audience);

            name = roster?.Members?.Find(m => m != null && m.Wcid == champion.Wcid)?.Name;

            if (champion.Wcid == 0)
                log.Warn($"[WORLDEVENT] run={evt.RunId} hold finale: the family has no member to make a boss from; finale without one");

            return champion;
        }

        /// <summary>An audience estimate with every level moved by <paramref name="offset"/> (pure; 0 hands it back unchanged).</summary>
        public static AudienceEstimate ShiftAudience(AudienceEstimate audience, int offset)
        {
            if (offset == 0)
                return audience;

            int Move(int level) => Math.Clamp(level + offset, 1, 999);

            var levels = (audience.Levels ?? Array.Empty<int>()).Select(Move).ToList();

            return new AudienceEstimate(audience.Count, Move(audience.MedianLevel), Move(audience.P90Level), levels, audience.PowerSum);
        }

        /// <summary>The standing knights as the planner sees them: guid, name and offset from the anchor in metres.</summary>
        private List<HoldKnightSlot> KnightSlots()
        {
            var anchor = evt.Composition?.AnchorPosition;
            var slots = new List<HoldKnightSlot>();

            if (anchor == null)
                return slots;

            var a = anchor.ToGlobal();

            foreach (var k in Run.Knights)
            {
                if (k == null || k.IsDead || k.IsDestroyed || k.Location == null)
                    continue;

                var g = k.Location.ToGlobal();

                slots.Add(new HoldKnightSlot(k.Guid.Full, k.Name, g.X - a.X, g.Y - a.Y));
            }

            return slots;
        }

        /// <summary>
        /// Where a wave spawns: one point per portal, inset metres inside it toward the anchor, at the start-spot rule's
        /// portal spots when it made a plan (else protect.portals' own ring, with portal_count / portal_radius applied).
        /// A goal with no portals spawns on a ring of four at spawn_radius.
        /// </summary>
        private IReadOnlyList<Position> WaveAnchors()
        {
            var anchor = evt.Composition?.AnchorPosition;
            var result = new List<Position>();

            if (anchor == null)
                return result;

            var spawnerTuning = Run.Tuning.Spawner;
            var portals = WorldEventHoldStartSpotRule.EffectivePortals(Protect?.Portals, spawnerTuning);

            if (portals != null)
            {
                var inset = Math.Max(0, portals.Inset);
                var spots = AreaPlan?.Portals;

                if (spots != null && spots.Count > 0)
                {
                    foreach (var spot in spots)
                        result.Add(WorldEventGeometry.AtBearing(anchor, (float)Math.Max(1.0, spot.Radius - inset), spot.Bearing * Math.PI / 180.0));
                }
                else
                {
                    var radius = (float)Math.Max(1.0, portals.Radius - inset);

                    foreach (var bearing in WorldEventHoldRules.PortalBearings(Math.Max(1, portals.Count), portals.RotationDegrees))
                        result.Add(WorldEventGeometry.AtBearing(anchor, radius, bearing));
                }

                if (result.Count > 0)
                    return result;
            }

            var ring = (float)Math.Max(1.0, spawnerTuning.SpawnRadius > 0 ? spawnerTuning.SpawnRadius : 20.0);

            foreach (var bearing in WorldEventHoldRules.PortalBearings(4, 45))
                result.Add(WorldEventGeometry.AtBearing(anchor, ring, bearing));

            return result;
        }

        /// <summary>The rest of the final wave, once its lead time is up.</summary>
        private void TickEscort(double tickNow)
        {
            var pending = escort;

            if (evt == null || pending == null || tickNow < pending.At)
                return;

            escort = null;
            lastAddsAt = tickNow;

            try
            {
                evt.Spawner.SpawnHoldWave(evt, pending.Pick, WaveAnchors(), pending.WaveIndex, pending.Band, pending.HealthMult, pending.Plan, Run);
                Run.PulsePortals(evt.Spawner.LiveSceneObjects);

                log.Info($"[WORLDEVENT] run={evt.RunId} hold finale: the rest of the final wave arrives ({pending.Pick.Total} monsters)");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} hold finale escort spawn threw", ex);
            }
        }

        /// <summary>finale_add_count adds every finale_add_interval_seconds while the finale boss stands (after its escort).</summary>
        private void TickFinaleAdds(double tickNow)
        {
            var spawnerTuning = Run.Tuning.Spawner;

            if (evt == null || finaleBossWcid == 0 || escort != null || spawnerTuning.FinaleAddCount <= 0)
                return;

            if (tickNow - lastAddsAt < Math.Max(5, spawnerTuning.FinaleAddIntervalSeconds))
                return;

            var bossStanding = Run.WaveMonsters.Any(m => m != null && !m.IsDead && !m.IsDestroyed && m.WorldEventHold?.Kind == HoldUnitKind.FinaleBoss);

            if (!bossStanding)
                return;

            lastAddsAt = tickNow;

            evt.Spawner.SpawnHoldFinaleAdds(evt, evt.Composition?.Roster?.Members, lastBand, finaleBossWcid, WaveAnchors(), lastHealthMult, Run, rng);
        }

        // ---- the wave and phase lines -------------------------------------------------------------------------------------

        /// <summary>
        /// A special wave or the finale is a phase, announced so nobody can miss it ("Wave 4 of 7: ...", "Final wave: ..."), and
        /// a knight says one of the type's lines in local chat (for a breach, the threatened knight). A mixed wave announces the
        /// goal's waveMix line, the breach target calls its side, and the warded ranged and the corrupted (or each assault
        /// call) get a line of their own. Nothing for an ordinary wave.
        /// </summary>
        private void AnnounceWave(HoldWavePlan plan, int waveNumber, int target)
        {
            if (plan == null || plan.FinaleEscort || plan.Type == HoldWaveType.Normal)
                return;

            var protect = Protect;

            List<string> Fill(IReadOnlyList<string> pool) =>
                pool?.Select(l => WorldEventHoldRules.FillWaveTokens(l, plan.BreachSide, plan.ChampionName, plan.BreachTargetName)).ToList();

            Creature KnightByGuid(uint guid) => Run.Knights.FirstOrDefault(k => k != null && !k.IsDead && k.Guid.Full == guid);

            if (plan.Type == HoldWaveType.Mixed)
            {
                var mixAnnounce = Run.ComposeLine(Fill(protect?.WaveMix?.Announce));

                if (!string.IsNullOrEmpty(mixAnnounce))
                    evt.AnnounceInRun(WorldEventHoldRules.PhaseLine(WorldEventHoldRules.PhaseLabel(waveNumber, target, false), mixAnnounce));

                var breachTarget = KnightByGuid(plan.BreachTargetGuid);

                if (breachTarget != null)
                    Run.SayQueued(breachTarget, Fill(protect?.SpecialWave(HoldWaveType.Breach)?.Lines));

                if (plan.Warded && plan.Roles != null && plan.Roles.Contains(HoldRole.Ranged))
                {
                    var wardLine = Run.ComposeLine(Fill(protect?.Ward?.Announce));

                    if (!string.IsNullOrEmpty(wardLine))
                        evt.AnnounceInRun(WorldEventAnnouncer.Global(wardLine));
                }

                var corrupted = protect?.EffectiveCorrupted;

                if (plan.Assault && plan.AssaultCalls != null && plan.AssaultCalls.Count > 0)
                {
                    var pool = corrupted?.AssaultAnnounce ?? new List<string>();

                    for (var k = 0; k < plan.AssaultCalls.Count; k++)
                    {
                        var call = plan.AssaultCalls[k];
                        var monster = call.Monster ?? AssaultMonsterName(plan, k, corrupted);
                        var template = WorldEventHoldRules.PickLine(pool, rng);
                        var line = Run.ComposeLine(new List<string> { WorldEventHoldRules.AssaultLine(template, monster, call.Target) });

                        if (!string.IsNullOrEmpty(line))
                            evt.AnnounceInRun(WorldEventAnnouncer.Global(line));
                    }
                }
                else if (plan.Roles != null && plan.Roles.Contains(HoldRole.Corrupted))
                {
                    var corruptedLine = Run.ComposeLine(Fill(corrupted?.Announce));

                    if (!string.IsNullOrEmpty(corruptedLine))
                        evt.AnnounceInRun(WorldEventAnnouncer.Global(corruptedLine));
                }

                return;
            }

            var def = protect?.SpecialWave(plan.Type);

            if (def == null)
                return;

            var announce = Run.ComposeLine(Fill(def.Announce));

            if (!string.IsNullOrEmpty(announce))
                evt.AnnounceInRun(WorldEventHoldRules.PhaseLine(WorldEventHoldRules.PhaseLabel(waveNumber, target, plan.Type == HoldWaveType.Finale), announce));

            var speaker = plan.Type == HoldWaveType.Breach ? KnightByGuid(plan.BreachTargetGuid) : null;
            speaker ??= Run.RandomStandingKnight();

            if (speaker == null)
                return;

            var spoken = def.Lines != null && def.Lines.Count > 0 ? Run.ComposeLine(Fill(def.Lines)) : announce;

            Run.SayQueued(speaker, spoken);
        }

        /// <summary>
        /// The name an assault call uses before the spawner has named the monster (the batch is placed a moment later on its
        /// landblock): the corrupted title in front of the roster member's name.
        /// </summary>
        private string AssaultMonsterName(HoldWavePlan plan, int k, CorruptedDef corrupted)
        {
            if (plan.CorruptedTargets == null)
                return null;

            var slots = plan.CorruptedTargets.Keys.OrderBy(i => i).ToList();

            if (k < 0 || k >= slots.Count)
                return null;

            var index = slots[k];
            var trash = plan.ReplacedTrash;
            var wcid = trash != null && index < trash.Count ? trash[index] : 0u;
            var name = evt.Composition?.Roster?.Members?.Find(m => m != null && m.Wcid == wcid)?.Name;

            return string.IsNullOrWhiteSpace(name) ? null : WorldEventHoldRules.Titled(corrupted?.Prefix, name);
        }

        // ---- the run-shaping hooks (IWorldEventObjective) ---------------------------------------------------------------

        /// <summary>A Hold run always holds the anchor's eight neighbours: its portals and its ring reach across the edges.</summary>
        public bool HoldsAdjacentLandblocks => true;

        /// <summary>participant_radius when set (the presence arena's radius), else the theme's rule.</summary>
        public float? ArenaRadius => PositiveRadius(HookTuning.ParticipantRadius);

        /// <summary>participant_radius when set: presence, audience, local lines and the cache count all count within it.</summary>
        public float? ParticipantRadius => PositiveRadius(HookTuning.ParticipantRadius);

        /// <summary>afk_seconds: a defender who neither moves nor fights for this long counts as outside (0 = off).</summary>
        public double PresenceAfkSeconds => HookTuning.AfkSeconds;

        /// <summary>announce_locally: the in-run lines go only to the players near the fight.</summary>
        public bool AnnouncesLocally => HookTuning.AnnouncesLocally;

        /// <summary>admins_count: staff count toward the audience and presence.</summary>
        public bool CountsStaffTowardAudience => HookTuning.Runtime.AdminsCount;

        /// <summary>The progress line stays as it stood at Finish (cleanup removes the knights it counts).</summary>
        public bool FreezesProgressAtFinish => true;

        /// <summary>The outcome template with the final {alive}/{total}/{lost} and {family}; a success with none gets the Hold default.</summary>
        public string OutcomeTemplate(string template, bool success) =>
            WorldEventHoldRules.OutcomeTemplate(template, success, Alive, Placed, family());

        /// <summary>
        /// The Hold claim policy (H7): failure_reward for a failed run, min_presence_seconds and min_contribution as the coal
        /// thresholds (under either pays coal, owner ruling 2026-11-01), and participants_per_cache for the cache count.
        /// </summary>
        public WorldEventClaimPolicy ClaimPolicy => ClaimPolicyFrom(HookTuning.Runtime);

        /// <summary>The claim policy a runtime tuning gives (pure).</summary>
        public static WorldEventClaimPolicy ClaimPolicyFrom(WorldEventHoldRuntimeTuning runtime)
        {
            runtime ??= WorldEventHoldRuntimeTuning.Defaults;

            return new WorldEventClaimPolicy(runtime.FailureReward, new ClaimThresholds(runtime.MinPresenceSeconds, runtime.MinContribution))
            {
                ParticipantsPerCache = runtime.ParticipantsPerCache
            };
        }

        private static float? PositiveRadius(double? r) => r is double d && double.IsFinite(d) && d > 0 ? (float)d : (float?)null;

        // ---- the voice --------------------------------------------------------------------------------------------------

        /// <summary>How the run speaks through its WorldEvent (world thread).</summary>
        private sealed class Voice : IWorldEventHoldVoice
        {
            private readonly WorldEvent worldEvent;

            public Voice(WorldEvent worldEvent) => this.worldEvent = worldEvent;

            public string AnchorName => worldEvent.AnchorName;

            public string FamilyDisplayName => worldEvent.Composition?.FamilyDisplayName;

            public void Announce(string line) => worldEvent.AnnounceInRun(line);

            public void AnnounceLocal(string line) => WorldEventAnnouncer.Local(worldEvent, line);
        }
    }
}
