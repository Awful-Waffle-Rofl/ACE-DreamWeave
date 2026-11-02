using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The colour memory game: four coloured pads on the floor, each a ring of small glowing Hill Marker
    /// wisps (wcids 1006800-1006803, red/blue/green/purple). The room is shown a sequence as coloured beams fired DOWN
    /// from the ceiling (<c>beam_from=ceiling</c>, the default; <c>floor</c> stands them on the ground instead) at
    /// random points of the region (never above a pad); players answer by WALKING INTO the pad of each colour. One
    /// sequence covers the whole room: any player's correct entry advances it, any player's wrong entry fails it for
    /// everyone. Apart from the optional pad pillars below the game deals no damage - a boss script decides the punishment
    /// from the outcome.
    ///
    /// ROUNDS. The first round shows <c>len</c> colours; every won round deals a brand NEW random sequence (a colour never
    /// follows itself but may return later, nothing kept from the round before - not a Simon growth) one colour longer than the last, up to
    /// <c>max_len</c>; winning the <c>max_len</c> round ends the run Completed. <c>/hazard reveal</c> shows the CURRENT round's sequence.
    ///
    /// CEILING BEAMS. The beam host hangs MemoryLayout.CeilingBeamOffset under the region's CeilingZ (its origin must
    /// resolve to a cell: checked with ctx.HasCell(raise: false) and spawned without the 1.5 m raise, so the check and
    /// the spawn agree and a host is never walked up through the ceiling). MEASURED: the beam mesh hangs 5 m (z -5..0
    /// at scale 1, up to 8 m at the 1.6 final scale) below its host origin, so a ceiling host stays UNTURNED and the
    /// beam falls from the ceiling. A host on the FLOOR (<c>beam_from=floor</c>, and the echo beam on a touched pad,
    /// which stays on the pad's floor point) is turned 180 degrees about x so its beam rises 0..+5 m from the floor
    /// instead of sinking under it. <c>beam_flip=1</c> is the owner's in-game SWAP: it inverts whichever of those
    /// the placement would choose (default 0).
    ///
    /// PAD PILLARS. <c>pad_pillar=1</c> also raises ONE fire pillar (HazardDefaults.PillarScript, as the wisps raise theirs) on
    /// the centre of the pad of the shown colour, standing <c>pulse_on</c> seconds: an unmistakable cue for the answer pad. It
    /// deals the STANDARD hazard damage, through the same call the wisp pillars use (ctx.HitPlayers with the pillar as the damage
    /// source, an infinite window so a player is hit once per pillar, element ctx.DamageType, pct from the args, default 0.60) to
    /// every player within <c>pad_radius</c> plus the player's radius of the pad centre while the pillar stands. The pillars are struck once per tick,
    /// never after the run has ended. They are not counted as playback beams and a touch during playback is ignored anyway.
    ///
    /// TOUCH is edge-triggered (MemoryTouchTracker): only entering a pad circle counts, so a player standing on the
    /// pad a new round opens with must step out and back in. Touches during playback are ignored.
    ///
    /// FAILURE (the players' fault) ends the run <see cref="HazardOutcome.Failed"/> with a one-line reason: a wrong pad, or no correct
    /// entry within <c>answer_timeout</c> seconds. The window opens when playback ends and restarts at every correct
    /// entry.
    ///
    /// Works on any region (Rect, Circle, ad-hoc, Outdoor): the pads, the beams and the validation use only
    /// ctx.Region. Pads default to the four quadrant centres; <c>layout=random</c> picks four random placeable
    /// points. Numbers: see <see cref="MemorySettings"/>.
    ///
    /// All world access goes through <see cref="IMemoryWorld"/> (the live wrapper over HazardContext), so the
    /// decisions below are tested without a landblock.
    /// </summary>
    public sealed class MemoryMechanic : IHazardMechanic
    {
        /// <summary>How long a correct entry's echo beam stands on its pad.</summary>
        public const double EchoSeconds = 0.6;

        /// <summary>Markers are re-checked (and any lost one respawned) at most this often.</summary>
        private const double RepairEverySeconds = 1.0;

        private sealed class Marker
        {
            public int Pad;
            public Vector3 Position;
            public IMemoryPiece Piece;

            /// <summary>True once a spawn of this marker succeeded; only a placed marker that died is respawned.</summary>
            public bool Placed;
        }

        /// <summary>A pad pillar that stands: its piece, where it stands (the pad centre on the ground) and the run time it ends.</summary>
        private sealed class Pillar
        {
            public IMemoryPiece Piece;
            public Vector3 Centre;
            public double Expiry;
        }

        private readonly List<Marker> markers = new List<Marker>();
        private readonly List<Pillar> pillars = new List<Pillar>();

        private IMemoryWorld world;
        private MemorySettings settings;
        private MemorySession session;
        private IReadOnlyList<Vector2> pads = Array.Empty<Vector2>();
        private double lastRepair;
        private bool noBeamRoomLogged;

        /// <summary>True once a refused pad pillar was logged, so a run logs it once (mirrors noBeamRoomLogged).</summary>
        private bool padPillarRefusedLogged;
        private bool noCeilingLogged;
        private int revealRound = 1;

        // Describe/Reveal may run on the command thread: they read only these simple, replaced-whole fields.
        private volatile string reveal;
        private volatile string status = "not started";
        private volatile string endedText;

        /// <summary>Set the moment this mechanic ends the run itself (Fail or Abort), so the repair pass stops and Describe stops showing a live game.</summary>
        private volatile bool ended;

        public string Kind => HazardMechanicRegistry.Memory;

        public IReadOnlyList<string> Patterns => Array.Empty<string>();

        public IEnumerable<uint> RequiredWcids(HazardArgs args)
        {
            foreach (var wcid in MemoryRules.PadWcids)
                yield return wcid;

            // the beams and the echo are effect hosts (HazardContext.SpawnTimedEffect)
            yield return HazardDefaults.EffectHostWcid;
        }

        public string Validate(HazardRegion region, HazardArgs args, string pattern)
        {
            if (region == null)
                return "no region";

            args ??= HazardArgs.Empty;

            // a typo is refused, never silently defaulted (the same refusal the balls and the wisps give)
            var notNumeric = args.FirstNonNumeric(MemorySettings.NumberKeys, MemorySettings.WholeNumberKeys);

            if (notNumeric != null)
                return notNumeric;

            var layout = args.GetString("layout");

            if (layout != null && !string.Equals(layout, MemorySettings.LayoutQuadrant, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(layout, MemorySettings.LayoutRandom, StringComparison.OrdinalIgnoreCase))
                return $"layout='{layout}' is not one of {MemorySettings.LayoutQuadrant}|{MemorySettings.LayoutRandom}";

            var beamArgs = MemorySettings.CheckBeamArgs(args);

            if (beamArgs != null)
                return beamArgs;

            var s = MemorySettings.Read(args);

            var numbers = s.Check();

            if (numbers != null)
                return numbers;

            var ceiling = s.CheckCeiling(region);

            if (ceiling != null)
                return ceiling;

            var corners = s.CheckCorners(region);

            if (corners != null)
                return corners;

            var inset = (float)args.GetDouble(HazardDefaults.EdgeInsetKey, HazardDefaults.EdgeInset, HazardDefaults.MinEdgeInset, HazardDefaults.MaxEdgeInset);

            return MemoryLayout.Feasible(region, s.RandomLayout, s.PadRadius, s.PadGap, inset);
        }

        public void Start(HazardContext ctx) => StartCore(world = new HazardMemoryWorld(ctx));

        public void Tick(HazardContext ctx)
        {
            if (world != null)
                TickCore(world);
        }

        public void OnStop(HazardContext ctx, HazardOutcome outcome)
        {
            if (world != null)
                StopCore(world, outcome);
        }

        public string Describe(HazardContext ctx)
        {
            if (endedText != null)
                return endedText;

            if (ended)
                return status;

            var g = session?.Game;

            if (g == null)
                return status;

            return $"{g.Phase.ToString().ToLowerInvariant()} round {g.Round} (fresh sequence of {g.Length}), {g.Position}/{g.Length} entered, max {g.MaxLen}, {pads.Count} pad(s)";
        }

        public string Reveal(HazardContext ctx) => reveal;

        // ---- the decisions (world-free; tested through IMemoryWorld) ---------------------------------------------

        internal void StartCore(IMemoryWorld w)
        {
            world = w;
            settings = MemorySettings.Read(w.Args);

            // The world's say on top of the region's: a pad needs a cell under its centre and most of its rim.
            Func<Vector2, bool> cellOk = p => w.HasCell(w.GroundPoint(p.X, p.Y), true);

            pads = MemoryLayout.Plan(w.Region, settings.RandomLayout, w.Rng, settings.PadRadius, settings.PadGap, w.EdgeInset, cellOk);

            if (pads == null)
            {
                pads = Array.Empty<Vector2>();
                Abort(w, "no room for four pads");
                return;
            }

            var count = MemoryLayout.RingCount(settings.PadRadius, settings.PadSpacing);

            for (var pad = 0; pad < pads.Count; pad++)
            {
                foreach (var p in MemoryLayout.RingPoints(pads[pad], settings.PadRadius, count))
                    markers.Add(new Marker { Pad = pad, Position = w.GroundPoint(p.X, p.Y) });
            }

            foreach (var m in markers)
            {
                m.Piece = SpawnMarker(w, m);
                m.Placed = m.Piece != null;
            }

            // A pad with too few markers is invisible but still live: refuse the whole run instead.
            var need = MemoryMarkers.MinimumPlaced(count);

            for (var pad = 0; pad < pads.Count; pad++)
            {
                var placed = 0;

                foreach (var m in markers)
                    if (m.Pad == pad && m.Placed)
                        placed++;

                if (placed < need)
                {
                    Abort(w, $"pad {pad + 1} ({MemoryRules.ColourName(pad)}) got only {placed} of {count} markers (need {need})");
                    return;
                }
            }

            var game = new MemoryGame(MemoryRules.NewSequence(w.Rng, settings.Len), settings.MaxLen, settings.PulseOn, settings.PulseOff,
                MemorySettings.RoundPause, settings.AnswerTimeout);
            session = new MemorySession(game, pads, settings.PadRadius, w.Rng);

            reveal = MemoryRules.Format(game.Sequence);
            lastRepair = w.Now;

            w.Log($"memory start layout={(settings.RandomLayout ? "random" : "quadrant")} pads={FormatPads()} markers={markers.Count} per-pad={count} marker-z-offset=0 (PointFor Ground) beams={BeamsPerPulseText()} round-1 sequence=[{reveal}]");

            game.BeginRound(w.Now);
            status = $"playback round {game.Round}";
            w.Say($"Round {game.Round}: {game.Length} colours");
        }

        internal void TickCore(IMemoryWorld w)
        {
            if (session == null || session.Game.Phase == MemoryPhase.Done)
                return;

            if (w.Now - lastRepair >= RepairEverySeconds)
            {
                lastRepair = w.Now;
                RepairMarkers(w);

                if (session.Game.Phase == MemoryPhase.Done || ended)
                    return;
            }

            var game = session.Game;

            var events = session.Step(w.Now, w.Players());

            // The new round's sequence is dealt inside Step: publish it BEFORE any event line is said.
            if (game.Round != revealRound)
            {
                revealRound = game.Round;
                reveal = MemoryRules.Format(game.Sequence);
            }

            foreach (var e in events)
            {
                switch (e.Kind)
                {
                    case MemoryEventKind.Fire:
                        FireBeams(w, e.Colour);
                        break;

                    case MemoryEventKind.YourTurn:
                        status = $"answer round {game.Round}";
                        w.Say("Your turn.");
                        w.SayPlayer("Your turn.");
                        break;

                    case MemoryEventKind.Correct:
                        EchoOnPad(w, e.Pad);
                        w.Say($"{w.NameOf(e.Player)} stepped on {MemoryRules.ColourName(e.Pad)} - correct, step {e.Step} of {e.Length}.");
                        w.SayPlayer($"{MemoryRules.ColourName(e.Pad)} - correct, step {e.Step} of {e.Length}.");
                        break;

                    case MemoryEventKind.RoundWon:
                        w.Say($"Round {e.Round} complete.");
                        break;

                    case MemoryEventKind.NextRound:
                        // a brand new random sequence (already published above)
                        status = $"playback round {e.Round}";
                        w.Say($"Round {e.Round}: {e.Length} colours");
                        break;

                    case MemoryEventKind.Won:
                        status = "complete";
                        w.Say($"The sequence is complete! All {e.Length} colours answered.");
                        w.End(HazardOutcome.Completed, $"sequence complete at {e.Length} colours");
                        return;

                    case MemoryEventKind.Wrong:
                    {
                        var who = w.NameOf(e.Player);
                        var stepped = MemoryRules.ColourName(e.Pad);

                        Fail(w, $"{who} stepped on {stepped} but the sequence wanted {MemoryRules.ColourName(e.Expected)} (step {e.Step + 1} of {e.Length})",
                            $"{who} stepped on {stepped} - wrong colour! The sequence is broken.");
                        return;
                    }

                    case MemoryEventKind.TimedOut:
                        Fail(w, $"no correct entry within {settings.AnswerTimeout:0.#} s (waiting for step {e.Step + 1} of {e.Length})");
                        return;
                }
            }

            StrikePillars(w);
        }

        internal void StopCore(IMemoryWorld w, HazardOutcome outcome)
        {
            status = $"ended ({outcome})";
            endedText = status;
            pillars.Clear();

            if (outcome == HazardOutcome.Stopped || outcome == HazardOutcome.Reaped)
                w.Say("The memory game has ended.");
        }

        // ---- internals ------------------------------------------------------------------------------------------

        /// <summary>The PLAYERS lost: a wrong pad or a timeout.</summary>
        private void Fail(IMemoryWorld w, string reason, string line = null)
        {
            ended = true;
            status = "failed";
            w.Say(line ?? $"The memory game is lost: {reason}.");
            w.End(HazardOutcome.Failed, reason);
        }

        /// <summary>The MECHANIC cannot continue (no room, a pad's markers gone): not the players' fault.</summary>
        private void Abort(IMemoryWorld w, string reason)
        {
            ended = true;
            status = "aborted";
            w.Say($"The memory game cannot continue: {reason}.");
            w.End(HazardOutcome.Aborted, reason);
        }

        private static IMemoryPiece SpawnMarker(IMemoryWorld w, Marker m)
            => w.SpawnMarker(MemoryRules.PadWcids[m.Pad], m.Position, "pad-" + MemoryRules.ColourName(m.Pad));

        /// <summary>/reload-landblock destroys every non-persisted object while the run survives: respawn markers that were placed and died.</summary>
        private void RepairMarkers(IMemoryWorld w)
        {
            foreach (var m in markers)
            {
                if (!MemoryMarkers.NeedsRespawn(m.Placed, m.Piece != null && m.Piece.Alive))
                    continue;

                m.Piece = SpawnMarker(w, m);
                m.Placed = m.Piece != null;
            }

            // The shared dead-handle policy: carry on without a lost marker, but a pad left with too few live markers
            // (the respawn was refused) is invisible and still live, so the game ends Aborted with a one-line reason,
            // as the wisps do when no wisp is left and the balls do when a whole wave is lost.
            var need = MemoryMarkers.MinimumPlaced(MemoryLayout.RingCount(settings.PadRadius, settings.PadSpacing));

            for (var pad = 0; pad < pads.Count; pad++)
            {
                var live = 0;

                foreach (var m in markers)
                    if (m.Pad == pad && m.Piece != null && m.Piece.Alive)
                        live++;

                if (live < need)
                {
                    Abort(w, $"pad {pad + 1} ({MemoryRules.ColourName(pad)}) lost: only {live} live markers (need {need}) and they could not be respawned");
                    return;
                }
            }
        }

        /// <summary>
        /// The standard hazard hit of every standing pad pillar, once per tick after the tick's events: the pillar is the damage
        /// source, the centre is the pad centre, the radius is pad_radius (the circle; the player's own radius is added by
        /// HitPlayers) and the window is infinite, so a player is hit once per pillar. An expired or dead pillar is dropped. Never
        /// after the run ended: the Fail/Abort/Won paths return before this, and `ended` covers the repair path.
        /// </summary>
        private void StrikePillars(IMemoryWorld w)
        {
            if (ended)
                return;

            for (var i = pillars.Count - 1; i >= 0; i--)
            {
                var pillar = pillars[i];

                if (w.Now >= pillar.Expiry || !pillar.Piece.Alive)
                {
                    pillars.RemoveAt(i);
                    continue;
                }

                w.Strike(pillar.Piece, pillar.Centre, settings.PadRadius);
            }
        }

        private void FireBeams(IMemoryWorld w, int colour)
        {
            var script = MemoryRules.BeamScripts[colour];
            var fired = 0;

            // Validate refused a ceiling beam in a region without one; the floor path is also the fallback here.
            var ceilingZ = settings.BeamsFromCeiling ? w.Region.CeilingZ : null;

            if (settings.BeamsFromCeiling && !ceilingZ.HasValue && !noCeilingLogged)
            {
                noCeilingLogged = true;
                w.Log("memory: beam_from=ceiling but the region has no CeilingZ; firing floor beams instead (Validate should have refused this)");
            }

            // A ceiling host is checked AND spawned exactly where it hangs (no raise): a raise walks up to 1.5 m and
            // could resolve into a cell above the ceiling. Floor hosts keep the raise.
            var raise = !ceilingZ.HasValue;

            // the point the beam host will really sit at, and the orientation it spawns with
            Func<Vector2, Vector3> hostAt = p => ceilingZ.HasValue ? MemoryLayout.CeilingBeamPoint(p, ceilingZ.Value) : w.GroundPoint(p.X, p.Y);
            var orientation = MemoryLayout.BeamOrientation(MemoryLayout.BeamTurned(ceilingZ.HasValue, settings.BeamFlip));

            Func<Vector2, bool> cellOk = p => w.HasCell(hostAt(p), raise);

            // beam_corners: the first beams of the pulse stand one in each corner, so no corner of the room misses the
            // colour; a corner the world refuses falls back to a random point. The count stays beams_per_pulse.
            var corners = settings.BeamCorners
                ? MemoryLayout.CornerBeamPoints(w.Region, pads, settings.PadRadius, w.EdgeInset)
                : Array.Empty<Vector2>();

            for (var i = 0; i < settings.BeamsPerPulse; i++)
            {
                Vector2 p;

                if (i < corners.Count && cellOk(corners[i]))
                    p = corners[i];
                else if (!MemoryLayout.PickBeamPoint(w.Region, w.Rng, pads, settings.PadRadius, w.EdgeInset, cellOk, out p))
                    continue;

                if (w.SpawnBeam(hostAt(p), script, settings.PulseOn, "beam-" + MemoryRules.ColourName(colour), orientation, raise))
                    fired++;
            }

            // pad_pillar: one fire pillar on the centre of THIS colour's pad (pad index == colour index), on the ground point, standing
            // pulse_on seconds; StrikePillars deals the standard hazard damage while it stands
            if (settings.PadPillar)
            {
                var centre = w.GroundPoint(pads[colour].X, pads[colour].Y);
                var piece = w.SpawnPillar(centre, settings.PulseOn, "pillar-" + MemoryRules.ColourName(colour));

                if (piece != null)
                    pillars.Add(new Pillar { Piece = piece, Centre = centre, Expiry = w.Now + settings.PulseOn });
                else if (!padPillarRefusedLogged)
                {
                    padPillarRefusedLogged = true;
                    w.Log($"memory: pad_pillar refused for {MemoryRules.ColourName(colour)}");
                }
            }

            if (fired == 0 && !noBeamRoomLogged)
            {
                noBeamRoomLogged = true;
                w.Log($"memory: no beam placed for {MemoryRules.ColourName(colour)} (no cell-valid point off the pads, or the spawns were refused)");
            }
        }

        private void EchoOnPad(IMemoryWorld w, int pad)
        {
            var c = pads[pad];

            w.SpawnBeam(w.GroundPoint(c.X, c.Y), MemoryRules.BeamScripts[pad], EchoSeconds, "echo-" + MemoryRules.ColourName(pad), MemoryLayout.BeamOrientation(MemoryLayout.BeamTurned(false, settings.BeamFlip)), true);
        }

        private string BeamsPerPulseText()
            => $"{settings.BeamsPerPulse}x{(settings.BeamsFromCeiling ? "ceiling" : "floor")}{(settings.BeamFlip ? "+swap" : "")}{(settings.BeamCorners ? "+corners" : "")}{(settings.PadPillar ? "+pillar" : "")}";

        private string FormatPads()
        {
            var parts = new List<string>();

            for (var i = 0; i < pads.Count; i++)
                parts.Add(FormattableString.Invariant($"{MemoryRules.ColourName(i)}({pads[i].X:0.0},{pads[i].Y:0.0})"));

            return string.Join(" ", parts);
        }
    }
}
