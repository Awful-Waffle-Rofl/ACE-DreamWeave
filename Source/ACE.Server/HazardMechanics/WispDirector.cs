using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Everything the wisps director needs from the world, so the director can be driven by a recording fake in tests and by
    /// a <see cref="HazardContext"/> in the game (see WispSquadMechanic). <typeparamref name="THandle"/> is HazardHandle live.
    /// </summary>
    public interface IWispPort<THandle> where THandle : class
    {
        Random Rng { get; }

        bool Alive(THandle handle);

        Vector3 PositionAt(THandle handle, double now);

        /// <summary>The point a wisp occupies at (x, y): the region's hazard height.</summary>
        Vector3 PointFor(float x, float y);

        /// <summary>Is there a cell at this point for a moving body (no raise)?</summary>
        bool HasCell(Vector3 point);

        /// <summary>ONE shared glide for the whole crew (HazardContext.MoveLegs).</summary>
        void MoveLegs(IReadOnlyList<(THandle Handle, Vector3 End)> group, double seconds, HazardMoveOptions options);

        /// <summary>The run's live count (HazardRun.LiveCount; null = n). The director reads it ONLY at the first glide of a cycle.</summary>
        int? LiveCount { get; }

        /// <summary>One wisp spawned at <paramref name="point"/> (a point from <see cref="PointFor"/>), moving, to grow the squad; null when it would not spawn.</summary>
        THandle SpawnWisp(Vector3 point);

        /// <summary>One leg for one wisp (the follower): from where it is now to <paramref name="end"/> over <paramref name="seconds"/>, at the hazard height (HazardContext.MoveLeg). False for a dead handle.</summary>
        bool MoveLeg(THandle handle, Vector3 end, double seconds);

        /// <summary>The landblock-local position of the player the follower trails (the first player in the region), or false when nobody is in the region.</summary>
        bool TryFollowTarget(out Vector3 position, out uint targetId);

        /// <summary>Every player in the region (landblock-local position, guid), in the region's own order: the pool follow_target=random draws from.</summary>
        IReadOnlyList<(Vector3 Position, uint Id)> FollowCandidates();

        /// <summary>The driver's tick in seconds (the live cadence), so a leg can be chained one tick before the last one ends.</summary>
        double TickSeconds { get; }

        /// <summary>A pillar effect host burning for <paramref name="seconds"/>; null when it would not spawn.</summary>
        THandle SpawnPillar(Vector3 point, double seconds);

        /// <summary>
        /// mark_dest: a harmless ground mark under <paramref name="point"/> (a wisp's Pillar point) standing for
        /// <paramref name="seconds"/>; null when it would not spawn. Never damages and is never registered for a strike.
        /// </summary>
        THandle SpawnDestMark(Vector3 point, double seconds);

        /// <summary>
        /// ceiling_beam: a harmless beam falling from the ceiling onto <paramref name="pillar"/> (a wisp's Pillar point) for
        /// <paramref name="seconds"/>; null when the region has no ceiling or it would not spawn. Never damages and is never
        /// registered for a strike.
        /// </summary>
        THandle SpawnCeilingBeam(Vector3 pillar, double seconds);

        /// <summary>The run's clock in seconds (HazardContext.Now), the same clock Schedule counts from.</summary>
        double Now { get; }

        /// <summary>Does the region have a ceiling for a beam to fall from? When false the director schedules no beams.</summary>
        bool HasCeiling { get; }

        /// <summary>Runs <paramref name="step"/> after <paramref name="seconds"/> (HazardContext.Schedule: at the first driver tick at or after it).</summary>
        void Schedule(double seconds, Action step);

        /// <summary>Removes one spawned object now (a mark whose burst will never come). Safe on a dead or null handle.</summary>
        void Destroy(THandle handle);

        /// <summary>Where the host really is (its point pulled inside the region edge).</summary>
        Vector3 RestOf(THandle handle);

        /// <summary>
        /// The one damage call: players within radius of centre, once per window PER VOLLEY - every pillar struck with the
        /// same <paramref name="volley"/> (from <see cref="NewHitGroup"/>) shares one hit per player (HazardContext.HitPlayers
        /// hitGroup). Returns how many were hit.
        /// </summary>
        int Strike(THandle source, Vector3 centre, float radius, double window, int volley);

        /// <summary>A fresh volley key for <see cref="Strike"/> (HazardContext.NewHitGroup): never a pillar host's own key.</summary>
        int NewHitGroup();

        void Say(string line);

        void Log(string line);

        void End(HazardOutcome outcome, string reason);
    }

    /// <summary>
    /// The wisps run itself, with the world behind <see cref="IWispPort{THandle}"/>: it turns <see cref="WispScript"/>'s
    /// actions into formation, glide, pillar and damage calls. The mechanic is a thin adapter over this, so the invariants
    /// that matter (one MoveLegs per step with Stand as the end point, the pillar at Pillar, one strike helper, one dead-handle
    /// policy) are testable without a world.
    /// </summary>
    public sealed class WispDirector<THandle> where THandle : class
    {
        private readonly HazardRegion region;
        private readonly WispSettings s;
        private readonly string pattern;
        private readonly IWispPort<THandle> port;
        private readonly List<THandle> squad;
        private readonly WispScript script;
        private readonly WispBurnBook book = new WispBurnBook();

        // the follower (follow=1): one extra wisp that trails the first player in the region; not part of the crew
        private readonly WispTrail trail;
        private THandle follower;
        private double legEndsAt = double.NegativeInfinity;
        private double followStartedAt = double.NaN;
        private uint followTargetId;

        // follow_target=random: set at each cycle's first glide, consumed by the next follower tick (one fresh draw)
        private bool repickTarget;

        // the follower's beat: the current step's crew hold start and first burst (WispFollowRules, "THE BEAT")
        private double beatHoldAt = double.NaN;
        private double beatBurstAt = double.NaN;
        private bool beatPending;
        private bool beatLocked;
        private bool beatMarked;
        private HazardGround.WispPlacement beatPlaced;

        // the mark_dest ground mark of a locked beat, removed if that beat is abandoned before its burst (a mark with no
        // burst under it would be a false telegraph)
        private THandle beatMark;

        // ceiling_beam: the beam of a locked beat (destroyed if the beat is abandoned), and a generation counter so a
        // scheduled beam step only spawns while its own beat is still pending
        private THandle beatBeam;
        private int beatGeneration;

        // the "no ceiling" log line is written once per run, not once per beam
        private bool beamNoteLogged;

        private WispCyclePlan plan;
        private List<THandle> crew = new List<THandle>();
        private HazardGround.WispPlacement[] placed = Array.Empty<HazardGround.WispPlacement>();
        private bool ended;

        // the current volley: the hit-ledger key shared by every pillar that fires in the tick at volleyAt
        private double volleyAt = double.NaN;
        private int volley;

        private int pillars;
        private int hits;
        private int unseen;
        private int alive;

        /// <param name="follower">The extra follower wisp when <c>follow=1</c> (spawned by the mechanic at the first spawn point), else null. It does not count toward n, the crew or the cycles; a dead one is dropped.</param>
        public WispDirector(HazardRegion region, WispSettings settings, string pattern, IWispPort<THandle> port, IReadOnlyList<THandle> wisps, THandle follower = null)
        {
            this.region = region;
            s = settings;
            this.pattern = pattern ?? WispRules.Scatter;
            this.port = port;

            // the caller's own list when it is a List (the director only edits it under a live count), else a copy
            squad = wisps as List<THandle> ?? new List<THandle>(wisps);
            script = new WispScript(settings);
            this.follower = settings.Follow ? follower : null;
            trail = new WispTrail(settings.FollowLag + 2.0);
        }

        public int Pillars => pillars;

        public int Hits => hits;

        /// <summary>Pillars that could not be spawned anywhere and so dealt no damage.</summary>
        public int Unseen => unseen;

        public bool Ended => ended;

        public string Phase => script.PhaseName;

        public int Cycle => script.Cycle;

        public int Step => script.Step;

        public string CyclePattern { get; private set; } = "-";

        /// <summary>Logs the mixed pool once, at the start of the run.</summary>
        public void Start()
        {
            if (pattern == WispRules.Mixed)
                port.Log("mixed pool: " + string.Join(",", WispRules.Pool(region, s)));
        }

        public void Tick(double now)
        {
            if (ended)
                return;

            var live = squad.Count(w => port.Alive(w));
            alive = live; // Describe reads this plain field from the command thread

            foreach (var action in script.Advance(now, live))
            {
                if (ended)
                    break;

                switch (action.Kind)
                {
                    case WispActionKind.Glide:
                        Glide(now, action);
                        break;

                    case WispActionKind.Burst:
                        Burst(now, action.Slot);

                        // the follower bursts in the same tick as the step's first crew burst
                        if (beatPending)
                            FollowerBeat(now);
                        break;

                    case WispActionKind.NextStep:
                        break;

                    case WispActionKind.Rest:
                        port.Log($"cycle {action.Cycle}/{s.Cycles} done, resting {action.Seconds:0.##}s");
                        break;

                    case WispActionKind.End:
                        ended = true;

                        if (action.Outcome == HazardOutcome.Aborted)
                            port.Say($"The wisps are gone ({action.Reason}).");
                        else
                            port.Say("The wisps gutter out.");

                        port.End(action.Outcome, action.Reason);
                        break;
                }
            }

            if (!ended)
                TickFollower(now);

            // THE ONE PILLAR HIT, for every pattern: radius pillar_radius, one hit per VOLLEY per player (the pillars that
            // fire in the same tick share one; a lone pillar is a volley of one, so that is one hit per pillar host).
            foreach (var entry in book.Due(now, o => port.Alive((THandle)o)))
                hits += port.Strike((THandle)entry.Source, entry.Centre, s.HitRadius, s.HitWindow, entry.Volley);
        }

        private void Glide(double now, WispAction action)
        {
            if (action.FirstOfCycle)
            {
                // THE ONE LIVE-COUNT READ: the first glide of each cycle. Null (the default) leaves the squad and the plan
                // exactly as n gave them; a count resizes the squad to it (1..32) and plans the formation for it.
                var planSettings = s;
                var asked = port.LiveCount;

                if (asked.HasValue)
                {
                    var target = Math.Clamp(asked.Value, WispSettings.MinCount, WispSettings.MaxCount);

                    ResizeSquad(target);
                    planSettings = s.WithCount(target);
                }

                if (s.FollowTarget == WispFollowTarget.Random)
                    repickTarget = true;

                plan = WispFormations.Plan(region, planSettings, pattern, port.Rng);
                CyclePattern = plan.Pattern;

                port.Log(FormattableString.Invariant($"cycle {action.Cycle}/{s.Cycles} pattern={plan.Pattern} steps={plan.Steps.Count}"));

                if (WispFormations.IsEmpty(plan))
                {
                    ended = true;
                    port.Say("The wisps cannot find anywhere to fire in this region.");
                    port.End(HazardOutcome.Aborted, "no formation fits the region");
                    return;
                }
            }

            var formation = plan.Steps[action.Step];
            var more = action.Step + 1 < plan.Steps.Count;
            var live = squad.Where(w => port.Alive(w)).ToList();

            crew = new List<THandle>();
            placed = Array.Empty<HazardGround.WispPlacement>();

            // a new step: any beat of the last one is over (its burst already fired, or the step had none)
            AbandonBeat();

            if (formation.Points.Count == 0 || live.Count == 0)
            {
                port.Log($"cycle {action.Cycle} step {action.Step + 1}/{plan.Steps.Count} skipped: {(formation.Points.Count == 0 ? "no spots (" + formation.Detail + ")" : "no wisps")}");
                script.Glided(now, Array.Empty<double>(), more);
                return;
            }

            var positions = live.Select(h => { var p = port.PositionAt(h, now); return new Vector2(p.X, p.Y); }).ToList();
            var assign = WispRules.Assign(positions, formation.Points);

            var currents = new List<Vector3>();
            var desired = new List<Vector3>();

            for (var j = 0; j < formation.Points.Count; j++)
            {
                if (assign[j] < 0)
                    continue;

                var wisp = live[assign[j]];
                crew.Add(wisp);
                currents.Add(port.PositionAt(wisp, now));
                desired.Add(port.PointFor(formation.Points[j].X, formation.Points[j].Y));
            }

            // WISPS NEVER ENTER SOLIDS: stand at the last open point, fire at the true target.
            placed = WispRules.Place(currents, desired, port.HasCell, s.WispClear);

            var group = new List<(THandle Handle, Vector3 End)>(crew.Count);

            for (var i = 0; i < crew.Count; i++)
                group.Add((crew[i], placed[i].Stand));

            // ONE shared schedule for the whole formation: same start, same duration, same sub-leg count.
            port.MoveLegs(group, action.Seconds, new HazardMoveOptions { Follow = HazardFollow.HazardHeight });

            var cycleHold = s.ForCycle(action.Cycle).Hold;
            var offsets = WispRules.FireOffsets(s.Fire, crew.Count, cycleHold, s.SeqGap, port.Rng);
            var blocked = placed.Count(p => p.Blocked);

            // mark_dest: the glide is warning time too - each wisp's Pillar point is marked from now until its burst
            // (glide + hold + its fire offset; WispScript.Glided schedules the burst at exactly that)
            if (s.MarkDest)
            {
                for (var i = 0; i < crew.Count; i++)
                {
                    var until = action.Seconds + cycleHold + (i < offsets.Length ? offsets[i] : 0.0);

                    if (port.SpawnDestMark(placed[i].Pillar, until) == null)
                        port.Log("destination mark would not spawn; that wisp's move is unmarked");
                }
            }

            // ceiling_beam: a beam falls onto each Pillar point for the last ceiling_beam seconds before its burst
            // (the same burst time as the mark above and WispScript.Glided). Locals, not the mutable crew/placed fields.
            if (s.CeilingBeam > 0.0)
            {
                for (var i = 0; i < crew.Count; i++)
                {
                    var burstIn = action.Seconds + cycleHold + (i < offsets.Length ? offsets[i] : 0.0);
                    var wisp = crew[i];
                    var pillar = placed[i].Pillar;

                    ScheduleBeam(now + burstIn, () => !ended && port.Alive(wisp), pillar);
                }
            }

            port.Log(FormattableString.Invariant(
                $"cycle {action.Cycle} step {action.Step + 1}/{plan.Steps.Count} {formation.Pattern} {formation.Detail}: {crew.Count} wisp(s) of {live.Count}, {blocked} blocked by a solid, fire={s.Fire}, glide {action.Seconds:0.##}s"));

            // THE BEAT: the same timeline WispScript.Glided schedules the crew's bursts from (same now, glide, hold and
            // offsets), so the follower's burst is the step's first crew burst and its settle is the crew's hold start
            if (follower != null && crew.Count > 0)
            {
                var timeline = WispRules.Timeline(action.Seconds, cycleHold, offsets, s.Burst);

                beatHoldAt = now + timeline.Arrive;
                beatBurstAt = now + (timeline.BurstAt.Count > 0 ? timeline.BurstAt.Min() : timeline.Arrive + cycleHold);
                beatPending = true;
                beatLocked = false;
            }

            script.Glided(now, offsets, more);
        }

        /// <summary>
        /// Schedules a ceiling beam on <paramref name="pillar"/> that ends at the burst, <paramref name="burstIn"/> seconds
        /// from now: it starts ceiling_beam seconds before (or now, when the burst is nearer than that) and lasts until the
        /// burst. The step spawns only while <paramref name="stillWanted"/> holds. Returns the beam's handle through
        /// <paramref name="onSpawned"/> when given. A scheduled step (never the tick loop) starts it: HazardManager runs
        /// scheduled steps before Mechanic.Tick.
        /// </summary>
        private void ScheduleBeam(double burstAt, Func<bool> stillWanted, Vector3 pillar, Action<THandle> onSpawned = null)
        {
            if (!port.HasCeiling)
            {
                NoteBeamUnavailable();
                return;
            }

            var burstIn = burstAt - port.Now;
            var start = Math.Max(0.0, burstIn - s.CeilingBeam);

            if (!(burstIn - start > 0.0))
                return;

            port.Schedule(start, () =>
            {
                if (!stillWanted())
                    return;

                // a late tick must not stretch the beam past the burst: it lasts only until the absolute burst time
                var left = burstAt - port.Now;

                if (left < 0.05)
                    return;

                var beam = port.SpawnCeilingBeam(pillar, left);

                if (beam == null)
                {
                    NoteBeamUnavailable();
                    return;
                }

                onSpawned?.Invoke(beam);
            });
        }

        private void NoteBeamUnavailable()
        {
            if (beamNoteLogged)
                return;

            beamNoteLogged = true;
            port.Log("ceiling beam would not spawn (no ceiling?); wisps are unbeamed");
        }

        /// <summary>
        /// Brings the live squad to <paramref name="target"/>: dead wisps are dropped from it, extras are destroyed (the
        /// last ones), missing ones are spawned at WispFormations.SpawnPoints (a spot with no cell is retried, then
        /// skipped). A spawn that is refused leaves the squad short; the plan still has target points and the assignment
        /// leaves the surplus spots empty.
        /// </summary>
        private void ResizeSquad(int target)
        {
            squad.RemoveAll(w => !port.Alive(w));

            var before = squad.Count;

            while (squad.Count > target)
            {
                port.Destroy(squad[squad.Count - 1]);
                squad.RemoveAt(squad.Count - 1);
            }

            if (squad.Count < target)
            {
                var spots = WispFormations.SpawnPoints(region, target - squad.Count, s.EdgeInset, port.Rng,
                    p => port.HasCell(port.PointFor(p.X, p.Y)));

                foreach (var spot in spots)
                {
                    var handle = port.SpawnWisp(port.PointFor(spot.X, spot.Y));

                    if (handle != null)
                        squad.Add(handle);
                }
            }

            if (before != target || squad.Count != target)
                port.Log($"live count {target}: squad {before} -> {squad.Count}");
        }

        private void Burst(double now, int slot)
        {
            if (slot < 0 || slot >= crew.Count)
                return;

            var wisp = crew[slot];

            // one dead-handle policy: a lost wisp's burst is skipped, the run carries on
            if (!port.Alive(wisp))
            {
                port.Log("burst skipped: its wisp is gone");
                return;
            }

            Pillar(now, placed[slot], "slot");
        }

        /// <summary>
        /// THE ONE PILLAR PATH, for the crew's bursts and the follower's alike: a host at the placement's Pillar point (retried
        /// once at its Stand point), registered in the burn book so the one strike helper damages from it.
        /// </summary>
        private void Pillar(double now, HazardGround.WispPlacement where, string what)
        {
            var host = WispRules.SpawnPillar(p => port.SpawnPillar(p, s.Burst), where.Pillar, where.Stand, out var usedAt);

            if (host == null)
            {
                unseen++;
                port.Log(FormattableString.Invariant(
                    $"pillar host would not spawn at ({where.Pillar.X:0.00},{where.Pillar.Y:0.00},{where.Pillar.Z:0.00}) nor at the wisp's stand ({where.Stand.X:0.00},{where.Stand.Y:0.00},{where.Stand.Z:0.00}); no damage from this {what}"));
                return;
            }

            // The infinite hit window (WispSettings.HitWindow) means "one hit per ledger key per player", and the key is
            // THIS tick's volley (below). That is only one-hit-per-burst because every tick that fires gets a FRESH volley
            // key and every burst a FRESH host (above); if a key were ever reused across ticks the later burst would deal
            // no damage at all.
            // The pillar strikes from strike_delay after it appears, not in its spawn tick: a hit sent with the host's
            // CreateObject could kill before the client ever drew the pillar. The strike window still ends at burst.
            // The FIRST strike is a scheduled step, not a tick of the book: the host's destroy is a scheduled step too
            // (due at burst, later than this one), and steps run in due order before the mechanic ticks, so even a late
            // tick past the whole burst strikes once before the host goes. Left to the book alone it would strike zero times.
            // ONE HIT PER VOLLEY: every pillar that fires in this tick (a simultaneous crew burst, and the follower's burst
            // in the tick of the step's first crew burst) strikes under one shared ledger key, so a player in reach of two
            // of them, at once or one after the other while they burn, is hit once. Every pillar still shows.
            if (volley == 0 || volleyAt != now)
            {
                volleyAt = now;
                volley = port.NewHitGroup();
            }

            pillars++;
            var delay = s.StrikeDelaySeconds;
            var entry = book.Add(host, port.RestOf(host), now + delay, s.Burst - delay, volley);

            if (delay > 0.0)
                port.Schedule(delay, () => FirstStrike(entry));
        }

        private void FirstStrike(WispBurnBook.Entry entry)
        {
            if (ended || !book.StrikeFirst(entry, o => port.Alive((THandle)o)))
                return;

            hits += port.Strike((THandle)entry.Source, entry.Centre, s.HitRadius, s.HitWindow, entry.Volley);
        }

        /// <summary>
        /// The follower (follow=1), once per tick: record where the target is and issue the next 0.5 s leg towards where it
        /// was follow_lag seconds ago (WispFollowRules). It bursts ON THE CREW'S BEAT, never on a clock of its own: it
        /// arrives on <see cref="WispFollowRules.BeatPoint"/> by the crew's hold start (<see cref="LockBeat"/>) and sits
        /// still there (with a mark_dest ground mark for the hold), and its pillar fires in the tick of the step's first crew burst
        /// (<see cref="FollowerBeat"/>). Every target point goes through WispRules.Place (the same wisp rule as the crew):
        /// the follower flies to the Stand point and its pillar fires at the Pillar point. With nobody in the region it
        /// idles. A dead follower is dropped and the run carries on.
        /// </summary>
        private void TickFollower(double now)
        {
            if (follower == null)
                return;

            if (!port.Alive(follower))
            {
                port.Log("follower wisp is gone; dropped");

                // its beat will never burst: take down its mark and beam, and stop a scheduled beam spawning
                AbandonBeat();
                follower = null;
                return;
            }

            Vector3 target;
            uint targetId;

            if (s.FollowTarget == WispFollowTarget.Random)
            {
                if (!TryPickRandomTarget(out target, out targetId))
                {
                    ResetFollow();
                    return;
                }
            }
            else if (!port.TryFollowTarget(out target, out targetId))
            {
                // nobody in the region: forget the session, so the next sighting is a first sighting (a fresh telegraph)
                ResetFollow();
                return;
            }

            // a different player is now first in the region: start over for them
            if (!double.IsNaN(followStartedAt) && targetId != followTargetId)
                ResetFollow();

            if (double.IsNaN(followStartedAt))
            {
                followStartedAt = now;
                followTargetId = targetId;
            }

            trail.Record(now, new Vector2(target.X, target.Y));

            // the settle move: it starts one leg (plus a tick of slack) before the crew's hold start, so the follower
            // ARRIVES on its beat point when the hold starts, as the crew's glide does, and sits still for the whole hold.
            // Checked before the trailing leg, so the tick that settles issues only the settle move.
            if (beatPending && !beatLocked && now >= beatHoldAt - WispFollowRules.LegStep - port.TickSeconds - 1e-9)
                LockBeat(now);

            // the crew's hold has begun: the warning mark (and ceiling beam) go down for the hold, as before
            if (beatPending && beatLocked && !beatMarked && now >= beatHoldAt - 1e-9)
                MarkBeat(now);

            // settled on a beat: no trailing leg until its burst fires (NextLeg would chain one a tick early)
            var leg = beatPending && beatLocked
                ? null
                : WispFollowRules.NextLeg(trail, now, s.FollowLag, WispFollowRules.LegStep, legEndsAt, port.TickSeconds);

            if (leg.HasValue)
            {
                var current = port.PositionAt(follower, now);
                var desired = port.PointFor(leg.Value.End.X, leg.Value.End.Y);
                var placement = WispRules.Place(new[] { current }, new[] { desired }, port.HasCell, s.WispClear)[0];

                // never faster than FollowMaxSpeed: a long leg (the first one, or after a teleport) takes longer than a step.
                // The end point is NOT recomputed for the longer time; the next leg converges on the lagged position.
                var seconds = Math.Max(leg.Value.Seconds, Vector3.Distance(current, placement.Stand) / WispFollowRules.FollowMaxSpeed);

                if (port.MoveLeg(follower, placement.Stand, seconds))
                    legEndsAt = now + seconds;
            }
        }

        /// <summary>
        /// One leg (plus a tick) before the crew's hold start: fix where the follower bursts on this beat (where its target
        /// stood follow_lag before the burst, or the newest position known when that is still in the future), and fly
        /// there so it ARRIVES at the hold start, then sit still until the burst. The settle takes at least one
        /// <see cref="WispFollowRules.LegStep"/> and never outruns FollowMaxSpeed, so it arrives late only when the glide
        /// is shorter than a leg or the point is beyond the speed cap. <see cref="MarkBeat"/> marks it at the hold start.
        /// A target sighted under follow_lag before the burst gets no burst on this beat: a fresh sighting is warned first.
        /// </summary>
        private void LockBeat(double now)
        {
            beatLocked = true;
            beatMarked = false;

            if (follower == null || !port.Alive(follower))
            {
                beatPending = false;
                return;
            }

            var point = WispFollowRules.BeatPoint(trail, followStartedAt, beatBurstAt, s.FollowLag, now);

            if (point == null)
            {
                beatPending = false;
                return;
            }

            var current = port.PositionAt(follower, now);
            var desired = port.PointFor(point.Value.X, point.Value.Y);

            beatPlaced = WispRules.Place(new[] { current }, new[] { desired }, port.HasCell, s.WispClear)[0];

            // arrive at the hold start: the time left to it, but never under one leg nor faster than the speed cap
            var seconds = Math.Max(Math.Max(WispFollowRules.LegStep, beatHoldAt - now), Vector3.Distance(current, beatPlaced.Stand) / WispFollowRules.FollowMaxSpeed);

            // no trailing leg until the burst: the follower sits still for the hold, as the crew does
            if (port.MoveLeg(follower, beatPlaced.Stand, seconds))
                legEndsAt = Math.Max(now + seconds, beatBurstAt);

            // locked at or after the hold start (a glide shorter than a leg, or the burst's fallback): mark it now
            if (now >= beatHoldAt - 1e-9)
                MarkBeat(now);
        }

        /// <summary>
        /// The crew's hold start: the locked beat's mark_dest ground mark goes down for the rest of the hold, and its
        /// ceiling beam is scheduled against the burst (the same rule as the crew's).
        /// </summary>
        private void MarkBeat(double now)
        {
            beatMarked = true;

            if (!beatPending || follower == null)
                return;

            var remaining = beatBurstAt - now;

            if (s.MarkDest && remaining > 0.0)
            {
                beatMark = port.SpawnDestMark(beatPlaced.Pillar, remaining);

                if (beatMark == null)
                    port.Log("destination mark would not spawn; the follower's burst is unmarked");
            }

            // ceiling_beam: the same rule as the crew's, against this beat's burst; only while THIS beat is still pending
            var generation = ++beatGeneration;

            if (s.CeilingBeam > 0.0 && remaining > 0.0)
                ScheduleBeam(beatBurstAt, () => beatPending && generation == beatGeneration && follower != null && port.Alive(follower), beatPlaced.Pillar, beam => beatBeam = beam);
        }

        /// <summary>The step's first crew burst: the follower's pillar fires now, at the point locked at the hold start.</summary>
        private void FollowerBeat(double now)
        {
            if (!beatLocked)
                LockBeat(now);

            if (!beatPending)
                return;

            if (follower == null || !port.Alive(follower))
            {
                AbandonBeat();
                return;
            }

            beatPending = false;

            // the mark burns out on its own now: it stood until this burst
            beatMark = null;

            // and so does the ceiling beam
            beatBeam = null;

            Pillar(now, beatPlaced, "follower burst");

            // resume trailing on the next tick
            legEndsAt = now;
        }

        /// <summary>
        /// follow_target=random: the player to trail this tick. A fresh uniform draw (the run's rng) when a cycle has just begun,
        /// when there is no session, or when the current target has left the region; otherwise the current target. A draw clears the
        /// trail and restarts the session (followStartedAt is NaN until TickFollower stamps it), so the new target gets the full
        /// follow_lag telegraph. A new cycle's beat belongs to the new target and is kept; a target that left takes its beat with it.
        /// False when nobody is in the region. Consumes the rng ONLY when it draws, so follow_target=first runs never touch it.
        /// </summary>
        private bool TryPickRandomTarget(out Vector3 position, out uint targetId)
        {
            var players = port.FollowCandidates();
            position = default;
            targetId = 0;

            if (players == null || players.Count == 0)
                return false;

            var current = -1;

            if (!double.IsNaN(followStartedAt))
            {
                for (var i = 0; i < players.Count; i++)
                {
                    if (players[i].Id == followTargetId)
                    {
                        current = i;
                        break;
                    }
                }
            }

            if (current < 0 || repickTarget)
            {
                var fresh = port.Rng.Next(players.Count);

                // a target that left mid-cycle takes its beat with it; when this tick is also the cycle's first glide the pending
                // beat is the NEW cycle's (Glide already dropped the old one), so it stays and the new target is warned afresh
                if (!double.IsNaN(followStartedAt) && current < 0 && !repickTarget)
                    ResetFollow();
                else
                {
                    trail.Clear();
                    legEndsAt = double.NegativeInfinity;
                }

                followStartedAt = double.NaN;
                followTargetId = players[fresh].Id;
                current = fresh;
                repickTarget = false;
            }

            position = players[current].Position;
            targetId = players[current].Id;
            return true;
        }

        private void ResetFollow()
        {
            trail.Clear();
            followStartedAt = double.NaN;
            legEndsAt = double.NegativeInfinity;

            // a beat for the old sighting is dropped: the next sighting is warned afresh
            AbandonBeat();
        }

        /// <summary>Drops the pending beat. A beat already locked has a mark on the ground; it is removed, since no burst will come.</summary>
        private void AbandonBeat()
        {
            if (beatPending && beatMark != null)
                port.Destroy(beatMark);

            // the ceiling beam of the dropped beat: removed if it is up, and a still-scheduled one will not spawn
            beatGeneration++;

            if (beatBeam != null)
                port.Destroy(beatBeam);

            beatBeam = null;
            beatMark = null;
            beatPending = false;
        }

        public string Describe()
            => string.Format(CultureInfo.InvariantCulture, "cycle {0}/{1} {2} step {3} {4}; wisps {5}/{6}; pillars {7} hits {8}",
                script.Cycle, s.Cycles, CyclePattern, script.Step + 1, script.PhaseName, alive, squad.Count, pillars, hits);
    }
}
