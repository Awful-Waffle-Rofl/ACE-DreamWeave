using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Harbinger
{
    /// <summary>
    /// Where the Harbinger fight stands inside one framework phase. Fighting: the phase's element runs and he can be hurt down
    /// to the phase's floor. Alcove: he is immune, the element still runs, and the alcove (portal, crystal, two guardians) is
    /// up. Landing: the crystal is dead, the element is over, he stays immune until the owner is back on the chamber floor.
    /// Final: phase 5, all four elements at once.
    /// </summary>
    public enum HarbingerStage { Fighting, Alcove, Landing, Final }

    /// <summary>
    /// The Drift Harbinger boss script, the element loop (plan T9). Pure logic behind <see cref="IPinnacleFightPort"/>: no
    /// WorldObject, PropertyManager, DateTime or new Random.
    ///
    /// FIVE FRAMEWORK PHASES. Phases 1 to 4 are one element each (Acid, Lightning, Frost, Fire, <see cref="HarbingerData.Elements"/>),
    /// all through the SAME code: the element's run starts, at its health floor (80, 60, 40, 20 percent) the Harbinger turns
    /// immune and the element's alcove opens (<see cref="OnThreshold"/>), the crystal's death ends the element
    /// (<see cref="OnCrystalDied"/>), and when the owner is back on the chamber floor the script asks for the next phase
    /// (<see cref="CheckLanding"/>). Phase 5 runs all four elements at once, halved (the settings builders apply the final scale),
    /// with no floor. Everything that differs between the elements is data (<see cref="HarbingerElementDef"/>,
    /// <see cref="HarbingerAlcove"/>) except the one switch that builds each element's hazard run (<see cref="StartRun"/>).
    ///
    /// A 1 s poll drives what the port cannot raise as an event: the health floor, the owner's arrival at the alcove, the owner's
    /// landing and the acid's rate escalation. The summon timer and the cast timer each re-arm themselves.
    ///
    /// ACID IS A LEVEL. Its run is a fill, so the script remembers the surface (<see cref="acidLevel"/>, read on the poll) and a
    /// restart starts from it, clamped between the fill floor and the run's own max_z (FillRules.Validate refuses a start_z above
    /// max_z, and the final's ceiling is lower than a phase's). The crystal's death drains it (<see cref="HarbingerSettings.CrystalDrainRate"/>)
    /// and a timer stops the run once it is dry.
    ///
    /// THE WALL STRIPS (round 2 A2). While the acid is wanted or draining (phase 1 and the final), the four wall strips
    /// (<see cref="HarbingerLayout.WallStrip"/>) stand as props. The script takes them down (<see cref="DropWallStrips"/>) at every
    /// exit it owns: the drain is done; the drain could not start; a phase is entered without the acid; a restart finds the drain
    /// over; the acid run is abandoned after <see cref="MaxRunRestarts"/> (a restart that starts the acid again brings them back); and
    /// the head's death, BEFORE Win, because a Win leaves props standing (IPinnacleFightPort.SpawnProp). The core's End (a lost or
    /// abandoned fight, a hold that expires, the end of a won one) destroys every prop itself. A hold keeps them up with the run
    /// stopped until the resume or the hold's End. Props outlive a phase change and a hold, so every sync point
    /// (<see cref="SyncWallStrips"/>) keeps the strips it has and spawns only the missing ones: a restart never doubles them.
    ///
    /// RESTARTS. A HazardEnded for a wanted run restarts it: Completed always, anything else at most
    /// <see cref="MaxRunRestarts"/> times per tag per phase. A run whose element is over is not wanted and is ignored.
    ///
    /// THE ALCOVE CONTRACT (round 2 A5, A6, A7, B1). The head and the four crystals spawn with the settings' health multiplier
    /// (<see cref="Data"/>, built in the constructor). A crystal spawns SHIELDED: immune with its health floor at full (the
    /// World Events source guard and the head's own immunity, never SetAttackable(false), which would make it an NPC and stop
    /// its monster AI). The guardians hold the shield: the first one down (the ward link) halves the crystal's cast interval for
    /// the rest of that alcove (a guardian whose spawn failed never sets it), the last one down (a guardian whose spawn failed counts as down) clears the immunity and the floor
    /// (the core plays the shield-down cue) and the Harbinger says <see cref="HarbingerLines.LineWardBroken"/>. A resume in the
    /// Alcove stage respawns the crystal shielded with two fresh guardians and a fresh ward link. Once the owner has arrived,
    /// the crystal casts by script (<see cref="TimerCrystalCast"/>): only in the Alcove stage, while it is alive and the owner is
    /// inside the alcove (the arrival test), the element's vulnerability on its casts 1, 1 + CastEvery ..., war spells otherwise,
    /// on its own counter. Breaking the crystal (a real death, not a failed spawn or a skipped alcove) grants the owner the
    /// element's Legendary Ward for <see cref="HarbingerSettings.AttuneSeconds"/> (the attunement). Phase 5 has no alcove, so
    /// none of this runs there.
    ///
    /// THE SIGNATURES AND THE DOORWAY PRESSURE (round 2 B1, B2). Each alcove has one signature, started on the owner's
    /// arrival (never at the threshold, <see cref="StartSignature"/>) and stopped at the crystal's death, at a resume and so
    /// at a hold (the core stops every run at a hold; the resume's <see cref="StopSignature"/> clears the rest, and the next
    /// arrival starts it again). Its hazard runs are wanted runs like the elements' (restarted only through
    /// <see cref="wanted"/>, <see cref="OnRunEnded"/>), built in ONE place (<see cref="StartSignatureRun"/>), tagged
    /// sig-&lt;element&gt; or pool-&lt;slot&gt;, on the alcove's preset or a pool's ad-hoc area only. Acid, Corrosion pools: each
    /// guardian killed leaves a pool at the free slot nearest where the 1 s poll last saw it (AddDied carries no position),
    /// and every <see cref="HarbingerSettings.DripSeconds"/> the crystal drips (round 3): ONE small green ball falls on a slot
    /// drawn with the fight's Rng (an existing pool that can still deepen about half the time, else a free slot under
    /// <see cref="HarbingerSettings.PoolCap"/>), and the drip run's own Completed end (its impact) starts a pool there or
    /// raises the pool there one <see cref="HarbingerSettings.PoolStep"/> (at the cap, the nearest pool instead). A drip run is
    /// drip-&lt;n&gt;, one-shot, never wanted or restarted (<see cref="drips"/>). A pool's depth (<see cref="poolZ"/>) survives
    /// its run's restarts. Round 4: the signature also starts the film (<see cref="TagFilm">sig-film</see>), one still thin fill over the whole acid
    /// floor under every pool, a wanted run that holds no pool slot. Lightning, Twin conductors and the conductor wisps (round 3): the wisps run (sig-lightning) from
    /// the arrival, and a guardian killed while its twin stands
    /// arms revive-&lt;i&gt;, which raises it again at its spawn point if the twin is still up then; the shield is untouched
    /// (it drops only when both are down at once, and the last death cancels the revives). Frost, Whiteout: small slowing
    /// hail in the alcove. Fire, Ember roll: small balls in waves, on lanes parallel to the doorway axis whose
    /// ball body stays out of the doorway lane, so a player standing inside that lane is never touched. <see cref="HarbingerSettings.PressureSeconds"/> after the arrival, ONE switch
    /// (<see cref="Escalate"/>) presses the signature once: faster drips, the revive time halved and more conductor wisps,
    /// denser and faster hail and balls.
    /// </summary>
    public sealed class HarbingerScript : IPinnacleBossScript
    {
        public const string TimerPoll = "poll";

        public const string TimerSummon = "summon";

        public const string TimerCast = "cast";

        public const string TimerDrainDone = "drain-done";

        /// <summary>The crystal's scripted cast (round 2 A6): armed at the owner's arrival, re-armed by itself while the Alcove stage lasts.</summary>
        public const string TimerCrystalCast = "crystal-cast";

        /// <summary>The acid alcove's drips (round 3, Corrosion pools): re-armed by itself while the signature is on.</summary>
        public const string TimerDrip = "sig-drip";

        /// <summary>The doorway pressure (round 2 B1): armed once at the arrival.</summary>
        public const string TimerPressure = "sig-pressure";

        /// <summary>The alcove arrival grace (round 4): fires <see cref="HarbingerSettings.AlcoveGraceSeconds"/> after the owner's arrival and starts the alcove's hazards.</summary>
        public const string TimerGrace = "sig-grace";

        /// <summary>A Twin conductors revive: revive-&lt;guard index&gt; (0 or 1, the alcove's guard point).</summary>
        public const string TimerRevivePrefix = "revive-";

        /// <summary>A signature hazard run's tag prefix: sig-&lt;element tag&gt; (Whiteout, Ember roll).</summary>
        public const string TagSignaturePrefix = "sig-";

        /// <summary>A Corrosion pool run's tag prefix: pool-&lt;slot index&gt; (HarbingerLayout.PoolSlotsUV).</summary>
        public const string TagPoolPrefix = "pool-";

        /// <summary>An acid drip run's tag prefix: drip-&lt;sequence&gt; (one-shot runs, never wanted).</summary>
        public const string TagDripPrefix = "drip-";

        /// <summary>The acid alcove's film run's tag (round 4): one still thin fill over the whole floor, a signature run that is not a pool slot.</summary>
        public const string TagFilm = TagSignaturePrefix + "film";

        public const double PollSeconds = 1.0;

        /// <summary>The acid run's tag; the other elements' tags are <see cref="HarbingerElementDef.Tag"/>.</summary>
        public const string TagAcid = "acid";

        /// <summary>The wall strip props' tag prefix: wall-&lt;alcove name&gt;-&lt;index&gt;.</summary>
        public const string TagWallPrefix = "wall-";

        /// <summary>The fill's floor_z (<see cref="HarbingerSettings.AcidArgs"/>): the surface the acid starts from and drains to.</summary>
        public const double AcidFloorZ = HarbingerSettings.AcidFloorZ;

        /// <summary>Restarts of a run after a non-Completed end, per tag per phase.</summary>
        public const int MaxRunRestarts = 5;

        /// <summary>The owner has reached the alcove at or above this z...</summary>
        public const float AlcoveArrivalMinZ = -2.0f;

        /// <summary>...and within this distance (2D) of its centre.</summary>
        public const double AlcoveArrivalRadius = 5.5;

        /// <summary>The owner is back on the chamber floor at or below this z (with the port reading them grounded).</summary>
        public const float LandingMaxZ = -30.0f;

        /// <summary>No cast while the owner is above this z (up in an alcove).</summary>
        public const float CastMinZ = -5.0f;

        /// <summary>A summon never spawns within this distance (2D) of the owner.</summary>
        public const double SummonClearance = 5.0;

        /// <summary>The acid's rate doubling is capped at this many doublings (the cap clamps long before).</summary>
        private const double MaxDoublings = 30.0;

        /// <summary>Health floors of phases 1 to 4; phase 5 clears the floor.</summary>
        public static readonly IReadOnlyList<double> Thresholds = new[] { 0.8, 0.6, 0.4, 0.2 };

        private readonly HarbingerSettings settings;
        private readonly PinnacleScriptData data;

        private int phase;
        private HarbingerStage stage;

        /// <summary>The element of phases 1 to 4; null in phase 5.</summary>
        private HarbingerElementDef current;

        /// <summary>The tags of the runs that are wanted now.</summary>
        private readonly HashSet<string> wanted = new HashSet<string>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> restarts = new Dictionary<string, int>(StringComparer.Ordinal);

        // ---- the alcove of the current element ----
        private PinnacleHandle crystal;
        private PinnacleHandle portal;
        private bool arrivalSaid;
        private bool advanceRequested;

        // ---- the crystal's ward (round 2 A5, B1) ----

        /// <summary>The guardians still holding the ward (a failed spawn is a None entry until its SpawnFailed arrives).</summary>
        private readonly List<PinnacleHandle> guards = new List<PinnacleHandle>();

        /// <summary>The live crystal is immune at a full health floor.</summary>
        private bool shielded;

        /// <summary>A guardian has been killed in this alcove (a failed spawn does not count): the crystal casts at half its interval.</summary>
        private bool wardLinked;

        /// <summary>The live crystal's scripted casts, its own count (never <see cref="headCasts"/>).</summary>
        private int crystalCasts;

        // ---- the alcove signature (round 2 B1, B2) ----

        /// <summary>The current alcove's signature is running (the owner has arrived; cleared at every stop).</summary>
        private bool signatureOn;

        /// <summary>The alcove's damaging hazards are live: the arrival grace is over (round 4). False while the grace runs, and outside a signature.</summary>
        private bool hazardsLive;

        /// <summary>The fight time the current arrival's grace ends (set at the arrival, <see cref="CrystalCastDelay"/>).</summary>
        private double graceEnd;

        /// <summary>The fight time the next acid drip is owed (carries the remainder between ticks, so the average cadence is the drip interval).</summary>
        private double dripDue;

        /// <summary>Where guardians were last seen when they died during the arrival grace: their pools are placed when it ends (<see cref="BeginHazards"/>).</summary>
        private readonly List<PinnaclePoint> graceDeaths = new List<PinnaclePoint>();

        /// <summary>The most drips one tick may fire when the 1 s poll finds more than one owed (the pressed 1.2 s interval owes 2 about one tick in six).</summary>
        public const int MaxDripsPerTick = 2;

        /// <summary>The doorway pressure has begun in this alcove.</summary>
        private bool pressured;

        /// <summary>The live pools' slots (each one wanted run, pool-&lt;slot&gt;), in the order they were started.</summary>
        private readonly List<int> pools = new List<int>();

        /// <summary>Each live pool's surface z (its depth over the alcove floor, z 0): a restart of its run starts from it.</summary>
        private readonly Dictionary<int, double> poolZ = new Dictionary<int, double>();

        /// <summary>The drips in the air: drip run tag to the slot it falls on. Its Completed end is the impact.</summary>
        private readonly Dictionary<string, int> drips = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>The last drip's sequence number (the drip tags never repeat within a fight).</summary>
        private int dripSeq;

        /// <summary>Each live guardian's guard index (0 or 1: alcove.Guards), for the twin's revive.</summary>
        private readonly Dictionary<PinnacleHandle, int> guardIndex = new Dictionary<PinnacleHandle, int>();

        /// <summary>Where each live guardian was last seen (its spawn point, then the 1 s poll's reads while the acid signature runs).</summary>
        private readonly Dictionary<PinnacleHandle, PinnaclePoint> guardSeen = new Dictionary<PinnacleHandle, PinnaclePoint>();

        // ---- summons, by element tag ----
        private readonly Dictionary<string, List<PinnacleHandle>> summons = new Dictionary<string, List<PinnacleHandle>>(StringComparer.Ordinal);

        // ---- the acid ----
        private double acidLevel = AcidFloorZ;
        private double acidStart;
        private double? acidRate;
        private bool draining;

        // ---- the wall strips: tag, prop and handle (None until spawned), all four alcoves; empty while down ----
        private readonly List<(string Tag, PinnacleProp Prop, PinnacleHandle Handle)> wallStrips = new List<(string, PinnacleProp, PinnacleHandle)>();

        // ---- casts: one counter for the whole fight, like Grael's head ----
        private int headCasts;

        public HarbingerScript(): this(HarbingerSettings.Defaults)
        {
        }

        public HarbingerScript(HarbingerSettings settings)
        {
            this.settings = settings ?? HarbingerSettings.Defaults;
            data = HarbingerData.Script(this.settings.HealthMultiplier, this.settings.HeadHealthMultiplier);
        }

        public string Kind => HarbingerData.Kind;

        public PinnacleScriptData Data => data;

        /// <summary>The tunables this fight runs on (the live defaults, or a /pinnacle enter per-copy override of them).</summary>
        public HarbingerSettings Settings => settings;

        public int PhaseCount => 5;

        public HarbingerStage Stage => stage;

        // ================================================================ lifecycle

        public void OnStart(IPinnacleFightPort port)
        {
            port.Say(HarbingerLines.LineStart);
            port.SetAttackable(port.Head, true);
            port.SetMeleeReach(port.Head, (float)settings.MeleeReach);
            port.Engage(port.Head);
        }

        public void EnterPhase(int n, IPinnacleFightPort port)
        {
            phase = n;

            // the core cleared the previous phase: no run, no timer, no add
            wanted.Clear();
            restarts.Clear();
            summons.Clear();
            crystal = PinnacleHandle.None;
            // an admin jump out of the Alcove stage leaves the portal prop (props outlive a phase change)
            if (!portal.IsNone)
                port.DestroyProp(portal);

            portal = PinnacleHandle.None;
            arrivalSaid = false;
            advanceRequested = false;
            ResetWard();
            ResetSignature();   // the core cleared every run and timer of the old phase
            draining = false;
            acidLevel = AcidFloorZ;
            acidStart = port.Now;
            acidRate = null;

            port.SetImmune(port.Head, false);

            if (n <= HarbingerData.Elements.Count)
            {
                current = HarbingerData.Elements[n - 1];
                stage = HarbingerStage.Fighting;
                port.SetHealthFloor(port.Head, Thresholds[n - 1]);
                wanted.Add(current.Tag);
                StartRun(port, current, final: false);
            }
            else
            {
                current = null;
                stage = HarbingerStage.Final;
                port.SetHealthFloor(port.Head, null);
                port.Say(HarbingerLines.LineFinal);

                foreach (var def in HarbingerData.Elements)
                {
                    wanted.Add(def.Tag);
                    StartRun(port, def, final: true);
                }

                SpawnFinalSummons(port);
            }

            SyncWallStrips(port);
            ArmTimers(port);
        }

        public void RestartPhase(IPinnacleFightPort port)
        {
            // a returning player lands on the floor: the acid restarts at the phase's starting height and rate, not where it was
            acidLevel = AcidFloorZ;
            acidStart = port.Now;
            acidRate = null;

            // the new crystal (Alcove stage, below) is engaged on arrival again
            arrivalSaid = false;

            // the hold stopped the signature's runs; its timers and its wanted runs go too, and the next arrival starts it again
            StopSignature(port);

            // the hold stopped every run: a drain in progress is over with it, and the crystal's element is already done
            if (draining)
            {
                draining = false;
                port.CancelTimer(TimerDrainDone);
            }

            restarts.Clear();
            port.Say(HarbingerLines.LineReturn);
            port.SetImmune(port.Head, stage == HarbingerStage.Alcove || stage == HarbingerStage.Landing);
            port.SetMeleeReach(port.Head, (float)settings.MeleeReach);
            port.Engage(port.Head);

            if (stage == HarbingerStage.Alcove)
                RespawnAlcove(port);

            // between the crystal's death and the landing the owner is back on the floor already: the next phase starts on return
            if (stage == HarbingerStage.Landing)
                RequestAdvance(port);

            // whatever an event handler started at this resume is stopped first, so one run per element is live afterwards
            foreach (var def in HarbingerData.Elements)
            {
                if (!wanted.Contains(def.Tag))
                    continue;

                port.StopHazard(def.Tag);
                StartRun(port, def, stage == HarbingerStage.Final);
            }

            SyncWallStrips(port);
            ArmTimers(port);
        }

        public string Describe()
            => $"harbinger p{phase} stage={stage} element={(current == null ? "all" : current.Tag)} runs={string.Join(",", wanted.OrderBy(t => t, StringComparer.Ordinal))} casts={headCasts} crystal_casts={crystalCasts} ward={(shielded ? "up" : "down")} guards={guards.Count}{(wardLinked ? " linked" : "")} drain={(draining ? "on" : "off")} sig={(signatureOn ? (pressured ? "pressed" : "on") : "off")} pools={pools.Count}";

        private void ArmTimers(IPinnacleFightPort port)
        {
            port.SetTimer(TimerPoll, PollSeconds);
            port.SetTimer(TimerSummon, settings.SummonSeconds);
            port.SetTimer(TimerCast, settings.CastSeconds);
        }

        // ================================================================ events

        public void OnEvent(PinnacleEvent e, IPinnacleFightPort port)
        {
            switch (e.Kind)
            {
                case PinnacleEventKind.AddDied:
                    if (e.Tag == PinnacleEvent.HeadTag)
                    {
                        port.Say(HarbingerLines.LineDeath);
                        DropWallStrips(port);   // a Win leaves props standing: the acid's flows go with him
                        port.Win();
                    }
                    // the live crystal only (or its failed spawn): an old crystal destroyed at an Alcove restart never ends the element
                    else if (current != null && stage == HarbingerStage.Alcove && e.Tag == current.CrystalTag && (e.SpawnFailed || e.Handle == crystal))
                    {
                        OnCrystalDied(port, broken: !e.SpawnFailed);
                    }
                    else if (current != null && stage == HarbingerStage.Alcove && e.Tag == current.GuardTag)
                    {
                        OnGuardDied(e, port);
                    }
                    break;

                case PinnacleEventKind.HazardEnded:
                    OnRunEnded(e.Tag, e.Outcome, port);
                    break;

                case PinnacleEventKind.TimerFired:
                    OnTimer(e.Tag, port);
                    break;
            }
        }

        private void OnTimer(string tag, IPinnacleFightPort port)
        {
            switch (tag)
            {
                case TimerPoll:
                    Poll(port);
                    port.SetTimer(TimerPoll, PollSeconds);
                    break;

                case TimerSummon:
                    Summon(port);
                    port.SetTimer(TimerSummon, settings.SummonSeconds);
                    break;

                case TimerCast:
                    Cast(port);
                    port.SetTimer(TimerCast, settings.CastSeconds);
                    break;

                case TimerCrystalCast:
                    // the Alcove stage is over (the crystal died, or the phase moved on): the timer is not re-armed
                    if (stage != HarbingerStage.Alcove)
                        break;

                    CrystalCast(port);
                    port.SetTimer(TimerCrystalCast, CrystalCastInterval);
                    break;

                case TimerDrip:
                    // the signature is over (the crystal died, a resume, the phase moved on): not re-armed
                    // (and the arrival grace is not over: no drip before the hazards go live)
                    if (!signatureOn || !hazardsLive || stage != HarbingerStage.Alcove || current == null || current.Element != HarbingerElement.Acid)
                        break;

                    // the poll is 1 s: carry the remainder and fire every drip the elapsed time owes, at most MaxDripsPerTick,
                    // so the average cadence is the interval itself (1.2 s pressed), not the interval rounded up to whole ticks
                    var fired = 0;

                    while (dripDue <= port.Now + 1e-9 && fired < MaxDripsPerTick)
                    {
                        Drip(port);
                        dripDue += DripInterval;
                        fired++;
                    }

                    // a backlog past the cap is dropped, not repaid later: the interval starts afresh from now
                    if (dripDue < port.Now)
                        dripDue = port.Now + DripInterval;

                    port.SetTimer(TimerDrip, Math.Max(0.0, dripDue - port.Now));
                    break;

                case TimerGrace:
                    if (signatureOn && !hazardsLive && stage == HarbingerStage.Alcove && current != null)
                        BeginHazards(port);
                    break;

                case TimerPressure:
                    if (signatureOn && stage == HarbingerStage.Alcove)
                        Escalate(port, current);
                    break;

                case TimerDrainDone:
                    if (draining)
                    {
                        draining = false;
                        port.StopHazard(TagAcid);
                        SyncWallStrips(port);
                    }
                    break;

                default:
                    if (tag != null && tag.StartsWith(TimerRevivePrefix, StringComparison.Ordinal) && int.TryParse(tag.Substring(TimerRevivePrefix.Length), out var index))
                        Revive(port, index);
                    break;
            }
        }

        private void Poll(IPinnacleFightPort port)
        {
            ReconcileAcid(port);

            switch (stage)
            {
                case HarbingerStage.Fighting:
                    if (port.IsAtHealthFloor(port.Head))
                        OnThreshold(port);
                    break;

                case HarbingerStage.Alcove:
                    CheckArrival(port);
                    TrackGuards(port);
                    break;

                case HarbingerStage.Landing:
                    CheckLanding(port);
                    break;
            }
        }

        // ================================================================ the element loop

        /// <summary>The health floor is reached: he is immune from here, and the element's alcove opens (the run keeps going).</summary>
        private void OnThreshold(IPinnacleFightPort port)
        {
            var def = current;
            var alcove = HarbingerData.AlcoveOf(def.Element);

            stage = HarbingerStage.Alcove;
            port.SetImmune(port.Head, true);

            portal = port.SpawnProp(def.PortalTag, new PinnacleProp(alcove.PortalWcid, alcove.Portal));
            SpawnCrystalAndGuards(port, def);

            port.Say(HarbingerLines.Threshold(def.Element));
            SkipAlcoveWithoutPortal(port);
        }

        private void SpawnCrystalAndGuards(IPinnacleFightPort port, HarbingerElementDef def)
        {
            var alcove = HarbingerData.AlcoveOf(def.Element);

            ResetWard();
            crystal = port.SpawnAdd(def.CrystalTag, def.CrystalWcid, alcove.Crystal, HarbingerData.AddTier);

            // shielded from its spawn (a failed spawn ends the element on its own SpawnFailed, so it needs no shield)
            if (!crystal.IsNone)
            {
                shielded = true;
                port.SetImmune(crystal, true);
                port.SetHealthFloor(crystal, 1.0);
            }

            for (var i = 0; i < alcove.Guards.Count; i++)
                AddGuard(port, def, i);
        }

        /// <summary>Spawns guard <paramref name="index"/> at its point and holds it in the ward (a failed spawn is a None entry).</summary>
        private PinnacleHandle AddGuard(IPinnacleFightPort port, HarbingerElementDef def, int index)
        {
            var at = HarbingerData.AlcoveOf(def.Element).Guards[index];
            var h = port.SpawnAdd(def.GuardTag, def.GuardianWcid, at, HarbingerData.AddTier);

            guards.Add(h);

            if (!h.IsNone)
            {
                guardIndex[h] = index;
                guardSeen[h] = at;
            }

            return h;
        }

        /// <summary>A fresh alcove: no guardian held, no shield, no ward link, the crystal's cast count back to its first (the vulnerability).</summary>
        private void ResetWard()
        {
            guards.Clear();
            guardIndex.Clear();
            guardSeen.Clear();
            shielded = false;
            wardLinked = false;
            crystalCasts = 0;
        }

        /// <summary>
        /// A guardian of the live alcove is down: the last one down breaks the ward, and a failed guardian spawn counts as down for
        /// that. Only a REAL death sets the ward link (a guardian that never stood was never killed); the first one halves the crystal's
        /// cast interval and, once the owner has arrived (the crystal's casts have started), re-arms the cast timer at the halved
        /// interval at once. An AddDied for a guardian this alcove no longer holds (one destroyed at a restart) is ignored.
        /// </summary>
        private void OnGuardDied(PinnacleEvent e, IPinnacleFightPort port)
        {
            var i = guards.IndexOf(e.SpawnFailed ? PinnacleHandle.None : e.Handle);

            if (i < 0)
                return;

            guards.RemoveAt(i);

            var index = -1;
            PinnaclePoint? seenAt = null;

            if (!e.SpawnFailed)
            {
                if (guardIndex.TryGetValue(e.Handle, out var gi))
                    index = gi;

                if (guardSeen.TryGetValue(e.Handle, out var at))
                    seenAt = at;

                guardIndex.Remove(e.Handle);
                guardSeen.Remove(e.Handle);
            }

            if (!e.SpawnFailed && !wardLinked)
            {
                wardLinked = true;

                if (arrivalSaid && stage == HarbingerStage.Alcove)
                    port.SetTimer(TimerCrystalCast, CrystalCastDelay(port));
            }

            if (guards.Count == 0)
            {
                // both down at once: the ward breaks, and no twin is left to raise the other
                BreakWard(port);
                CancelRevives(port);
            }
            else if (!e.SpawnFailed && signatureOn && current.Element == HarbingerElement.Lightning && index >= 0)
            {
                // Twin conductors: the twin still stands, so this one rises again unless the twin falls first
                port.SetTimer(TimerRevivePrefix + index, ReviveSeconds);
            }

            // Corrosion pools: a guardian leaves a pool where it fell (the last one too)
            if (!e.SpawnFailed && current.Element == HarbingerElement.Acid && seenAt.HasValue)
            {
                if (hazardsLive)
                    PoolAtDeath(port, seenAt.Value);
                else if (signatureOn)
                    graceDeaths.Add(seenAt.Value);   // the arrival grace is running: the pool waits for its end
            }
        }

        /// <summary>The last guardian is down: the crystal can be hurt (the core's SetImmune plays the shield-down cue), and he says so.</summary>
        private void BreakWard(IPinnacleFightPort port)
        {
            if (!shielded)
                return;

            shielded = false;
            port.SetHealthFloor(crystal, null);
            port.SetImmune(crystal, false);
            port.Say(HarbingerLines.LineWardBroken);
        }

        /// <summary>
        /// No portal, no way up: the alcove is skipped (as a crystal that failed to spawn skips it) so the fight cannot stall. A
        /// crystal that did spawn is destroyed so it is not left standing.
        /// </summary>
        private void SkipAlcoveWithoutPortal(IPinnacleFightPort port)
        {
            if (!portal.IsNone)
                return;

            port.DestroyAdds(current.CrystalTag);
            OnCrystalDied(port, broken: false);
        }

        /// <summary>
        /// A resume in the Alcove stage: the crystal and guardians reset to full (the hold kept the damaged ones), the portal stays
        /// (respawned when it is gone), and the new crystal wakes on arrival (<see cref="arrivalSaid"/> is reset by the caller).
        /// </summary>
        private void RespawnAlcove(IPinnacleFightPort port)
        {
            var def = current;
            var alcove = HarbingerData.AlcoveOf(def.Element);

            port.DestroyAdds(def.CrystalTag);
            port.DestroyAdds(def.GuardTag);

            // the new crystal's casts start again at the owner's arrival
            port.CancelTimer(TimerCrystalCast);

            if (!portal.IsNone && !port.IsAlive(portal))
                portal = PinnacleHandle.None;

            if (portal.IsNone)
                portal = port.SpawnProp(def.PortalTag, new PinnacleProp(alcove.PortalWcid, alcove.Portal));

            SpawnCrystalAndGuards(port, def);
            SkipAlcoveWithoutPortal(port);
        }

        /// <summary>The next phase, asked for once per phase (a poll and a resume never both ask).</summary>
        private void RequestAdvance(IPinnacleFightPort port)
        {
            if (advanceRequested)
                return;

            advanceRequested = true;
            port.AdvancePhase();
        }

        /// <summary>
        /// The owner's first arrival in the alcove: the crystal wakes, its shield cue plays again where the owner can see it (the
        /// spawn's cue played before anyone was there), and its scripted casts start. Said once per phase (and per resume).
        /// </summary>
        private void CheckArrival(IPinnacleFightPort port)
        {
            if (arrivalSaid || !IsOwnerInAlcove(port))
                return;

            arrivalSaid = true;
            port.Engage(crystal);

            if (shielded)
                port.PlayEffect(crystal, PlayScript.ShieldUpGrey);

            graceEnd = port.Now + settings.AlcoveGraceSeconds;
            port.SetTimer(TimerCrystalCast, CrystalCastDelay(port));
            port.Say(HarbingerLines.LineAlcove);
            StartSignature(port);
        }

        /// <summary>The arrival test: the owner is readable, at or above <see cref="AlcoveArrivalMinZ"/> and within <see cref="AlcoveArrivalRadius"/> of the current alcove's centre.</summary>
        private bool IsOwnerInAlcove(IPinnacleFightPort port)
        {
            if (current == null || !port.TryGetOwnerPosition(out var owner))
                return false;

            var centre = HarbingerData.AlcoveOf(current.Element).Centre;

            return owner.Z >= AlcoveArrivalMinZ && Distance2D(owner.X, owner.Y, centre.X, centre.Y) <= AlcoveArrivalRadius;
        }

        /// <summary>
        /// The crystal is dead (or never spawned): the element ends, its guardians, summons and portal go, and he stays immune
        /// until the owner lands. The other elements are not touched. <paramref name="broken"/> (a real death, not a failed spawn
        /// or a skipped alcove) grants the owner the element's attunement.
        /// </summary>
        private void OnCrystalDied(IPinnacleFightPort port, bool broken)
        {
            var def = current;

            wanted.Remove(def.Tag);
            port.CancelTimer(TimerCrystalCast);
            StopSignature(port);
            ResetWard();

            if (broken && settings.AttuneSeconds > 0)
                port.GrantOwnerBuff(def.AttuneSpell, settings.AttuneSeconds);

            if (def.Element == HarbingerElement.Acid)
            {
                DrainAcid(port);
                SyncWallStrips(port);   // a drain that could not start stopped the run at once: the strips go with it
            }
            else
            {
                port.StopHazard(def.Tag);
            }

            port.DestroyAdds(def.SummonTag);
            port.DestroyAdds(def.GuardTag);
            summons.Remove(def.SummonTag);

            if (!portal.IsNone)
                port.DestroyProp(portal);

            portal = PinnacleHandle.None;
            crystal = PinnacleHandle.None;

            port.Say(HarbingerLines.Crystal(def.Element));
            stage = HarbingerStage.Landing;
        }

        /// <summary>The crystal drains the acid at its own rate; the run stops when it is dry (a timer), or at once when it is not running.</summary>
        private void DrainAcid(IPinnacleFightPort port)
        {
            if (port.TryGetHazardLevel(TagAcid, out var live))
                acidLevel = live;

            var depth = Math.Max(0.0, acidLevel - AcidFloorZ);
            var rate = settings.CrystalDrainRate;

            if (!port.ControlHazard(TagAcid, "rate", rate))
            {
                port.StopHazard(TagAcid);
                return;
            }

            draining = true;
            acidRate = rate;
            port.SetTimer(TimerDrainDone, depth / Math.Abs(rate) + 1.0);
        }

        /// <summary>Back on the chamber floor, standing on it: the next phase. Asked for once.</summary>
        private void CheckLanding(IPinnacleFightPort port)
        {
            if (advanceRequested || !port.TryGetOwnerPosition(out var owner) || owner.Z > LandingMaxZ || !port.IsOwnerGrounded())
                return;

            RequestAdvance(port);
        }

        // ================================================================ the wall strips

        /// <summary>
        /// The strips stand exactly while the acid is wanted or draining. Up: every strip prop is kept if it is alive and spawned
        /// (again) when it is not, so a sync after a phase change or a hold never doubles them. Down: every strip prop is destroyed.
        /// </summary>
        private void SyncWallStrips(IPinnacleFightPort port)
        {
            if (!wanted.Contains(TagAcid) && !draining)
            {
                DropWallStrips(port);
                return;
            }

            if (wallStrips.Count == 0)
            {
                foreach (var alcove in HarbingerData.Alcoves)
                {
                    var strip = HarbingerLayout.WallStrip(alcove, settings.WallFx, settings.FogAcid);

                    for (var i = 0; i < strip.Count; i++)
                        wallStrips.Add(($"{TagWallPrefix}{alcove.Name}-{i}", strip[i], PinnacleHandle.None));
                }
            }

            for (var i = 0; i < wallStrips.Count; i++)
            {
                var (tag, prop, handle) = wallStrips[i];

                if (!handle.IsNone && port.IsAlive(handle))
                    continue;

                wallStrips[i] = (tag, prop, port.SpawnProp(tag, prop));
            }
        }

        /// <summary>Destroys every strip prop this script spawned and forgets them (a later sync that wants them builds them anew).</summary>
        private void DropWallStrips(IPinnacleFightPort port)
        {
            foreach (var (_, _, handle) in wallStrips)
                if (!handle.IsNone)
                    port.DestroyProp(handle);

            wallStrips.Clear();
        }

        // ================================================================ the runs

        /// <summary>
        /// THE one place an element's hazard is built and started (every start and restart goes through here). Pcts are scaled to
        /// the Knot level and passed un-halved: the settings builders apply the final scale themselves.
        /// </summary>
        private void StartRun(IPinnacleFightPort port, HarbingerElementDef def, bool final)
        {
            switch (def.Element)
            {
                case HarbingerElement.Acid:
                    var rate = AcidRateNow(port, final);
                    var startZ = Math.Min(Math.Max(acidLevel, AcidFloorZ), final ? settings.FinalAcidMaxZ : settings.AcidMaxZ);

                    acidRate = rate;
                    port.StartHazard(def.Tag, HazardMechanicRegistry.Fill, null, settings.AcidArgs(startZ, rate, LevelScale(port), final));
                    break;

                case HarbingerElement.Lightning:
                    port.StartHazard(def.Tag, HazardMechanicRegistry.Wisps, "mixed", settings.LightningArgs(ScaledPct(port, settings.WispPct), final));
                    break;

                case HarbingerElement.Frost:
                    port.StartHazard(def.Tag, HazardMechanicRegistry.Rain, null, settings.HailArgs(ScaledPct(port, settings.HailPct), final), default, new PinnacleHazardOptions(settings.HailSlow));
                    break;

                case HarbingerElement.Fire:
                    port.StartHazard(def.Tag, HazardMechanicRegistry.Balls, BallPatterns.RingAlt, settings.FireArgs(ScaledPct(port, settings.FirePct), final), port.Head);
                    break;
            }
        }

        private void OnRunEnded(string tag, HazardOutcome outcome, IPinnacleFightPort port)
        {
            // an acid drip is one-shot: its Completed end is its impact, any other end is a drop that never landed
            if (tag != null && drips.TryGetValue(tag, out var dripSlot))
            {
                drips.Remove(tag);

                if (outcome == HazardOutcome.Completed)
                    DripLanded(port, dripSlot);

                return;
            }

            if (!wanted.Contains(tag))
                return;

            var signature = IsSignatureTag(tag);

            if (outcome != HazardOutcome.Completed)
            {
                restarts.TryGetValue(tag, out var n);

                if (n >= MaxRunRestarts)
                {
                    // a signature run given up on is no longer wanted (an abandoned pool frees its slot)
                    if (signature)
                    {
                        wanted.Remove(tag);

                        if (TryPoolSlot(tag, out var slot))
                        {
                            pools.Remove(slot);
                            poolZ.Remove(slot);
                        }

                        return;
                    }

                    // the acid is over for this phase (until a restart starts it again): no acid flows down the walls either
                    if (tag == TagAcid)
                        DropWallStrips(port);

                    return;
                }

                restarts[tag] = n + 1;
            }

            if (signature)
            {
                StartSignatureRun(port, tag);
                return;
            }

            StartRun(port, HarbingerData.Elements.First(d => d.Tag == tag), stage == HarbingerStage.Final);
        }

        /// <summary>The poll's acid work: read the surface, and send the escalated rate when it changed (never while draining).</summary>
        private void ReconcileAcid(IPinnacleFightPort port)
        {
            if (!wanted.Contains(TagAcid) || draining)
                return;

            if (port.TryGetHazardLevel(TagAcid, out var z))
                acidLevel = z;

            var rate = AcidRateNow(port, stage == HarbingerStage.Final);

            if (acidRate.HasValue && Math.Abs(acidRate.Value - rate) < 1e-12)
                return;

            if (port.ControlHazard(TagAcid, "rate", rate))
                acidRate = rate;
        }

        /// <summary>The final's flat rate, or the phase's rate doubled every <see cref="HarbingerSettings.AcidEscalateSeconds"/> up to the cap.</summary>
        private double AcidRateNow(IPinnacleFightPort port, bool final)
        {
            if (final)
                return settings.FinalAcidRate;

            var doublings = Math.Floor(Math.Max(0.0, port.Now - acidStart) / settings.AcidEscalateSeconds);

            return Math.Min(settings.AcidRate * Math.Pow(2.0, Math.Min(doublings, MaxDoublings)), settings.AcidRateCap);
        }

        private double ScaledPct(IPinnacleFightPort port, double authored)
            => HazardLevelScale.Scaled(port.Level, authored, settings.HazardPctMin, settings.HazardPctMax);

        /// <summary>The Knot level's damage scale for the acid: the benchmark ball pct over its anchor (1.0 at level 200).</summary>
        private double LevelScale(IPinnacleFightPort port)
            => HazardLevelScale.BenchmarkPct(port.Level, settings.HazardPctMin, settings.HazardPctMax) / HazardLevelScale.AnchorPct;

        // ================================================================ the alcove signatures (round 2 B1, B2)

        /// <summary>Forgets the signature without touching the port (a phase entry: the core already cleared runs and timers).</summary>
        private void ResetSignature()
        {
            signatureOn = false;
            hazardsLive = false;
            dripDue = 0;
            graceDeaths.Clear();
            pressured = false;
            pools.Clear();
            poolZ.Clear();
            drips.Clear();
        }

        /// <summary>
        /// The owner's arrival: the current alcove's signature starts, once (a second call while it runs does nothing), and
        /// the doorway pressure is armed. Never in the final (no alcove) or outside the Alcove stage.
        /// </summary>
        private void StartSignature(IPinnacleFightPort port)
        {
            if (signatureOn || current == null || stage != HarbingerStage.Alcove)
                return;

            signatureOn = true;
            hazardsLive = false;
            pressured = false;
            port.SetTimer(TimerPressure, settings.PressureSeconds);

            // the film is only a picture while film_damage is 0: it shows at once. With a damaging film it waits with the rest
            if (current.Element == HarbingerElement.Acid && settings.FilmDamage <= 0)
            {
                wanted.Add(TagFilm);
                StartSignatureRun(port, TagFilm);
            }

            // round 4 (owner playtest): every hazard of the alcove waits for the grace, so a player who has just arrived is never
            // hit before they can react. The one hook covers the first arrival, a re-arrival after a death and a resume
            // (RestartPhase stops the signature, the next arrival is a new StartSignature). The 1 s poll makes it whole ticks.
            if (settings.AlcoveGraceSeconds > 0)
                port.SetTimer(TimerGrace, settings.AlcoveGraceSeconds);
            else
                BeginHazards(port);
        }

        /// <summary>The arrival grace is over: the alcove's hazards start (the signature runs, the acid drips, a damaging film).</summary>
        private void BeginHazards(IPinnacleFightPort port)
        {
            hazardsLive = true;

            switch (current.Element)
            {
                case HarbingerElement.Acid:
                    if (settings.FilmDamage > 0)
                    {
                        wanted.Add(TagFilm);
                        StartSignatureRun(port, TagFilm);
                    }

                    ArmDrip(port);

                    // the guardians that fell during the grace leave their pools now, in the order they fell (PoolAtDeath keeps the cap and the slot rules)
                    foreach (var at in graceDeaths.ToList())
                        PoolAtDeath(port, at);

                    graceDeaths.Clear();
                    break;

                case HarbingerElement.Lightning:
                    // Twin conductors is the guardians' own (a revive needs a death); the conductor wisps run from now
                case HarbingerElement.Frost:
                case HarbingerElement.Fire:
                    var tag = SignatureTag(current);
                    wanted.Add(tag);
                    StartSignatureRun(port, tag);
                    break;
            }
        }

        /// <summary>The drip clock (re)starts: the next drip is owed one interval from now.</summary>
        private void ArmDrip(IPinnacleFightPort port)
        {
            dripDue = port.Now + DripInterval;
            port.SetTimer(TimerDrip, DripInterval);
        }

        /// <summary>
        /// The signature stops: its runs are no longer wanted and are stopped (the drips in the air too), its drip, pressure and revive timers are
        /// cancelled, and its pools are forgotten. Nothing to do when it is not running.
        /// </summary>
        private void StopSignature(IPinnacleFightPort port)
        {
            if (!signatureOn)
            {
                ResetSignature();
                return;
            }

            port.CancelTimer(TimerDrip);
            port.CancelTimer(TimerGrace);
            port.CancelTimer(TimerPressure);
            CancelRevives(port);

            // the drips in the air: stopped, and their ends (if any still arrive) are no longer drips of this signature
            foreach (var tag in drips.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList())
                port.StopHazard(tag);

            foreach (var tag in wanted.Where(IsSignatureTag).OrderBy(t => t, StringComparer.Ordinal).ToList())
            {
                wanted.Remove(tag);
                restarts.Remove(tag);
                port.StopHazard(tag);
            }

            ResetSignature();
        }

        private static void CancelRevives(IPinnacleFightPort port)
        {
            port.CancelTimer(TimerRevivePrefix + 0);
            port.CancelTimer(TimerRevivePrefix + 1);
        }

        /// <summary>
        /// THE doorway pressure, the one switch (round 2 B1): said once, and the signature turns harder. Acid drips faster from
        /// now; Lightning's next revive comes in half the time and its conductor wisps restart pressed; Frost and Fire restart
        /// their run with the pressed numbers.
        /// </summary>
        private void Escalate(IPinnacleFightPort port, HarbingerElementDef def)
        {
            if (pressured || def == null)
                return;

            pressured = true;
            port.Say(HarbingerLines.LinePressure);

            switch (def.Element)
            {
                case HarbingerElement.Acid:
                    // during the arrival grace no drip clock runs yet: BeginHazards arms it at the pressed interval
                    if (hazardsLive)
                        ArmDrip(port);
                    break;

                case HarbingerElement.Lightning:
                    // ReviveSeconds reads the halved time from here on; the wisps restart below
                case HarbingerElement.Frost:
                case HarbingerElement.Fire:
                    var tag = SignatureTag(def);

                    if (!wanted.Contains(tag))
                        break;

                    port.StopHazard(tag);
                    restarts.Remove(tag);
                    StartSignatureRun(port, tag);
                    break;
            }
        }

        /// <summary>
        /// THE one place a signature run is built and started (every start, restart and pressed restart goes through here):
        /// a pool on its slot's ad-hoc area (from its remembered depth), the conductor wisps, Whiteout and Ember roll on the
        /// alcove's own preset. Pcts are level-scaled (<see cref="ScaledPct"/>), Ember roll's on its own linear clamp
        /// (<see cref="HarbingerSettings.EmberPctAt"/>); a pool's flat hit takes the acid's level scale (<see cref="LevelScale"/>).
        /// The one-shot drips are not signature runs (<see cref="StartDrip"/>).
        /// </summary>
        private void StartSignatureRun(IPinnacleFightPort port, string tag)
        {
            var def = current;
            var alcove = HarbingerData.AlcoveOf(def.Element);

            if (tag == TagFilm)
            {
                port.StartHazard(tag, HazardMechanicRegistry.Fill, null, settings.FilmArgs(LevelScale(port)), default,
                    new PinnacleHazardOptions(Area: HarbingerLayout.FilmArea(alcove)));
                return;
            }

            if (TryPoolSlot(tag, out var slot))
            {
                var surface = poolZ.TryGetValue(slot, out var z) ? z : HarbingerSettings.PoolSurfaceZ;

                port.StartHazard(tag, HazardMechanicRegistry.Fill, null, settings.PoolArgs(LevelScale(port), surface), default,
                    new PinnacleHazardOptions(Area: HarbingerLayout.PoolArea(alcove, slot, settings.PoolSize)));
                return;
            }

            switch (def.Element)
            {
                case HarbingerElement.Lightning:
                    port.StartHazard(tag, HazardMechanicRegistry.Wisps, WispRules.Scatter, settings.ConductorArgs(ScaledPct(port, settings.ConductorWispPct), pressured), default,
                        new PinnacleHazardOptions(Region: HarbingerLayout.RegionPreset(alcove)));
                    break;

                case HarbingerElement.Frost:
                    port.StartHazard(tag, HazardMechanicRegistry.Rain, null, settings.WhiteoutArgs(ScaledPct(port, settings.WhiteoutPct), pressured), default,
                        new PinnacleHazardOptions(settings.HailSlow, Region: HarbingerLayout.RegionPreset(alcove)));
                    break;

                case HarbingerElement.Fire:
                    port.StartHazard(tag, HazardMechanicRegistry.Balls, BallPatterns.Random, settings.EmberArgs(settings.EmberPctAt(port.Level), pressured), default,
                        new PinnacleHazardOptions(Region: HarbingerLayout.RegionPreset(alcove)));
                    break;
            }
        }

        public static string SignatureTag(HarbingerElementDef def) => TagSignaturePrefix + def.Tag;

        public static string PoolTag(int slot) => TagPoolPrefix + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static bool IsSignatureTag(string tag)
            => tag != null && (tag.StartsWith(TagSignaturePrefix, StringComparison.Ordinal) || tag.StartsWith(TagPoolPrefix, StringComparison.Ordinal));

        private static bool TryPoolSlot(string tag, out int slot)
        {
            slot = -1;
            return tag != null && tag.StartsWith(TagPoolPrefix, StringComparison.Ordinal)
                && int.TryParse(tag.Substring(TagPoolPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out slot)
                && slot >= 0 && slot < HarbingerLayout.PoolSlotsUV.Count;
        }

        private double DripInterval => pressured ? settings.DripPressureSeconds : settings.DripSeconds;

        /// <summary>Twin conductors' revive time: halved once the doorway pressure has begun (the override floor 4 s keeps it at least 2 s).</summary>
        private double ReviveSeconds => pressured ? settings.TwinReviveSeconds / 2.0 : settings.TwinReviveSeconds;

        // ---- Corrosion pools ----

        /// <summary>The poll's read of the live guardians' positions (the acid signature only: a pool needs where one fell).</summary>
        private void TrackGuards(IPinnacleFightPort port)
        {
            if (!signatureOn || current == null || current.Element != HarbingerElement.Acid)
                return;

            foreach (var g in guards)
                if (!g.IsNone && port.IsAlive(g) && port.TryGetPosition(g, out var at))
                    guardSeen[g] = at;
        }

        /// <summary>
        /// A slot is free when a pool there would not overlap any live pool: the two squares are at least a pool's edge apart on
        /// one axis or the other (touching edges allowed). At the default 2.25 m neighbouring grid slots (2.25 m apart) are free
        /// of each other; the two sill slots, a pulled-in corner and its neighbours, and any neighbours of a larger pool are not.
        /// </summary>
        private bool SlotFree(HarbingerAlcove alcove, int slot)
        {
            if (pools.Contains(slot))
                return false;

            var c = HarbingerLayout.PoolSlot(alcove, slot);
            var size = settings.PoolSize - 1e-4;   // float slot positions: an exact edge-to-edge pair still counts as apart

            foreach (var other in pools)
            {
                var o = HarbingerLayout.PoolSlot(alcove, other);

                if (Math.Abs(o.X - c.X) < size && Math.Abs(o.Y - c.Y) < size)
                    return false;
            }

            return true;
        }

        private List<int> FreeSlots(HarbingerAlcove alcove)
            => Enumerable.Range(0, HarbingerLayout.PoolSlotsUV.Count).Where(i => SlotFree(alcove, i)).ToList();

        /// <summary>The deepest a pool's surface goes (z): the alcove floor plus the settings' max depth.</summary>
        private double PoolTopZ => HazardRegionPresets.HarbingerAlcoveGroundZ + settings.PoolMaxDepth;

        /// <summary>
        /// The crystal drips (round 3): ONE drop on a slot drawn with the fight's Rng. A pool that can still deepen is aimed at
        /// with <see cref="HarbingerSettings.DripDeepenShare"/> when a free slot under the cap is also open (always when none
        /// is), a free slot otherwise; with every pool at its deepest and none free the drop still falls on a pool (it hurts,
        /// it raises nothing). No pool and no free slot (pool_cap 0): no drop.
        /// </summary>
        private void Drip(IPinnacleFightPort port)
        {
            var alcove = HarbingerData.AlcoveOf(current.Element);
            var deepenable = pools.Where(sl => poolZ.TryGetValue(sl, out var z) && z < PoolTopZ - 1e-9).ToList();
            var free = pools.Count < settings.PoolCap ? FreeSlots(alcove) : new List<int>();
            int slot;

            if (deepenable.Count > 0 && (free.Count == 0 || port.Rng.NextDouble() < HarbingerSettings.DripDeepenShare))
                slot = deepenable[port.Rng.Next(deepenable.Count)];
            else if (free.Count > 0)
                slot = free[port.Rng.Next(free.Count)];
            else if (pools.Count > 0)
                slot = pools[port.Rng.Next(pools.Count)];
            else
                return;

            StartDrip(port, slot);
        }

        /// <summary>One drop on <paramref name="slot"/>: a one-shot rain run on a small area round the slot (drip-&lt;n&gt;).</summary>
        private void StartDrip(IPinnacleFightPort port, int slot)
        {
            var alcove = HarbingerData.AlcoveOf(current.Element);
            var tag = TagDripPrefix + (++dripSeq).ToString(System.Globalization.CultureInfo.InvariantCulture);

            drips[tag] = slot;
            port.StartHazard(tag, HazardMechanicRegistry.Rain, null, settings.DripArgs(ScaledPct(port, settings.DripPct)), default,
                new PinnacleHazardOptions(Area: HarbingerLayout.PoolArea(alcove, slot, HarbingerSettings.DripAreaSize)));
        }

        /// <summary>
        /// A drop landed on <paramref name="slot"/> (its run's Completed end): the pool there deepens; with none there, a new
        /// pool starts when the slot is free and the cap allows; otherwise (the cap is reached, or a guardian's pool has since
        /// covered the spot) the pool nearest the slot deepens instead. Only while the acid signature runs.
        /// </summary>
        private void DripLanded(IPinnacleFightPort port, int slot)
        {
            if (!signatureOn || stage != HarbingerStage.Alcove || current == null || current.Element != HarbingerElement.Acid)
                return;

            if (pools.Contains(slot))
            {
                RaisePool(port, slot);
                return;
            }

            var alcove = HarbingerData.AlcoveOf(current.Element);

            if (pools.Count < settings.PoolCap && SlotFree(alcove, slot))
            {
                StartPool(port, slot);
                return;
            }

            var at = HarbingerLayout.PoolSlot(alcove, slot);
            var nearest = pools.OrderBy(sl => System.Numerics.Vector2.Distance(HarbingerLayout.PoolSlot(alcove, sl), at)).ThenBy(sl => sl).Cast<int?>().FirstOrDefault();

            if (nearest.HasValue)
                RaisePool(port, nearest.Value);
        }

        /// <summary>
        /// The pool on <paramref name="slot"/> rises one step, up to its deepest: the remembered surface first (a restart starts
        /// from it), then the live run is told to rise at <see cref="HarbingerSettings.PoolRiseRate"/> and stop at the new surface.
        /// </summary>
        private void RaisePool(IPinnacleFightPort port, int slot)
        {
            var old = poolZ.TryGetValue(slot, out var z) ? z : HarbingerSettings.PoolSurfaceZ;
            var next = Math.Min(old + settings.PoolStep, PoolTopZ);

            if (next <= old + 1e-9)
                return;

            poolZ[slot] = next;

            var tag = PoolTag(slot);

            if (port.ControlHazard(tag, "rate", HarbingerSettings.PoolRiseRate))
                port.ControlHazard(tag, "target", next);
        }

        /// <summary>
        /// A guardian fell at <paramref name="at"/>: a pool at the free slot nearest it (ties to the lower slot), while under the
        /// cap. Nothing when it fell outside the alcove (below the arrival height or beyond the region's player reach) or onto a
        /// pool already there.
        /// </summary>
        private void PoolAtDeath(IPinnacleFightPort port, PinnaclePoint at)
        {
            if (pools.Count >= settings.PoolCap)
                return;

            var alcove = HarbingerData.AlcoveOf(current.Element);

            if (at.Z < AlcoveArrivalMinZ || Distance2D(at.X, at.Y, alcove.Centre.X, alcove.Centre.Y) > HazardRegionPresets.HarbingerAlcovePlayerRadius)
                return;

            var half = settings.PoolSize * 0.5;

            foreach (var slot in pools)
            {
                var c = HarbingerLayout.PoolSlot(alcove, slot);

                if (Math.Abs(at.X - c.X) <= half && Math.Abs(at.Y - c.Y) <= half)
                    return;
            }

            var best = -1;
            var bestD = double.MaxValue;

            foreach (var slot in FreeSlots(alcove))
            {
                var c = HarbingerLayout.PoolSlot(alcove, slot);
                var d = Distance2D(at.X, at.Y, c.X, c.Y);

                if (d < bestD - 1e-9)
                {
                    best = slot;
                    bestD = d;
                }
            }

            if (best >= 0)
                StartPool(port, best);
        }

        private void StartPool(IPinnacleFightPort port, int slot)
        {
            var tag = PoolTag(slot);

            pools.Add(slot);
            poolZ[slot] = HarbingerSettings.PoolSurfaceZ;
            wanted.Add(tag);
            StartSignatureRun(port, tag);
        }

        // ---- Twin conductors ----

        /// <summary>
        /// revive-&lt;index&gt; fired: guard <paramref name="index"/> rises again at its spawn point when the signature still runs,
        /// the ward still stands, it is not standing already, and its twin (a live guardian of the other index) is up. It
        /// joins the ward; nothing is re-shielded (the shield never dropped: it drops only when both are down at once).
        /// </summary>
        private void Revive(IPinnacleFightPort port, int index)
        {
            if (!signatureOn || stage != HarbingerStage.Alcove || current == null || current.Element != HarbingerElement.Lightning || !shielded)
                return;

            if (index < 0 || index >= HarbingerData.AlcoveOf(current.Element).Guards.Count)
                return;

            var standing = guards.Where(g => !g.IsNone && port.IsAlive(g) && guardIndex.ContainsKey(g)).Select(g => guardIndex[g]).ToList();

            if (standing.Contains(index) || !standing.Any(i => i != index))
                return;

            var h = AddGuard(port, current, index);

            if (h.IsNone)
                return;   // its SpawnFailed takes the None entry out of the ward again

            port.Engage(h);
            port.Say(HarbingerLines.LineTwinRevive);
        }

        // ================================================================ summons

        private void Summon(IPinnacleFightPort port)
        {
            if (stage == HarbingerStage.Final)
            {
                SpawnFinalSummons(port);
                return;
            }

            // the element is over once its crystal is dead
            if (stage == HarbingerStage.Landing)
                return;

            if (LiveSummons(port, current.SummonTag) < settings.SummonCap)
                SpawnSummon(port, current);
        }

        /// <summary>Phase 5: one of each kind that has none alive, while fewer than the final cap live in all.</summary>
        private void SpawnFinalSummons(IPinnacleFightPort port)
        {
            foreach (var def in HarbingerData.Elements)
            {
                if (HarbingerData.Elements.Sum(d => LiveSummons(port, d.SummonTag)) >= settings.FinalSummonCap)
                    return;

                if (LiveSummons(port, def.SummonTag) == 0)
                    SpawnSummon(port, def);
            }
        }

        private int LiveSummons(IPinnacleFightPort port, string tag)
        {
            if (!summons.TryGetValue(tag, out var list))
                return 0;

            list.RemoveAll(h => !port.IsAlive(h));
            return list.Count;
        }

        /// <summary>One summon on a shuffled ring point not within <see cref="SummonClearance"/> of the owner; then it engages.</summary>
        private void SpawnSummon(IPinnacleFightPort port, HarbingerElementDef def)
        {
            var points = HarbingerData.SummonPoints.ToList();

            for (var i = points.Count - 1; i > 0; i--)
            {
                var j = port.Rng.Next(i + 1);
                (points[i], points[j]) = (points[j], points[i]);
            }

            var hasOwner = port.TryGetOwnerPosition(out var owner);

            foreach (var at in points)
            {
                if (hasOwner && Distance2D(at.X, at.Y, owner.X, owner.Y) < SummonClearance)
                    continue;

                var h = port.SpawnAdd(def.SummonTag, def.SummonWcid, at, HarbingerData.AddTier);

                // a spawn that failed twice (None) is tracked by nothing
                if (h.IsNone)
                    return;

                if (!summons.TryGetValue(def.SummonTag, out var list))
                    summons[def.SummonTag] = list = new List<PinnacleHandle>();

                list.Add(h);
                port.Engage(h);
                return;
            }
        }

        // ================================================================ casts

        /// <summary>
        /// The head's cast, skipped while the owner is up in an alcove (or unreadable). Casts 1, 6, 11 ... are the element's
        /// vulnerability, the rest its war spells; phase 5 picks the element with the fight's Rng.
        /// </summary>
        private void Cast(IPinnacleFightPort port)
        {
            if (!port.TryGetOwnerPosition(out var owner) || owner.Z > CastMinZ)
                return;

            var def = current ?? HarbingerData.Elements[port.Rng.Next(HarbingerData.Elements.Count)];
            var vuln = headCasts % HarbingerData.CastEvery == 0;

            headCasts++;
            port.CastAtOwnerNow(port.Head, vuln ? def.VulnSpell : def.WarSpells[port.Rng.Next(def.WarSpells.Count)]);
        }

        /// <summary>
        /// The delay to the crystal's next cast when its timer is (re)armed by the arrival or by the ward link: the interval, but
        /// never before the arrival grace ends (round 4), so the first cast after an arrival cannot hit a player who has not had the
        /// grace. The later cadence (the timer's own re-arm) stays the interval. With no grace it is exactly the interval.
        /// </summary>
        private double CrystalCastDelay(IPinnacleFightPort port)
            => Math.Max(CrystalCastInterval, hazardsLive ? 0.0 : graceEnd - port.Now);

        /// <summary>The crystal's cast interval now: halved once the ward link is set.</summary>
        private double CrystalCastInterval => wardLinked ? settings.CrystalCastSeconds / 2.0 : settings.CrystalCastSeconds;

        /// <summary>
        /// The crystal's scripted cast: only while it is alive and the owner is inside its alcove. Its casts 1, 1 + CastEvery ...
        /// are the element's vulnerability, the rest its war spells (the head's cadence, on the crystal's own counter).
        /// </summary>
        private void CrystalCast(IPinnacleFightPort port)
        {
            if (crystal.IsNone || !port.IsAlive(crystal) || !IsOwnerInAlcove(port))
                return;

            var def = current;
            var vuln = crystalCasts % HarbingerData.CastEvery == 0;

            crystalCasts++;
            port.CastAtOwnerNow(crystal, vuln ? def.VulnSpell : def.WarSpells[port.Rng.Next(def.WarSpells.Count)]);
        }

        private static double Distance2D(double ax, double ay, double bx, double by)
        {
            var dx = ax - bx;
            var dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
