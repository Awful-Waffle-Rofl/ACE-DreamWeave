using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The memory hazard's numbers, read from the merged args with a documented default and clamp for every key
    /// (shared invariant 5). Pure: <see cref="Read"/> takes only a <see cref="HazardArgs"/>.
    ///
    ///   len=4              colours in the first round, clamp 1..16. Round n shows len + n - 1 colours (a FRESH random sequence every round)
    ///   max_len=8          the round that ends the run Completed has this many colours, clamp 1..16 (len above it is refused by Validate)
    ///   beams_per_pulse=6  beam hosts fired per shown colour, clamp 1..16
    ///   beam_corners=0     1|0: the first beams of every pulse go one to each region CORNER (a Rect region's four, each edge_inset inside, as the zone pillars stand; a corner on a pad is skipped), the rest at random points; the count stays beams_per_pulse. A corner whose host has no cell falls back to a random point. Default 0
    ///   pad_pillar=0       1|0: every shown colour also raises ONE fire pillar (HazardDefaults.PillarScript) at the CENTRE of its pad, standing pulse_on seconds. It deals the standard hazard damage (pct, below) to every player within pad_radius plus the player's radius while it stands, one hit per player per pillar: an unmistakable cue for the answer pad. Default 0
    ///   beam_from=ceiling  "ceiling" (beams fire DOWN from just under the region's CeilingZ; Validate refuses it in a region with no ceiling) or "floor"
    ///   beam_flip=0        1|0: SWAP the host orientation the placement would choose. The beam mesh was measured to hang 5 m (z -5..0) BELOW its host origin, so a ceiling host is left unturned (beam falls) and a floor host (beam_from=floor, and the echo on a pad) is turned 180 degrees about x (beam rises); 1 inverts each of those. Default 0
    ///   pulse_on=1.0       seconds a shown colour's beams stand, clamp 0.2..10
    ///   pulse_off=0.5      seconds between two shown colours, clamp 0..10
    ///   answer_timeout=20  seconds the players have for the NEXT entry, clamp 3..120. It runs from the moment
    ///                      playback ends and restarts at every correct entry (a per-entry limit, not per-round).
    ///   pad_radius=1.0     radius of a pad circle in metres (2 m across), clamp 0.5..3
    ///   pad_gap=6          minimum distance between two pad CENTRES, clamp 2..40 (Validate refuses a gap under 2 x pad_radius + 0.5: touching pads)
    ///   pad_spacing=0.6    metres between ring markers on a pad's rim, clamp 0.2..3 (marker count is clamped 8..32)
    ///   layout=quadrant    "quadrant" (the four quadrant centres) or "random" (four random placeable points)
    ///   edge_inset         the framework key (HazardContext.EdgeInset): a pad's rim is kept this far inside the region edge
    ///
    ///   pct                the framework key (HazardContext.Pct, default 0.60): damage per pad_pillar hit as a fraction of the player's MaxHealth. Read by the context, not here; listed in <see cref="NumberKeys"/> so a non-numeric one is refused. Without pad_pillar the game deals no damage.
    /// </summary>
    public sealed class MemorySettings
    {
        public const int DefaultLen = 4;
        public const int MinLen = 1;
        public const int MaxLenClamp = 16;

        public const int DefaultMaxLen = 8;

        public const int DefaultBeamsPerPulse = 6;
        public const int MinBeamsPerPulse = 1;
        public const int MaxBeamsPerPulse = 16;

        public const double DefaultPulseOn = 1.0;
        public const double MinPulseOn = 0.2;
        public const double MaxPulseOn = 10.0;

        public const double DefaultPulseOff = 0.5;
        public const double MinPulseOff = 0.0;
        public const double MaxPulseOff = 10.0;

        public const double DefaultAnswerTimeout = 20.0;
        public const double MinAnswerTimeout = 3.0;
        public const double MaxAnswerTimeout = 120.0;

        public const double DefaultPadRadius = 1.0;
        public const double MinPadRadius = 0.5;
        public const double MaxPadRadius = 3.0;

        public const double DefaultPadGap = 6.0;
        public const double MinPadGap = 2.0;
        public const double MaxPadGap = 40.0;

        public const double DefaultPadSpacing = 0.6;
        public const double MinPadSpacing = 0.2;
        public const double MaxPadSpacing = 3.0;

        /// <summary>A pad_gap must exceed 2 x pad_radius by at least this much so two pads never touch.</summary>
        public const double PadGapMargin = 0.5;

        public const string BeamFromCeiling = "ceiling";
        public const string BeamFromFloor = "floor";

        public const bool DefaultBeamFlip = false;

        public const bool DefaultBeamCorners = false;

        public const bool DefaultPadPillar = false;

        public const string LayoutQuadrant = "quadrant";
        public const string LayoutRandom = "random";

        /// <summary>Seconds between a round being announced and its first colour.</summary>
        public const double RoundPause = 1.5;

        public int Len { get; private set; }
        public int MaxLen { get; private set; }
        public int BeamsPerPulse { get; private set; }
        public double PulseOn { get; private set; }
        public double PulseOff { get; private set; }
        public double AnswerTimeout { get; private set; }
        public float PadRadius { get; private set; }
        public float PadGap { get; private set; }
        public float PadSpacing { get; private set; }
        public bool RandomLayout { get; private set; }

        /// <summary>True: playback beams hang from the ceiling (the default); false: they stand on the floor as before.</summary>
        public bool BeamsFromCeiling { get; private set; }

        /// <summary>The owner's swap: invert the placement's own orientation choice (MemoryLayout.BeamTurned). Default off.</summary>
        public bool BeamFlip { get; private set; }

        /// <summary>beam_corners: every pulse puts one beam in each region corner before the random ones (MemoryLayout.CornerBeamPoints). Default off.</summary>
        public bool BeamCorners { get; private set; }

        /// <summary>pad_pillar: a damaging fire pillar on the centre of the pad of each shown colour. Default off.</summary>
        public bool PadPillar { get; private set; }

        /// <summary>Every numeric arg the memory game reads; a given one that does not parse is REFUSED by Validate.</summary>
        public static readonly string[] NumberKeys =
        {
            "pulse_on", "pulse_off", "answer_timeout", "pad_radius", "pad_gap", "pad_spacing", HazardDefaults.EdgeInsetKey, HazardDefaults.PctKey,
        };

        public static readonly string[] WholeNumberKeys = { "len", "max_len", "beams_per_pulse" };

        public static MemorySettings Read(HazardArgs args)
        {
            args ??= HazardArgs.Empty;

            return new MemorySettings
            {
                Len = args.GetInt("len", DefaultLen, MinLen, MaxLenClamp),
                MaxLen = args.GetInt("max_len", DefaultMaxLen, MinLen, MaxLenClamp),
                BeamsPerPulse = args.GetInt("beams_per_pulse", DefaultBeamsPerPulse, MinBeamsPerPulse, MaxBeamsPerPulse),
                PulseOn = args.GetDouble("pulse_on", DefaultPulseOn, MinPulseOn, MaxPulseOn),
                PulseOff = args.GetDouble("pulse_off", DefaultPulseOff, MinPulseOff, MaxPulseOff),
                AnswerTimeout = args.GetDouble("answer_timeout", DefaultAnswerTimeout, MinAnswerTimeout, MaxAnswerTimeout),
                PadRadius = (float)args.GetDouble("pad_radius", DefaultPadRadius, MinPadRadius, MaxPadRadius),
                PadGap = (float)args.GetDouble("pad_gap", DefaultPadGap, MinPadGap, MaxPadGap),
                PadSpacing = (float)args.GetDouble("pad_spacing", DefaultPadSpacing, MinPadSpacing, MaxPadSpacing),
                RandomLayout = args.GetChoice("layout", LayoutQuadrant, LayoutQuadrant, LayoutRandom) == LayoutRandom,
                BeamsFromCeiling = args.GetChoice("beam_from", BeamFromCeiling, BeamFromCeiling, BeamFromFloor) == BeamFromCeiling,
                BeamFlip = args.GetBool("beam_flip", DefaultBeamFlip),
                BeamCorners = args.GetBool("beam_corners", DefaultBeamCorners),
                PadPillar = args.GetBool("pad_pillar", DefaultPadPillar),
            };
        }

        /// <summary>The refusal for beam_from / beam_flip / beam_corners / pad_pillar that is not one of its words (pure; null when fine). A typo is never silently defaulted.</summary>
        public static string CheckBeamArgs(HazardArgs args)
        {
            args ??= HazardArgs.Empty;

            var from = args.GetString("beam_from");

            if (from != null && !string.Equals(from, BeamFromCeiling, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(from, BeamFromFloor, StringComparison.OrdinalIgnoreCase))
                return $"beam_from='{from}' is not one of {BeamFromCeiling}|{BeamFromFloor}";

            var flip = args.GetString("beam_flip");

            if (flip != null && args.GetBool("beam_flip", true) != args.GetBool("beam_flip", false))
                return $"beam_flip='{flip}' is not one of 1|0";

            var corners = args.GetString("beam_corners");

            if (corners != null && args.GetBool("beam_corners", true) != args.GetBool("beam_corners", false))
                return $"beam_corners='{corners}' is not one of 1|0";

            var pillar = args.GetString("pad_pillar");

            if (pillar != null && args.GetBool("pad_pillar", true) != args.GetBool("pad_pillar", false))
                return $"pad_pillar='{pillar}' is not one of 1|0";

            return null;
        }

        /// <summary>The refusal for beam_corners=1 in a region with no corners (only a Rect has them; pure; null when fine).</summary>
        public string CheckCorners(HazardRegion region)
            => BeamCorners && region != null && region.Shape != HazardShape.Rect
                ? $"beam_corners=1 needs a rectangular region (this one is {region.Shape}); pass beam_corners=0"
                : null;

        /// <summary>The refusal for beam_from=ceiling in a region that has no ceiling (pure; null when fine).</summary>
        public string CheckCeiling(HazardRegion region)
            => BeamsFromCeiling && region != null && !region.CeilingZ.HasValue
                ? "beam_from=ceiling needs a region with a ceiling (this one has none); pass beam_from=floor"
                : null;

        /// <summary>The refusal for numbers that leave no game (pure; null when fine).</summary>
        public string Check()
        {
            if (Len > MaxLen)
                return $"len={Len} is longer than max_len={MaxLen}: the game would be over before it starts";

            if (PadGap < 2.0 * PadRadius + PadGapMargin)
                return $"pad_gap={PadGap:0.##} is too small for pad_radius={PadRadius:0.##}: pads would touch (need at least {2.0 * PadRadius + PadGapMargin:0.##})";

            return null;
        }
    }

    public enum MemoryPhase
    {
        /// <summary>The colours are being shown. Touches are ignored.</summary>
        Playback,

        /// <summary>The players may enter the sequence.</summary>
        Answer,

        /// <summary>Finished (won or lost). Touches are ignored.</summary>
        Done,
    }

    public enum MemoryPlayback
    {
        None,

        /// <summary>Fire the beams of the returned colour now.</summary>
        Fire,

        /// <summary>The last colour has been shown; the answer window opened.</summary>
        Finished,
    }

    public enum MemoryTouch
    {
        /// <summary>Not in the answer phase: nothing happened.</summary>
        Ignored,

        /// <summary>A right pad; the sequence goes on.</summary>
        Correct,

        /// <summary>A right pad that finished the round; grow the sequence and play on.</summary>
        RoundWon,

        /// <summary>A right pad that finished the max_len round: the run is won.</summary>
        Won,

        /// <summary>A wrong pad: the run is lost.</summary>
        Wrong,
    }

    /// <summary>
    /// The memory game as a pure state machine over run-relative time (shared invariant 6): no Player, no
    /// HazardContext, no clock of its own. ONE sequence covers the whole room: any player's correct entry advances
    /// it, any player's wrong entry loses it for everyone. The mechanic feeds it playback polls and touches.
    /// </summary>
    public sealed class MemoryGame
    {
        // replaced WHOLE each round (never mutated in place): Describe/Reveal on the command thread read it unsynchronised
        private volatile List<int> sequence;
        private readonly int maxLen;
        private readonly double pulseOn;
        private readonly double pulseOff;
        private readonly double roundPause;
        private readonly double answerTimeout;

        private int nextStep;
        private double nextFireAt;
        private double finishAt;
        private double deadline;

        public MemoryGame(IReadOnlyList<int> initial, int maxLen, double pulseOn, double pulseOff, double roundPause, double answerTimeout)
        {
            if (initial == null || initial.Count == 0)
                throw new ArgumentException("the sequence needs at least one colour", nameof(initial));

            sequence = new List<int>(initial);
            this.maxLen = Math.Max(maxLen, initial.Count);
            this.pulseOn = pulseOn;
            this.pulseOff = pulseOff;
            this.roundPause = roundPause;
            this.answerTimeout = answerTimeout;
            Round = 1;
            Phase = MemoryPhase.Done; // until BeginRound
        }

        public MemoryPhase Phase { get; private set; }

        /// <summary>1-based round number.</summary>
        public int Round { get; private set; }

        /// <summary>Correct entries so far this round.</summary>
        public int Position { get; private set; }

        public int Length => sequence.Count;

        public int MaxLen => maxLen;

        /// <summary>The run-relative time the current answer window closes (meaningful in the Answer phase).</summary>
        public double Deadline => deadline;

        public int ColourAt(int index) => sequence[index];

        public IReadOnlyList<int> Sequence => sequence;

        /// <summary>Starts (or restarts) playback of the whole sequence: the first colour fires roundPause after <paramref name="now"/>.</summary>
        public void BeginRound(double now)
        {
            Phase = MemoryPhase.Playback;
            Position = 0;
            nextStep = 0;
            nextFireAt = now + roundPause;
            finishAt = double.PositiveInfinity;
        }

        /// <summary>
        /// Advances playback to <paramref name="now"/>: at most ONE event per call, so a large tick_ms shows the
        /// colours one tick apart instead of bunching them. The next colour is due at max(ideal, now + on + off) and
        /// the answer window opens pulse_on after the LAST colour actually fired.
        /// </summary>
        public MemoryPlayback Poll(double now, out int colour)
        {
            colour = -1;

            if (Phase != MemoryPhase.Playback)
                return MemoryPlayback.None;

            if (nextStep < sequence.Count)
            {
                if (now < nextFireAt)
                    return MemoryPlayback.None;

                colour = sequence[nextStep];
                nextStep++;

                // Keyed off the ACTUAL fire time: a laggy tick pushes the schedule back instead of bunching colours.
                var gap = pulseOn + pulseOff;
                nextFireAt = Math.Max(nextFireAt + gap, now + gap);

                if (nextStep == sequence.Count)
                    finishAt = now + pulseOn;

                return MemoryPlayback.Fire;
            }

            if (now < finishAt)
                return MemoryPlayback.None;

            Phase = MemoryPhase.Answer;
            deadline = now + answerTimeout;
            return MemoryPlayback.Finished;
        }

        /// <summary>A pad was entered (an edge, see <see cref="MemoryTouchTracker"/>). Ignored outside the answer phase.</summary>
        public MemoryTouch Touch(int pad, double now)
        {
            if (Phase != MemoryPhase.Answer)
                return MemoryTouch.Ignored;

            if (pad != sequence[Position])
            {
                Phase = MemoryPhase.Done;
                return MemoryTouch.Wrong;
            }

            Position++;
            deadline = now + answerTimeout; // each correct entry restarts the per-entry window

            if (Position < sequence.Count)
                return MemoryTouch.Correct;

            if (sequence.Count >= maxLen)
            {
                Phase = MemoryPhase.Done;
                return MemoryTouch.Won;
            }

            return MemoryTouch.RoundWon;
        }

        /// <summary>True when the answer window has run out. Strictly after the deadline.</summary>
        public bool TimedOut(double now) => Phase == MemoryPhase.Answer && now > deadline;

        /// <summary>Ends the game as lost (a timeout or a stop).</summary>
        public void Fail() => Phase = MemoryPhase.Done;

        /// <summary>
        /// After <see cref="MemoryTouch.RoundWon"/>: replaces the sequence with a FRESH ctx.Rng one (no colour follows itself,
        /// nothing kept from the last round) one colour longer than the last, never past max_len, and plays it.
        /// </summary>
        public void NextRound(Random rng, double now)
        {
            sequence = MemoryRules.NextRoundSequence(rng, sequence.Count, maxLen);
            Round++;
            BeginRound(now);
        }
    }

    /// <summary>
    /// Edge-triggered pad touches. A player's touch is the tick they ENTER a pad circle (2D distance to the centre
    /// within the radius); standing in it, or still being in it after a round changes, never counts again - the
    /// player has to leave and re-enter. State is kept for every player seen EVERY tick (including during
    /// playback, when the touches themselves are discarded), so someone standing on a pad when the answer window
    /// opens does not count as having touched it. A player missing from a call (left the region) is forgotten.
    ///
    /// SEGMENT TOUCH: with a large tick_ms a runner covers several metres per tick, so the test is the segment from
    /// the player's previous position to the current one against each pad circle (closest-point distance), not just
    /// the end point. A pad under the end point wins; otherwise the pad the segment reaches first. A segment longer
    /// than <see cref="MaxSegmentMetres"/> is a teleport, and only its end point counts. Moving straight from one
    /// pad onto another without leaving is an entry to the second.
    /// </summary>
    public sealed class MemoryTouchTracker
    {
        /// <summary>Longer than this in one tick is treated as a teleport: no pad is "walked through".</summary>
        public const float MaxSegmentMetres = 25f;

        private readonly Dictionary<uint, int> inside = new Dictionary<uint, int>();
        private readonly Dictionary<uint, Vector2> last = new Dictionary<uint, Vector2>();

        /// <summary>The pad (nearest, within the radius) a point is on, or -1.</summary>
        public static int PadAt(Vector2 position, IReadOnlyList<Vector2> pads, float radius)
        {
            var best = -1;
            var bestSq = float.MaxValue;
            var limit = radius * radius;

            for (var i = 0; i < pads.Count; i++)
            {
                var sq = Vector2.DistanceSquared(position, pads[i]);

                if (sq <= limit && sq < bestSq)
                {
                    best = i;
                    bestSq = sq;
                }
            }

            return best;
        }

        /// <summary>Squared distance from <paramref name="p"/> to the segment a-b; <paramref name="t"/> is the closest point's fraction along it.</summary>
        public static float SegmentDistanceSquared(Vector2 a, Vector2 b, Vector2 p, out float t)
        {
            var ab = b - a;
            var len2 = ab.LengthSquared();

            t = len2 > 0f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
            return Vector2.DistanceSquared(p, a + ab * t);
        }

        /// <summary>The pad this tick's movement touched, or -1 (see the class remarks for the rules).</summary>
        public static int PadTouched(Vector2 previous, Vector2 position, IReadOnlyList<Vector2> pads, float radius)
        {
            var end = PadAt(position, pads, radius);

            if (end >= 0 || Vector2.Distance(previous, position) > MaxSegmentMetres)
                return end;

            var best = -1;
            var bestT = float.MaxValue;
            var limit = radius * radius;

            for (var i = 0; i < pads.Count; i++)
            {
                if (SegmentDistanceSquared(previous, position, pads[i], out var t) <= limit && t < bestT)
                {
                    best = i;
                    bestT = t;
                }
            }

            return best;
        }

        /// <summary>Feeds this tick's players; returns the (player, pad) entries, in player order.</summary>
        public List<(uint Player, int Pad)> Update(IReadOnlyList<(uint Id, Vector2 Position)> players, IReadOnlyList<Vector2> pads, float radius)
        {
            var entered = new List<(uint, int)>();
            var seen = new HashSet<uint>();

            for (var i = 0; i < players.Count; i++)
            {
                var (id, position) = players[i];

                seen.Add(id);

                if (!inside.TryGetValue(id, out var was))
                    was = -1;

                var previous = last.TryGetValue(id, out var prev) ? prev : position;
                var touched = PadTouched(previous, position, pads, radius);

                if (touched >= 0 && touched != was)
                    entered.Add((id, touched));

                inside[id] = PadAt(position, pads, radius);
                last[id] = position;
            }

            if (inside.Count != seen.Count)
            {
                var gone = new List<uint>();

                foreach (var id in inside.Keys)
                    if (!seen.Contains(id))
                        gone.Add(id);

                foreach (var id in gone)
                {
                    inside.Remove(id);
                    last.Remove(id);
                }
            }

            return entered;
        }
    }

    public enum MemoryEventKind
    {
        /// <summary>Show <see cref="MemoryEvent.Colour"/>: fire its beams now.</summary>
        Fire,

        /// <summary>Playback ended; the answer window opened.</summary>
        YourTurn,

        /// <summary>A right pad. Player, Pad, Step (entries so far this round) and Length are set.</summary>
        Correct,

        /// <summary>The round was answered in full (follows its last Correct). Round is the round just won.</summary>
        RoundWon,

        /// <summary>The next round began (follows RoundWon): Round and Length are the new ones.</summary>
        NextRound,

        /// <summary>The max_len round was answered: the run is won (follows its last Correct).</summary>
        Won,

        /// <summary>A wrong pad. Player, Pad, Expected (the colour wanted), Step (0-based index wanted) and Length are set.</summary>
        Wrong,

        /// <summary>No correct entry in time. Step (0-based index still wanted) and Length are set.</summary>
        TimedOut,
    }

    public readonly struct MemoryEvent
    {
        public MemoryEvent(MemoryEventKind kind, int colour = -1, uint player = 0, int pad = -1, int expected = -1, int step = 0, int length = 0, int round = 0)
        {
            Kind = kind;
            Colour = colour;
            Player = player;
            Pad = pad;
            Expected = expected;
            Step = step;
            Length = length;
            Round = round;
        }

        public MemoryEventKind Kind { get; }
        public int Colour { get; }
        public uint Player { get; }
        public int Pad { get; }
        public int Expected { get; }
        public int Step { get; }
        public int Length { get; }
        public int Round { get; }
    }

    /// <summary>
    /// One tick of the memory game as a pure function of (time, where the players stand): the touch tracker and the
    /// game state machine together, returning WHAT HAPPENED for the mechanic to announce. Touch state is updated
    /// every tick, playback included, and the touches of a playback tick are discarded by the game
    /// (<see cref="MemoryTouch.Ignored"/>). Entries are processed in player order; the first wrong one fails the
    /// room and stops the tick; the entries after a round-ending one belong to the old round and are dropped.
    /// </summary>
    public sealed class MemorySession
    {
        private readonly IReadOnlyList<Vector2> pads;
        private readonly float padRadius;
        private readonly Random rng;
        private readonly MemoryTouchTracker tracker = new MemoryTouchTracker();

        public MemorySession(MemoryGame game, IReadOnlyList<Vector2> pads, float padRadius, Random rng)
        {
            Game = game;
            this.pads = pads;
            this.padRadius = padRadius;
            this.rng = rng;
        }

        public MemoryGame Game { get; }

        public List<MemoryEvent> Step(double now, IReadOnlyList<(uint Id, Vector2 Position)> players)
        {
            var events = new List<MemoryEvent>();
            var entered = tracker.Update(players, pads, padRadius);

            // Same-tick entries are judged in player-id order, so the outcome never depends on the caller's list order.
            entered.Sort((a, b) => a.Player.CompareTo(b.Player));

            if (Game.Phase == MemoryPhase.Done)
                return events;

            // A touch made during playback is discarded even on the tick that ends playback.
            var wasPlayback = Game.Phase == MemoryPhase.Playback;

            if (wasPlayback)
            {
                var step = Game.Poll(now, out var colour);

                if (step == MemoryPlayback.Fire)
                    events.Add(new MemoryEvent(MemoryEventKind.Fire, colour: colour));
                else if (step == MemoryPlayback.Finished)
                    events.Add(new MemoryEvent(MemoryEventKind.YourTurn));

                entered.Clear();
            }

            var advanced = false;

            if (Game.Phase == MemoryPhase.Answer && entered.Count > 0)
            {
                // Every entry of this tick is judged against the SAME expectation: any wrong one fails the room
                // (the lowest player id is named), whatever the order; otherwise they all chose the wanted pad and
                // count as one entry.
                var index = Game.Position;
                var expected = Game.ColourAt(index);

                foreach (var (wrongPlayer, wrongPad) in entered)
                {
                    if (wrongPad == expected)
                        continue;

                    Game.Touch(wrongPad, now);
                    events.Add(new MemoryEvent(MemoryEventKind.Wrong, player: wrongPlayer, pad: wrongPad, expected: expected, step: index, length: Game.Length));
                    return events;
                }

                var (player, pad) = entered[0];
                var result = Game.Touch(pad, now);

                advanced = true;
                events.Add(new MemoryEvent(MemoryEventKind.Correct, player: player, pad: pad, step: Game.Position, length: Game.Length));

                if (result == MemoryTouch.Won)
                {
                    events.Add(new MemoryEvent(MemoryEventKind.Won, player: player, pad: pad, length: Game.Length, round: Game.Round));
                    return events;
                }

                if (result == MemoryTouch.RoundWon)
                {
                    events.Add(new MemoryEvent(MemoryEventKind.RoundWon, round: Game.Round, length: Game.Length));
                    Game.NextRound(rng, now);
                    events.Add(new MemoryEvent(MemoryEventKind.NextRound, round: Game.Round, length: Game.Length));
                    return events;
                }
            }

            // An entry on a tick that is past the deadline still counts (the tick is the resolution): time out only
            // when nothing advanced the window this tick.
            if (!advanced && Game.TimedOut(now))
            {
                events.Add(new MemoryEvent(MemoryEventKind.TimedOut, step: Game.Position, length: Game.Length));
                Game.Fail();
            }

            return events;
        }
    }

    /// <summary>Colour vocabulary and sequence rules of the memory game. Pure; Random is always passed in.</summary>
    public static class MemoryRules
    {
        public const int PadCount = 4;

        public static readonly IReadOnlyList<string> ColourNames = new[] { "red", "blue", "green", "purple" };

        /// <summary>The Hill Marker weenie of each pad colour (red, blue, green, purple).</summary>
        public static readonly IReadOnlyList<uint> PadWcids = new[]
        {
            HazardDefaults.MemoryPadRedWcid,
            HazardDefaults.MemoryPadBlueWcid,
            HazardDefaults.MemoryPadGreenWcid,
            HazardDefaults.MemoryPadPurpleWcid,
        };

        /// <summary>The beam VisualEffectScript of each colour (HazardDefaults.MemoryBeam*Script).</summary>
        public static readonly IReadOnlyList<uint> BeamScripts = new[]
        {
            HazardDefaults.MemoryBeamRedScript,
            HazardDefaults.MemoryBeamBlueScript,
            HazardDefaults.MemoryBeamGreenScript,
            HazardDefaults.MemoryBeamPurpleScript,
        };

        public static string ColourName(int colour) => colour >= 0 && colour < ColourNames.Count ? ColourNames[colour] : "?";

        public static int RandomColour(Random rng) => rng.Next(PadCount);

        /// <summary>
        /// A fresh sequence of <paramref name="length"/> colours. A colour never follows itself (owner ruling 2026-10-18:
        /// two pulses of one colour in a row read as one), but may come back later ("red blue red").
        /// </summary>
        public static List<int> NewSequence(Random rng, int length)
        {
            var list = new List<int>(Math.Max(0, length));

            for (var i = 0; i < length; i++)
            {
                if (i == 0)
                {
                    list.Add(RandomColour(rng));
                    continue;
                }

                // one draw from the other PadCount - 1 colours: skip over the previous one
                var c = rng.Next(PadCount - 1);
                list.Add(c >= list[i - 1] ? c + 1 : c);
            }

            return list;
        }

        /// <summary>
        /// The sequence of the round AFTER one that showed <paramref name="previousLength"/> colours: one colour longer,
        /// capped at <paramref name="maxLen"/>, drawn fresh from <paramref name="rng"/> (nothing is carried over).
        /// </summary>
        public static List<int> NextRoundSequence(Random rng, int previousLength, int maxLen)
            => NewSequence(rng, Math.Min(Math.Max(1, previousLength + 1), Math.Max(1, maxLen)));

        /// <summary>"red blue blue green" - the /hazard reveal text.</summary>
        public static string Format(IReadOnlyList<int> sequence)
        {
            var sb = new StringBuilder();

            for (var i = 0; i < sequence.Count; i++)
            {
                if (i > 0)
                    sb.Append(' ');

                sb.Append(ColourName(sequence[i]));
            }

            return sb.ToString();
        }
    }

    /// <summary>Pure decisions about the pad markers.</summary>
    public static class MemoryMarkers
    {
        /// <summary>
        /// Respawn a marker only when it WAS placed and has since died (a /reload-landblock). One that was never
        /// placed (refused at Start) is not retried every second forever.
        /// </summary>
        public static bool NeedsRespawn(bool placed, bool alive) => placed && !alive;

        /// <summary>The fewest markers a pad ring may end up with before the pad counts as invisible: max(3, ceil(count / 2)).</summary>
        public static int MinimumPlaced(int ringCount) => Math.Max(3, (ringCount + 1) / 2);

        /// <summary>The planar (x, y) of a landblock-local position. x stays x, y stays y.</summary>
        public static Vector2 Planar(Vector3 position) => new Vector2(position.X, position.Y);
    }
}
