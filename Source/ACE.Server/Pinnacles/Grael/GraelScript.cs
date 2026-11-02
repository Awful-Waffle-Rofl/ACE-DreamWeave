using System;
using System.Collections.Generic;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Grael
{
    /// <summary>
    /// The Grael boss script (Docs/Knots/PINNACLE-ENCOUNTER-PLAN.md M3, with section 7 applied). Pure logic behind
    /// <see cref="IPinnacleFightPort"/>: no WorldObject, PropertyManager, DateTime or new Random.
    ///
    /// ONE INSTANCE PER FIGHT: the script keeps the current phase's bookkeeping (variant handles, kills, the
    /// memory length, the ball series) in fields. EnterPhase rebuilds all of it, so a jump to any phase is safe. The one
    /// exception is the head cast counter, which runs across the whole fight on purpose (see <see cref="CastHead"/>).
    ///
    /// RESTART. RestartPhase runs after the events queued during a hold were delivered, and those handlers may
    /// already have started a hazard at this resume. The script therefore never asks "is it running" (the port
    /// cannot say); it calls StopHazards (which stops and forgets every run) and then starts the phase's series
    /// again, so exactly one run is live afterwards whatever the handlers did. Timers survive a hold in the core
    /// and SetTimer replaces a tag's deadline, so re-setting a cast timer is idempotent too.
    ///
    /// RETRY. Only the core retries. A HazardEnded this script sees is final: Completed, Failed, or Aborted after
    /// the retry. Aborted never soft-locks a phase: it is treated like a pass (P3, P4) or is retried after a pause, a capped number of times (P1 wisps, P5 wisps and balls).
    ///
    /// PHASE 2 IS RETIRED (round 4 playtest, owner): the scatter wisps that were P2 now run DURING P1, the Grael body fight,
    /// looping until the body dies, and the body's death ends both. The phase ids stay 1..5 (the settings, their shard
    /// descriptions, the tags and the admin jump all name them), so P2 is a pass-through that advances to P3 the moment
    /// it is entered (an admin jump to 2 lands in 3).
    /// </summary>
    public sealed class GraelScript : IPinnacleBossScript
    {
        /// <summary>The scatter wisps (GraelSettings.P2Series). Named for the retired P2; since round 4 they run in P1.</summary>
        public const string TagP2 = "p2";

        /// <summary>An Aborted P1 wisp run is tried again after <see cref="P5WispRetrySeconds"/>, at most <see cref="MaxP5WispRetries"/> times.</summary>
        public const string TimerP1WispRetry = "p1-wisp-retry";

        public const string TagP3Round = "p3-round";

        public const string TagP5Wisps = "p5";

        /// <summary>The P5 scattered balls (random stream, GraelSettings.P5Balls), looped while P5 lasts.</summary>
        public const string TagP5Balls = "p5-balls";

        /// <summary>The P4 corner pillars (zones / corners): on from the start of the fan series through the rings, stopped before the next series.</summary>
        public const string TagP4Corners = "p4-corners";

        /// <summary>P3 rounds: 1 memory only; 2, 3, 4 memory then a wave of one colour; 5 three of each, no memory.</summary>
        public const int P3Rounds = 5;

        public const string TimerP4Cast = "p4-cast";

        /// <summary>The head's casts in P1 and P3 (owner, round 4 playtest: the head casts from the start of the fight).</summary>
        public const string TimerEarlyCast = "early-cast";

        /// <summary>
        /// Seconds between the head's P1 and P3 casts: twice the default P4 interval (GraelSettings.DefaultP4CastSeconds = 4).
        /// P1 already carries the Grael body and the wisps and P3 asks the player to watch the memory beams, so the head adds
        /// steady pressure there and its full cadence stays for P4 ("Run until you cannot") and P5. A code constant, not a
        /// setting: no new shard row. Casts through the one head picker (<see cref="CastHead"/>), like P4 and P5.
        /// </summary>
        public const double EarlyCastSeconds = 8;

        public const string TimerP5Cast = "p5-cast";

        public const string TimerP5WispRetry = "p5-wisp-retry";

        public const string TimerP5BallRetry = "p5-ball-retry";

        public const string TimerRespawnPrefix = "respawn-";

        /// <summary>
        /// An Aborted P5 wisp (or ball) run is restarted after this many seconds, at most <see cref="MaxP5WispRetries"/>
        /// times per phase for each of the two.
        /// </summary>
        public const double P5WispRetrySeconds = 10;

        public const int MaxP5WispRetries = 5;

        /// <summary>
        /// P4 series, in order: pattern, hazard tag, and whether it is centred on the head. ONE ring series whose waves
        /// alternate out and in (ring-alt, 2026-10-19 playtest), with twice the waves (GraelSettings.P4RingArgs) so the
        /// ring waves total what the old separate ring-out and ring-in series ran. Round 3 playtest (owner): the random
        /// stream runs BEFORE the rows and the cross, so the phase builds from the easiest series to the hardest.
        /// </summary>
        private static readonly (string Pattern, string Tag, bool FromHead)[] P4Series =
        {
            (BallPatterns.Fan, "p4-fan", true),
            (BallPatterns.RingAlt, "p4-rings", true),
            (BallPatterns.Random, "p4-stream", false),
            (BallPatterns.Row, "p4-row", false),
            (BallPatterns.Cross, "p4-cross", false),
        };

        /// <summary>The P4 series the corner pillars burn through (owner ruling 2026-10-20: the fan and the rings only).</summary>
        internal static bool CornersOnDuring(string pattern) => pattern == BallPatterns.Fan || pattern == BallPatterns.RingAlt;

        /// <summary>The P4 series patterns in run order (tests read it).</summary>
        internal static IReadOnlyList<string> P4Order => Array.ConvertAll(P4Series, s => s.Pattern);

        private readonly GraelSettings settings;
        private readonly PinnacleScriptData data = GraelData.Script();

        private int phase;

        // ---- P1 ----
        private int p1WispRetries;
        private bool p1WispRetryPending;

        /// <summary>A P1 wisp run has completed (its ramp has been played once): every later run holds the hardest values flat.</summary>
        private bool p1WispRampDone;

        // ---- P5 variants ----
        private readonly Dictionary<string, PinnacleHandle> variants = new Dictionary<string, PinnacleHandle>(StringComparer.Ordinal);

        // ---- P3 ----
        /// <summary>The current round's variants by tag ("blue-1"); a death (or a failed spawn) removes its tag.</summary>
        private readonly Dictionary<string, PinnacleHandle> roundAdds = new Dictionary<string, PinnacleHandle>(StringComparer.Ordinal);
        private int memoryRound;
        private bool roundActive;   // the round's memory sequence is running
        private bool roundPassed;   // the round's sequence was passed: its variants are killable

        // ---- P4 ----
        private int series;

        // ---- head casts (every phase; never reset: one counter for the whole fight, see GraelData.HeadVulnEvery) ----
        private int headCasts;

        // ---- P5 ----
        private int p5WispRetries;
        private bool p5WispRetryPending;
        private int p5BallRetries;
        private bool p5BallRetryPending;

        public GraelScript(): this(GraelSettings.Defaults)
        {
        }

        public GraelScript(GraelSettings settings)
        {
            this.settings = settings ?? GraelSettings.Defaults;
        }

        public string Kind => GraelData.Kind;

        public PinnacleScriptData Data => data;

        public int PhaseCount => 5;

        public void OnStart(IPinnacleFightPort port) => port.Say(GraelData.LineStart);

        public void EnterPhase(int n, IPinnacleFightPort port)
        {
            phase = n;
            variants.Clear();
            roundAdds.Clear();
            memoryRound = 0;
            roundActive = false;
            roundPassed = false;
            series = 0;
            p1WispRetries = 0;
            p1WispRetryPending = false;
            p1WispRampDone = false;
            p5WispRetries = 0;
            p5WispRetryPending = false;
            p5BallRetries = 0;
            p5BallRetryPending = false;

            // The head can only be hurt in P5: attackable AND not immune there, immune (spell-proof too) everywhere else.
            port.SetAttackable(port.Head, n == 5);
            port.SetImmune(port.Head, n != 5);

            switch (n)
            {
                case 1:
                    // Round 4 playtest: the Grael body AND the scatter wisps (the retired P2's series) at once.
                    port.SpawnAdd(GraelData.GraelTag, GraelData.GraelWcid, GraelData.GraelSpawn, GraelData.GraelTier);
                    port.Say(GraelData.LineP2);
                    StartP2(port);
                    port.SetTimer(TimerEarlyCast, EarlyCastSeconds);
                    break;

                case 2:
                    // retired: straight on to the memory phase (see the class comment)
                    port.AdvancePhase();
                    break;

                case 3:
                    port.Say(GraelData.LineP3);
                    port.SetTimer(TimerEarlyCast, EarlyCastSeconds);
                    BeginRound(1, port);
                    break;

                case 4:
                    port.Say(GraelData.LineP4);
                    StartSeries(0, port);
                    port.SetTimer(TimerP4Cast, settings.P4CastSeconds);
                    break;

                case 5:
                    port.Say(GraelData.LineP5);
                    SpawnVariants(port);

                    // Round 3 playtest (owner): the original Grael body joins the variants in P5, at Boss tier. It is
                    // never respawned, and its death does nothing: only the head's death ends the fight, and the win's
                    // ClearPhase destroys whatever adds are still alive.
                    port.SpawnAdd(GraelData.GraelTag, GraelData.GraelWcid, GraelData.GraelSpawn, GraelData.GraelTier);

                    StartP5Wisps(port);
                    StartP5Balls(port);
                    CastHead(port);
                    port.SetTimer(TimerP5Cast, settings.P5CastSeconds);
                    break;
            }
        }

        public void OnEvent(PinnacleEvent e, IPinnacleFightPort port)
        {
            // Defensive (review C4): the head dying ends the fight in ANY phase, not only P5 - however it died, a dead
            // head with the fight still running would otherwise strand the attempt.
            if (e.Kind == PinnacleEventKind.AddDied && e.Tag == PinnacleEvent.HeadTag)
            {
                port.Say(GraelData.LineHeadDied);
                port.Win();
                return;
            }

            // P1 and P3: the head's early casts (P4 and P5 run their own cast timers)
            if ((phase == 1 || phase == 3) && e.Kind == PinnacleEventKind.TimerFired && e.Tag == TimerEarlyCast)
            {
                CastHead(port);
                port.SetTimer(TimerEarlyCast, EarlyCastSeconds);
                return;
            }

            switch (phase)
            {
                case 1:
                    OnEventP1(e, port);
                    break;

                case 3:
                    OnEventP3(e, port);
                    break;

                case 4:
                    OnEventP4(e, port);
                    break;

                case 5:
                    OnEventP5(e, port);
                    break;
            }
        }

        public void RestartPhase(IPinnacleFightPort port)
        {
            switch (phase)
            {
                case 1:
                    // the Grael body keeps its health; only the wisps start again
                    port.StopHazards();
                    p1WispRetryPending = false;
                    StartP2(port);
                    port.SetTimer(TimerEarlyCast, EarlyCastSeconds);
                    break;

                case 3:
                    // the round and its variants stand; only a memory sequence still in progress restarts, at the same
                    // length, with no new spawns
                    if (roundActive)
                    {
                        port.StopHazards();
                        StartMemory(port);
                    }

                    port.SetTimer(TimerEarlyCast, EarlyCastSeconds);
                    break;

                case 4:
                    port.StopHazards();
                    StartSeries(0, port);
                    port.SetTimer(TimerP4Cast, settings.P4CastSeconds);
                    break;

                case 5:
                    // the head and the variants keep their health; the respawn timers are untouched
                    port.StopHazards();
                    p5WispRetryPending = false;
                    p5BallRetryPending = false;
                    StartP5Wisps(port);
                    StartP5Balls(port);
                    port.SetTimer(TimerP5Cast, settings.P5CastSeconds);
                    break;
            }
        }

        public string Describe()
        {
            switch (phase)
            {
                case 3:
                    return $"grael p3 round={memoryRound}/{P3Rounds} adds={roundAdds.Count} memory={(roundActive ? "active" : roundPassed ? "passed" : "none")} headcasts={headCasts}";
                case 4:
                    return $"grael p4 series={series}/{P4Series.Length} headcasts={headCasts}";
                default:
                    return $"grael p{phase} headcasts={headCasts}";
            }
        }

        // ================================================================ P1 (the Grael body and the wisps)

        /// <summary>
        /// The P1 wisps. Round 4 ruling: the first run ramps easy to hard (GraelSettings.P2Series); once a run has
        /// completed, every run after it (the loop, a retry, a restart after a hold) holds the hardest values flat
        /// (GraelSettings.P2SeriesFlat).
        /// </summary>
        private void StartP2(IPinnacleFightPort port)
        {
            var (pattern, args) = p1WispRampDone ? settings.P2SeriesFlat(port.Level) : settings.P2Series(port.Level);
            port.StartHazard(TagP2, HazardMechanicRegistry.Wisps, pattern, args);
        }

        /// <summary>
        /// The Grael body's death (or its failed spawn) ends P1, and the phase change stops the wisps with it. Until then the
        /// wisp series loops: a completed run starts again at once, an Aborted one after a pause, a capped number of times.
        /// </summary>
        private void OnEventP1(PinnacleEvent e, IPinnacleFightPort port)
        {
            switch (e.Kind)
            {
                case PinnacleEventKind.AddDied:
                    if (e.Tag == GraelData.GraelTag)
                        port.AdvancePhase();
                    break;

                case PinnacleEventKind.HazardEnded:
                    if (e.Tag != TagP2)
                        break;

                    if (e.Outcome == HazardOutcome.Completed)
                    {
                        // a completed run resets the cap: only CONSECUTIVE aborts count toward it
                        p1WispRampDone = true;
                        p1WispRetries = 0;
                        StartP2(port);
                    }
                    else if (p1WispRetries < MaxP5WispRetries)
                    {
                        p1WispRetries++;
                        p1WispRetryPending = true;
                        port.SetTimer(TimerP1WispRetry, P5WispRetrySeconds);
                    }
                    break;

                case PinnacleEventKind.TimerFired:
                    // RestartPhase clears the flag: a retry timer that survived a hold must not start a second run
                    if (e.Tag == TimerP1WispRetry && p1WispRetryPending)
                    {
                        p1WispRetryPending = false;
                        StartP2(port);
                    }
                    break;
            }
        }

        // ================================================================ P3

        private void SpawnVariants(IPinnacleFightPort port)
        {
            foreach (var (tag, wcid, at) in GraelData.Variants)
                variants[tag] = port.SpawnAdd(tag, wcid, at, GraelData.VariantTier);
        }

        /// <summary>
        /// The memory length of P3 round <paramref name="round"/> (1..4): the opening length plus round - 1, so L = 4, 5, 6,
        /// 7 by default and the pulse ramp of GraelSettings.MemoryRoundArgs runs its full span over the four memory rounds.
        /// </summary>
        internal int MemoryLengthFor(int round) => settings.MemoryOpeningLength + round - 1;

        /// <summary>
        /// Owner ruling 2026-10-20. Round 1: a memory sequence only. Rounds 2, 3, 4: <see cref="GraelData.P3WaveSize"/>
        /// variants of one colour (Blue, Red, Green) spawn at the memory start, immune until the sequence is passed, then
        /// killable; the next round begins when all of them are dead. Round 5: no memory,
        /// <see cref="GraelData.P3FinalEach"/> of each colour, killable at once; the phase ends when all are dead.
        /// </summary>
        private void BeginRound(int round, IPinnacleFightPort port)
        {
            memoryRound = round;
            roundAdds.Clear();
            roundActive = false;
            roundPassed = false;

            if (round >= P3Rounds)
            {
                port.Say(GraelData.LineP3FinalRound);

                var point = 0;

                foreach (var (colour, wcid, _) in GraelData.Variants)
                    for (var i = 1; i <= GraelData.P3FinalEach; i++)
                        SpawnRoundAdd(GraelData.P3Tag(colour, i), wcid, GraelData.VariantPoints[point++], port);

                roundPassed = true;   // nothing to remember: killable from the start
                CheckRoundCleared(port);
                return;
            }

            if (round >= 2)
            {
                var (colour, wcid) = GraelData.P3WaveColours[round - 2];

                for (var i = 1; i <= GraelData.P3WaveSize; i++)
                    SpawnRoundAdd(GraelData.P3Tag(colour, i), wcid, GraelData.VariantPoints[i - 1], port);
            }

            StartMemory(port);
        }

        private void SpawnRoundAdd(string tag, uint wcid, PinnaclePoint at, IPinnacleFightPort port)
            => roundAdds[tag] = port.SpawnAdd(tag, wcid, at, GraelData.VariantTier);

        /// <summary>The current round's memory sequence at its own length: every live variant of the round is made immune first.</summary>
        private void StartMemory(IPinnacleFightPort port)
        {
            roundActive = true;
            SetRoundAddsImmune(port, true);
            port.Say(GraelData.LineMemoryRound(memoryRound, GraelSettings.MemoryRounds));
            port.StartHazard(TagP3Round, HazardMechanicRegistry.Memory, null, settings.MemoryRoundArgs(MemoryLengthFor(memoryRound), port.Level));
        }

        private void SetRoundAddsImmune(IPinnacleFightPort port, bool immune)
        {
            foreach (var h in roundAdds.Values)
                if (port.IsAlive(h))
                    port.SetImmune(h, immune);
        }

        private void OnEventP3(PinnacleEvent e, IPinnacleFightPort port)
        {
            if (e.Kind == PinnacleEventKind.AddDied)
            {
                // a death or a failed spawn (SpawnFailed: Handle None) of the current round; anything else is stale
                if (roundAdds.TryGetValue(e.Tag ?? string.Empty, out var h) && (e.SpawnFailed ? h.IsNone : h == e.Handle))
                {
                    roundAdds.Remove(e.Tag);
                    CheckRoundCleared(port);
                }

                return;
            }

            if (e.Kind != PinnacleEventKind.HazardEnded || e.Tag != TagP3Round || !roundActive)
                return;

            roundActive = false;

            if (e.Outcome == HazardOutcome.Failed)
            {
                port.Say(GraelData.LineMemoryFailed);
                port.DispelOwnerBuffs(settings.P3DispelCount);
                StartMemory(port);   // the same length; the variants stay alive and immune
                return;
            }

            // Completed, or Aborted after the core's retry (never a soft-lock): the round counts as passed
            roundPassed = true;
            SetRoundAddsImmune(port, false);

            if (memoryRound == 1)
                BeginRound(2, port);
            else
                CheckRoundCleared(port);
        }

        /// <summary>A passed round with every variant dead moves on: rounds 2-4 to the next round, round 5 out of the phase.</summary>
        private void CheckRoundCleared(IPinnacleFightPort port)
        {
            if (!roundPassed || roundAdds.Count > 0)
                return;

            if (memoryRound >= P3Rounds)
                port.AdvancePhase();
            else if (memoryRound >= 2)
                BeginRound(memoryRound + 1, port);
        }

        private static bool IsVariant(string tag)
            => tag == GraelData.RedTag || tag == GraelData.GreenTag || tag == GraelData.BlueTag;

        // ================================================================ P4

        private void StartSeries(int index, IPinnacleFightPort port)
        {
            series = index;
            var (pattern, tag, fromHead) = P4Series[index];

            // Corner fire pillars punish hiding during the fan and the rings (owner ruling 2026-10-20): on with the fan
            // (index 0, which every P4 entry and restart starts from), off before the first series that is neither (the
            // random stream since round 3; the corners were never asked for there). A phase change stops them anyway.
            if (pattern == BallPatterns.Fan)
            {
                var (cornersPattern, cornersArgs) = settings.P4CornersSeries(port.Level);
                port.StartHazard(TagP4Corners, HazardMechanicRegistry.Zones, cornersPattern, cornersArgs);
            }
            else if (!CornersOnDuring(pattern) && index > 0 && CornersOnDuring(P4Series[index - 1].Pattern))
            {
                port.StopHazard(TagP4Corners);
            }

            // each series' own pins (GraelSettings.P4Args), pct scaled to the Knot level
            var args = settings.P4Args(pattern, port.Level);

            if (fromHead)
                port.StartHazard(tag, HazardMechanicRegistry.Balls, pattern, args, port.Head);
            else
                port.StartHazard(tag, HazardMechanicRegistry.Balls, pattern, args);
        }

        private void OnEventP4(PinnacleEvent e, IPinnacleFightPort port)
        {
            if (e.Kind == PinnacleEventKind.HazardEnded && e.Tag == P4Series[series].Tag)
            {
                if (series + 1 >= P4Series.Length)
                    port.AdvancePhase();
                else
                    StartSeries(series + 1, port);

                return;
            }

            if (e.Kind == PinnacleEventKind.TimerFired && e.Tag == TimerP4Cast)
            {
                CastHead(port);
                port.SetTimer(TimerP4Cast, settings.P4CastSeconds);
            }
        }

        // ================================================================ P5

        private void StartP5Wisps(IPinnacleFightPort port)
        {
            var (pattern, args) = settings.P5Series(port.Level);
            port.StartHazard(TagP5Wisps, HazardMechanicRegistry.Wisps, pattern, args);
        }

        /// <summary>The P5 scattered balls: one random stream (GraelSettings.P5Balls), started again each time one completes.</summary>
        private void StartP5Balls(IPinnacleFightPort port)
            => port.StartHazard(TagP5Balls, HazardMechanicRegistry.Balls, BallPatterns.Random, settings.P5BallArgs(port.Level));

        /// <summary>
        /// THE head cast picker, for every phase that casts (the P1/P3 early timer, the P4 timer, the P5 entry and timer).
        /// One counter across the whole fight (round 6, owner ask 2026-10-20): the first head cast of the fight and every
        /// fifth after it is Fire Vulnerability (<see cref="GraelData.HeadVulnSpell"/>); the rest are the P4 war spells.
        /// </summary>
        private void CastHead(IPinnacleFightPort port)
        {
            var vuln = headCasts % GraelData.HeadVulnEvery == 0;
            headCasts++;

            var spell = vuln ? GraelData.HeadVulnSpell : GraelData.P4Spells[port.Rng.Next(GraelData.P4Spells.Count)];
            port.CastAtOwner(port.Head, spell);
        }

        private void OnEventP5(PinnacleEvent e, IPinnacleFightPort port)
        {
            switch (e.Kind)
            {
                case PinnacleEventKind.AddDied:
                    // the head's death is handled ahead of the phase switch (OnEvent)
                    if (IsVariant(e.Tag))
                        port.SetTimer(TimerRespawnPrefix + e.Tag, settings.P5RespawnSeconds);
                    break;

                case PinnacleEventKind.HazardEnded:
                    // loop the wisps and the balls; an Aborted run (the core already retried it) is tried again after a
                    // pause, a few times
                    if (e.Tag == TagP5Wisps)
                    {
                        if (e.Outcome == HazardOutcome.Completed)
                        {
                            StartP5Wisps(port);
                        }
                        else if (p5WispRetries < MaxP5WispRetries)
                        {
                            p5WispRetries++;
                            p5WispRetryPending = true;
                            port.SetTimer(TimerP5WispRetry, P5WispRetrySeconds);
                        }
                    }
                    else if (e.Tag == TagP5Balls)
                    {
                        if (e.Outcome == HazardOutcome.Completed)
                        {
                            StartP5Balls(port);
                        }
                        else if (p5BallRetries < MaxP5WispRetries)
                        {
                            p5BallRetries++;
                            p5BallRetryPending = true;
                            port.SetTimer(TimerP5BallRetry, P5WispRetrySeconds);
                        }
                    }
                    break;

                case PinnacleEventKind.TimerFired:
                    if (e.Tag == TimerP5WispRetry)
                    {
                        // RestartPhase clears the flag: a retry timer that survived a hold must not start a second run
                        if (p5WispRetryPending)
                        {
                            p5WispRetryPending = false;
                            StartP5Wisps(port);
                        }
                    }
                    else if (e.Tag == TimerP5BallRetry)
                    {
                        if (p5BallRetryPending)
                        {
                            p5BallRetryPending = false;
                            StartP5Balls(port);
                        }
                    }
                    else if (e.Tag == TimerP5Cast)
                    {
                        CastHead(port);
                        port.SetTimer(TimerP5Cast, settings.P5CastSeconds);
                    }
                    else if (e.Tag.StartsWith(TimerRespawnPrefix, StringComparison.Ordinal))
                    {
                        Respawn(e.Tag.Substring(TimerRespawnPrefix.Length), port);
                    }
                    break;
            }
        }

        /// <summary>At most one live of each colour.</summary>
        private void Respawn(string colour, IPinnacleFightPort port)
        {
            if (variants.TryGetValue(colour, out var h) && port.IsAlive(h))
                return;

            foreach (var (tag, wcid, at) in GraelData.Variants)
            {
                if (tag == colour)
                {
                    variants[tag] = port.SpawnAdd(tag, wcid, at, GraelData.VariantTier);
                    return;
                }
            }
        }
    }
}
