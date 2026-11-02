using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles.Grael;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// GraelSettings is a pure record, so nothing here reads PropertyManager. The Validate test runs every default
    /// through the REAL mechanic pre-flight on the Pinnacle preset, so a default the mechanics would refuse fails
    /// here instead of refusing every boss run in game.
    /// </summary>
    [TestClass]
    public class GraelSettingsTests
    {
        private static HazardRegion Region => HazardRegionPresets.Pinnacle(0);

        private static HazardArgs WithHead(HazardArgs args)
            => args
                .With("cx", GraelData.HeadPoint.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .With("cy", GraelData.HeadPoint.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        // owner 2026-10-25: the 9-particle beam is the narrow one; the Grael mark and ceiling beam use the red one
        [TestMethod]
        public void DefaultWisps_MarkAndCeilingBeamUseTheNarrowBeam()
        {
            foreach (var row in new[] { GraelSettings.DefaultP2Wisps, GraelSettings.DefaultP5Wisps })
            {
                var w = WispSettings.From(HazardArgs.Merge(HazardArgs.Parse(row), HazardArgs.Parse(HazardDefaults.Wisps)));

                Assert.AreEqual(HazardDefaults.BeamAltScript, w.MarkScript, row);
                Assert.AreEqual(HazardDefaults.BeamAltScript, w.BeamScript, row);
                Assert.IsTrue(HazardDefaults.HangsBelowHost(w.MarkScript), "the floor mark must still be turned to rise");
            }
        }

        [TestMethod]
        public void DefaultStrings_PassMechanicValidate_OnPinnaclePreset()
        {
            var s = GraelSettings.Defaults;
            var failures = new List<string>();

            void Check(string what, string refusal)
            {
                if (refusal != null)
                    failures.Add($"{what}: {refusal}");
            }

            var (p2Pattern, p2Args) = s.P2Series();
            Check("p2 wisps", WispRules.Validate(Region, p2Args, p2Pattern));

            var (p5Pattern, p5Args) = s.P5Series();
            Check("p5 wisps", WispRules.Validate(Region, p5Args, p5Pattern));

            // fan and both rings are centred on the head (the core fills cx/cy from the origin); row and cross are not
            // the ring-out and ring-in patterns stay valid on their own; Grael runs ONE ring-alt series with twice the waves
            foreach (var pattern in new[] { BallPatterns.Fan, BallPatterns.RingOut, BallPatterns.RingIn })
                Check("p4 balls " + pattern, BallRules.Validate(Region, WithHead(s.P4BallArgs()), pattern));
            Check("p4 fan (no lead)", BallRules.Validate(Region, WithHead(s.P4FanArgs()), BallPatterns.Fan));
            Check("p4 rings ring-alt", BallRules.Validate(Region, WithHead(s.P4RingArgs()), BallPatterns.RingAlt));

            // the row series runs its own string (gaps=2); the cross keeps the pinned gaps=1
            Check("p4 row", BallRules.Validate(Region, s.P4RowArgs(), BallPatterns.Row));
            Check("p4 cross", BallRules.Validate(Region, s.P4CrossArgs(), BallPatterns.Cross));

            Check("p4 stream", BallRules.Validate(Region, s.P4StreamArgs(), BallPatterns.Random));

            Check("p5 balls", BallRules.Validate(Region, s.P5BallArgs(250), BallPatterns.Random));

            var memory = new MemoryMechanic();

            for (var length = s.MemoryOpeningLength; length < s.MemoryOpeningLength + GraelSettings.MemoryRounds; length++)
                Check("p3 memory L=" + length, memory.Validate(Region, s.MemoryRoundArgs(length, 300), null));

            Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));
        }

        /// <summary>
        /// C14: what a run actually gets is its args laid OVER the hazard_mech_&lt;kind&gt; row (HazardManager.Start:
        /// HazardArgs.Merge(args, settings)). Validated here against the shipped HazardDefaults row strings.
        /// </summary>
        [TestMethod]
        public void DefaultStrings_MergedOverTheHazardDefaults_PassMechanicValidate()
        {
            var s = GraelSettings.Defaults;
            var failures = new List<string>();
            var wisps = HazardArgs.Parse(HazardDefaults.Wisps);
            var balls = HazardArgs.Parse(HazardDefaults.Balls);
            var memoryRow = HazardArgs.Parse(HazardDefaults.Memory);

            void Check(string what, string refusal)
            {
                if (refusal != null)
                    failures.Add($"{what}: {refusal}");
            }

            var (p2Pattern, p2Args) = s.P2Series();
            Check("p2 wisps", WispRules.Validate(Region, HazardArgs.Merge(p2Args, wisps), p2Pattern));

            var (p5Pattern, p5Args) = s.P5Series();
            Check("p5 wisps", WispRules.Validate(Region, HazardArgs.Merge(p5Args, wisps), p5Pattern));

            foreach (var pattern in new[] { BallPatterns.Fan, BallPatterns.RingOut, BallPatterns.RingIn })
                Check("p4 balls " + pattern, BallRules.Validate(Region, HazardArgs.Merge(WithHead(s.P4BallArgs()), balls), pattern));
            Check("p4 fan (no lead)", BallRules.Validate(Region, HazardArgs.Merge(WithHead(s.P4FanArgs()), balls), BallPatterns.Fan));
            Check("p4 rings ring-alt", BallRules.Validate(Region, HazardArgs.Merge(WithHead(s.P4RingArgs()), balls), BallPatterns.RingAlt));

            Check("p4 row", BallRules.Validate(Region, HazardArgs.Merge(s.P4RowArgs(), balls), BallPatterns.Row));
            Check("p4 cross", BallRules.Validate(Region, HazardArgs.Merge(s.P4CrossArgs(), balls), BallPatterns.Cross));

            Check("p4 stream", BallRules.Validate(Region, HazardArgs.Merge(s.P4StreamArgs(), balls), BallPatterns.Random));

            Check("p5 balls", BallRules.Validate(Region, HazardArgs.Merge(s.P5BallArgs(250), balls), BallPatterns.Random));

            var memory = new MemoryMechanic();

            for (var length = s.MemoryOpeningLength; length < s.MemoryOpeningLength + GraelSettings.MemoryRounds; length++)
                Check("p3 memory L=" + length, memory.Validate(Region, HazardArgs.Merge(s.MemoryRoundArgs(length, 300), memoryRow), null));

            Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));
        }

        /// <summary>
        /// Owner ruling 2026-10-22 (on round 3's twice-the-balls): the P4 stream is 60 balls over the stream's 60 s at
        /// 6 -> 9 m/s with occupancy 1.25, PINNED in P4StreamArgs over the settings string and the live row. The shipped
        /// args pass Validate raw and merged over the balls default row; a row still holding the old count=20 speed=3
        /// occupancy=0.75 cannot undo the ruling. CONTROLS: the same 60 balls at the default 0.75 occupancy are refused,
        /// and 60 balls from 3 m/s are refused even at 1.25 (the bound is relaxed, not disabled).
        /// </summary>
        [TestMethod]
        public void P4Stream_SixtyBallsAtSixToNine_Occupancy125_Pinned_AndValid()
        {
            var balls = HazardArgs.Parse(HazardDefaults.Balls);
            var stale = GraelSettings.From(null, null, 5, null, "stream=60 count=20 speed=3 speed_end=9 occupancy=0.75 marker=0", 4, null, 20, 3);

            foreach (var s in new[] { GraelSettings.Defaults, stale })
            {
                var args = s.P4StreamArgs();

                Assert.AreEqual("60", args.GetString("count"));
                Assert.AreEqual("6", args.GetString("speed"));
                Assert.AreEqual("9", args.GetString("speed_end"));
                Assert.AreEqual("1.25", args.GetString("occupancy"));
                Assert.AreEqual("60", args.GetString("stream"), "the same duration");

                var merged = HazardArgs.Merge(args, balls);
                Assert.AreEqual("60", merged.GetString("count"));
                Assert.AreEqual("6", merged.GetString("speed"));
                Assert.AreEqual("1.25", merged.GetString("occupancy"));

                Assert.IsNull(BallRules.Validate(Region, args, BallPatterns.Random), "raw");
                Assert.IsNull(BallRules.Validate(Region, merged, BallPatterns.Random), "merged over the balls default row");
            }

            // three times the shipped string's count
            Assert.AreEqual(3 * HazardArgs.Parse(GraelSettings.DefaultP4Stream).GetInt("count", 0, 0, 100), int.Parse(GraelSettings.P4StreamCount));

            var strict = GraelSettings.Defaults.P4StreamArgs().With("occupancy", "0.75");
            StringAssert.Contains(BallRules.Validate(Region, strict, BallPatterns.Random), "random launch rate is too high", "control (a): 60 balls at 6 m/s are refused at the default occupancy");

            var slow = GraelSettings.Defaults.P4StreamArgs().With("speed", "3").With("speed_end", "3");
            StringAssert.Contains(BallRules.Validate(Region, slow, BallPatterns.Random), "random launch rate is too high", "control (b): 60 balls at 3 m/s are refused even at occupancy 1.25");
        }

        /// <summary>
        /// The head's spell book (Content/sql/weenies/1006909 Pinnacle Grael Head.sql, content/pinnacle-grael-pt2 commit
        /// 7045121d4) as of round 3: 4439, 4423, 4440, 6191, 4481. Held here as a list because the content lives on its
        /// own branch; once this checkout's copy of the file carries the 6191 row, the file's rows are checked too.
        /// </summary>
        private static readonly uint[] HeadSpellBook = { 4439, 4423, 4440, 6191, 4481 };

        /// <summary>
        /// The script casts from GraelData (P4Spells, and HeadVulnSpell in every phase), never from the book; the book
        /// mirrors it (round 3 review F4), so a spell added to one and not the other is caught here.
        /// </summary>
        [TestMethod]
        public void HeadSpellBook_MirrorsTheScriptCastList()
        {
            var cast = GraelData.P4Spells.Concat(new[] { GraelData.HeadVulnSpell }).ToArray();

            CollectionAssert.AreEquivalent(HeadSpellBook, cast);

            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            string file = null;

            while (dir != null && file == null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "Content", "sql", "weenies", "1006909 Pinnacle Grael Head.sql");

                if (System.IO.File.Exists(candidate))
                    file = candidate;

                dir = dir.Parent;
            }

            if (file == null)
                return;

            var text = System.IO.File.ReadAllText(file);
            var book = text.Substring(text.IndexOf("weenie_properties_spell_book", System.StringComparison.Ordinal));
            book = book.Substring(0, book.IndexOf(';'));

            var rows = System.Text.RegularExpressions.Regex.Matches(book, @"\(1006909,\s*(\d+),")
                .Select(m => uint.Parse(m.Groups[1].Value)).ToArray();

            Assert.IsTrue(rows.Length > 0, "the spell book rows parse");

            if (rows.Contains(6191u))
                CollectionAssert.AreEquivalent(HeadSpellBook, rows, "the content file's spell book");
        }

        [TestMethod]
        public void PinnedKeys_SurviveARowThatSaysOtherwise()
        {
            // C14: a shard row saved with other values cannot change these fight-critical keys
            var s = GraelSettings.Defaults;
            var oddRow = HazardArgs.Parse("spread=90 beams_per_pulse=2 mark_dest=0");

            Assert.AreEqual("90", HazardArgs.Merge(s.P4BallArgs(), oddRow).GetString("spread"), "round 3: the fan is 90 degrees wide");

            // round 3: two openings per row wall, one per cross wall, whatever a balls row says
            var gapsRow = HazardArgs.Parse("gaps=4");

            Assert.AreEqual("2", HazardArgs.Merge(s.P4RowArgs(), gapsRow).GetString("gaps"));
            Assert.AreEqual("1", HazardArgs.Merge(s.P4CrossArgs(), gapsRow).GetString("gaps"), "the cross keeps one opening");
            Assert.AreEqual("1", HazardArgs.Merge(s.P4RingArgs(), gapsRow).GetString("gaps"));
            Assert.AreEqual("2", HazardArgs.Merge(s.P4RowArgs(), HazardArgs.Parse("max_in_flight=9")).GetString("max_in_flight"), "rows start at most 2 in flight");
            Assert.AreEqual("3", HazardArgs.Merge(s.P4RowArgs(), HazardArgs.Parse("max_in_flight_end=9")).GetString("max_in_flight_end"), "and end at 3");
            Assert.IsFalse(s.P4BallArgs().Has("max_in_flight"), "only the rows are capped");
            Assert.IsFalse(s.P4RingArgs().Has("max_in_flight"));
            Assert.AreEqual("0", s.P4RowArgs().GetString("marker"), "no pillar telegraphs on the rows");
            Assert.AreEqual("0", s.P4RowArgs().GetString("ray_markers"));
            Assert.AreEqual("12", s.P4RingArgs().GetString("waves"), "the ring series is unaffected: still twice the waves");
            // 2026-10-19 playtest: no P4 telegraphs, whatever the row says
            var markingRow = HazardArgs.Parse("ray_markers=1 marker=1");

            Assert.AreEqual("0", HazardArgs.Merge(s.P4BallArgs(), markingRow).GetString("ray_markers"), "no ray markers in P4");
            Assert.AreEqual("0", HazardArgs.Merge(s.P4BallArgs(), markingRow).GetString("marker"), "no gap markers in P4");
            Assert.AreEqual("0", HazardArgs.Merge(s.P4RingArgs(), markingRow).GetString("ray_markers"), "no ray markers on the rings");
            Assert.AreEqual("0", HazardArgs.Merge(s.P4StreamArgs(), markingRow).GetString("marker"), "no markers on the stream");
            Assert.AreEqual("3", HazardArgs.Merge(s.P4BallArgs(), HazardArgs.Parse("wave_every=9")).GetString("wave_every"), "the cadence is pinned");
            Assert.AreEqual("12", HazardArgs.Merge(s.MemoryRoundArgs(4, 300), oddRow).GetString("beams_per_pulse"), "round 4: twice the beams");
            Assert.AreEqual("12", HazardArgs.Merge(s.MemoryRoundArgs(7, 300), oddRow).GetString("beams_per_pulse"));
            Assert.AreEqual("1", HazardArgs.Merge(s.MemoryRoundArgs(4, 300), HazardArgs.Parse("beam_corners=0")).GetString("beam_corners"), "a beam in every corner");
            Assert.AreEqual("1", HazardArgs.Merge(s.P2Series().Args, oddRow).GetString("mark_dest"));
            Assert.AreEqual("1", HazardArgs.Merge(s.P5Series().Args, oddRow).GetString("mark_dest"));

            // review R3: P5 has no follower, even over a wisps row that turns one on
            Assert.AreEqual("0", HazardArgs.Merge(s.P5Series().Args, HazardArgs.Parse("follow=1")).GetString("follow"));
            Assert.IsFalse(WispSettings.From(HazardArgs.Merge(s.P5Series().Args, HazardArgs.Parse("follow=1"))).Follow, "the merged P5 settings carry no follower");
        }

        /// <summary>The P4 ball series as GraelScript runs them, each with its expected flat lead (owner, 2026-10-19).</summary>
        private static (string Name, HazardArgs Args, string Pattern, double Lead)[] LeadSeries(GraelSettings s) => new[]
        {
            ("fan", WithHead(s.P4FanArgs()), BallPatterns.Fan, 0.0),
            ("rings", WithHead(s.P4RingArgs()), BallPatterns.RingAlt, 0.0),
            ("row", s.P4RowArgs(), BallPatterns.Row, 2.0),
            ("cross", s.P4CrossArgs(), BallPatterns.Cross, 2.0),
        };

        [TestMethod]
        public void P4Leads_FanAndRingsNone_RowsAndCrossFlatTwoSeconds_EveryWave()
        {
            // owner, 2026-10-19: "No hover delay" (fan); "rows and cross can have 2s hover. Rings should not."
            foreach (var x in LeadSeries(GraelSettings.Defaults))
            {
                var p = BallRules.Read(HazardArgs.Merge(x.Args, HazardArgs.Parse(HazardDefaults.Balls)), x.Pattern);

                Assert.IsFalse(p.FairLeadFloor, x.Name + ": the lead is pinned, not floored");

                for (var wave = 1; wave <= p.Waves; wave++)
                    foreach (var d in new double?[] { null, 5.0, 20.0 })
                        Assert.AreEqual(x.Lead, BallRules.LeadFor(Region, x.Pattern, BallRules.ForWave(p, wave), x.Pattern == BallPatterns.Fan ? d : null), 1e-9, $"{x.Name} wave {wave} d={d}");
            }

            Assert.AreEqual(12, BallRules.Read(HazardArgs.Merge(WithHead(GraelSettings.Defaults.P4RingArgs()), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.RingAlt).Waves, "control: all 12 ring waves were checked");
        }

        [TestMethod]
        public void P4Leads_SurviveARowOrAStringThatSaysOtherwise()
        {
            // a shard hazard_mech_balls row (laid UNDER the args) or a pinnacle_grael_p4_* string with fair_lead=1, lead=5
            // or a lead_end ramp changes none of the pinned leads
            var rows = new[]
            {
                HazardArgs.Parse("fair_lead=1 " + HazardDefaults.Balls),
                HazardArgs.Parse("lead=5 " + HazardDefaults.Balls),
                HazardArgs.Parse("fair_lead=1 lead=5 lead_end=9 " + HazardDefaults.Balls),
            };
            var odd = GraelSettings.From(null, null, 5, "waves=6 spread=90 lead=8 lead_end=12 fair_lead=1", "stream=60 count=20 fair_lead=0", 4, null, 20, 3,
                "waves=6 gaps=2 gap=6 lead=5 lead_end=9 fair_lead=1 wave_every=6 max_in_flight=2");

            foreach (var s in new[] { GraelSettings.Defaults, odd })
                foreach (var row in rows)
                    foreach (var x in LeadSeries(s))
                    {
                        var p = BallRules.Read(HazardArgs.Merge(x.Args, row), x.Pattern);

                        for (var wave = 1; wave <= p.Waves; wave++)
                            Assert.AreEqual(x.Lead, BallRules.LeadFor(Region, x.Pattern, BallRules.ForWave(p, wave), x.Pattern == BallPatterns.Fan ? 20.0 : null), 1e-9, $"{x.Name} wave {wave} over '{row}'");
                    }

            // control: the same row DOES change a series that pins no lead (the base args the series are built from)
            var unpinned = BallRules.Read(HazardArgs.Merge(HazardArgs.Parse("waves=6"), rows[1]), BallPatterns.Cross);
            Assert.IsTrue(BallRules.LeadFor(Region, BallPatterns.Cross, unpinned, null) >= 5.0, "control: lead=5 in the row reaches unpinned args");
        }

        [TestMethod]
        public void StreamAndBaseArgs_KeepTheFairLeadFloor_OverARowOrStringThatTurnsItOff()
        {
            // review F1: a fair_lead=0 in the live hazard_mech_balls row (laid UNDER the args) or in the p4 strings
            // themselves must not strip the floor from the stream, nor from the base args the fan/ring/cross are built on
            var row = HazardArgs.Parse("fair_lead=0 " + HazardDefaults.Balls);
            var off = GraelSettings.From(null, null, 5, "waves=6 spread=90 fair_lead=0", "stream=60 count=20 fair_lead=0", 4, null, 20, 3, "waves=6 gaps=2 gap=6 fair_lead=0");

            foreach (var s in new[] { GraelSettings.Defaults, off })
            {
                foreach (var x in new[] { (Name: "base balls", Args: s.P4BallArgs(), Pattern: BallPatterns.Cross), (Name: "stream", Args: s.P4StreamArgs(), Pattern: BallPatterns.Random) })
                {
                    var merged = HazardArgs.Merge(x.Args, row);

                    Assert.AreEqual("1", merged.GetString("fair_lead"), x.Name);
                    Assert.IsTrue(BallRules.Read(merged, x.Pattern).FairLeadFloor, x.Name + " keeps the fair-lead floor");
                }
            }

            // control: the same row DOES turn the floor off for args that do not pin it
            Assert.IsFalse(BallRules.Read(HazardArgs.Merge(HazardArgs.Parse("waves=6"), row), BallPatterns.Row).FairLeadFloor, "control: the row alone turns it off");
        }

        [TestMethod]
        public void RowArgs_PinGap_SoAWideGapInTheBallsRowCannotSkipTheSeries()
        {
            // review F1: two openings fit an east-west Pinnacle wall only up to about gap 6.55 m
            var s = GraelSettings.Defaults;
            var wideRow = HazardArgs.Merge(HazardArgs.Parse("gap=8"), HazardArgs.Parse(HazardDefaults.Balls));

            Assert.AreEqual("6", HazardArgs.Merge(s.P4RowArgs(), wideRow).GetString("gap"), "the rows pin gap=6");
            Assert.IsNull(BallRules.Validate(Region, HazardArgs.Merge(s.P4RowArgs(), wideRow), BallPatterns.Row), "the row series still validates over a gap=8 balls row");

            // control: the same string WITHOUT its gap pin is refused over that row, so the pin is what saves the series
            var unpinned = HazardArgs.FromTokens(GraelSettings.DefaultP4Rows.Split(' ').Where(t => !t.StartsWith("gap=", System.StringComparison.Ordinal)));
            StringAssert.Contains(BallRules.Validate(Region, HazardArgs.Merge(unpinned, wideRow), BallPatterns.Row), "do not fit");
        }

        [TestMethod]
        public void Defaults_RoundTripTheirConstStrings()
        {
            var d = GraelSettings.Defaults;

            Assert.AreEqual(GraelSettings.DefaultP2Wisps, d.P2Wisps);
            Assert.AreEqual(GraelSettings.DefaultP3Memory, d.P3Memory);
            Assert.AreEqual(GraelSettings.DefaultP3DispelCount, d.P3DispelCount);
            Assert.AreEqual(GraelSettings.DefaultP4Balls, d.P4Balls);
            Assert.AreEqual(GraelSettings.DefaultP4Stream, d.P4Stream);
            Assert.AreEqual(GraelSettings.DefaultP4Rows, d.P4Rows);
            Assert.AreEqual("waves=6", GraelSettings.From(null, null, 5, null, null, 4, null, 20, 3, "waves=6").P4Rows, "a rows row is taken as written");
            Assert.AreEqual(GraelSettings.DefaultP4Rows, GraelSettings.From(null, null, 5, null, null, 4, null, 20, 3, "  ").P4Rows, "blank restores the shipped string");
            Assert.AreEqual((double)GraelSettings.DefaultP4CastSeconds, d.P4CastSeconds);
            Assert.AreEqual(GraelSettings.DefaultP5Wisps, d.P5Wisps);
            Assert.AreEqual((double)GraelSettings.DefaultP5RespawnSeconds, d.P5RespawnSeconds);
            Assert.AreEqual((double)GraelSettings.DefaultP5CastSeconds, d.P5CastSeconds);

            var again = GraelSettings.From(d.P2Wisps, d.P3Memory, d.P3DispelCount, d.P4Balls, d.P4Stream, (long)d.P4CastSeconds, d.P5Wisps, (long)d.P5RespawnSeconds, (long)d.P5CastSeconds);
            Assert.AreEqual(d, again);
        }

        [TestMethod]
        public void From_BlankStrings_FallBackToDefaults_AndNumbersAreFloored()
        {
            var s = GraelSettings.From("", "  ", -3, null, "", 0, null, -1, 0);

            Assert.AreEqual(GraelSettings.DefaultP2Wisps, s.P2Wisps);
            Assert.AreEqual(GraelSettings.DefaultP3Memory, s.P3Memory);
            Assert.AreEqual(0, s.P3DispelCount);
            Assert.AreEqual(1.0, s.P4CastSeconds);
            Assert.AreEqual(1.0, s.P5RespawnSeconds);
            Assert.AreEqual(1.0, s.P5CastSeconds);
        }

        [TestMethod]
        public void P2Series_SplitsThePatternOut_AndKeepsTheRampAndFollowerKeys()
        {
            var (pattern, args) = GraelSettings.Defaults.P2Series();

            Assert.AreEqual("scatter", pattern);
            Assert.IsFalse(args.Has("pattern"));
            Assert.AreEqual("0.5", args.GetString("hold_end"));
            Assert.AreEqual("0", args.GetString("hold_floor"));
            Assert.AreEqual("1", args.GetString("follow"));
            Assert.AreEqual("6", args.GetString("cycles"));
        }

        [TestMethod]
        public void MemoryRoundArgs_RampPulsesByLength_AndDropTheEndKeys()
        {
            var s = GraelSettings.Defaults;

            var l4 = s.MemoryRoundArgs(4, 300);
            Assert.AreEqual(1.0, l4.GetDouble("pulse_on", 0, 0, 30), 1e-9);
            Assert.AreEqual(0.5, l4.GetDouble("pulse_off", 0, 0, 30), 1e-9);
            Assert.AreEqual("4", l4.GetString("len"));
            Assert.AreEqual("4", l4.GetString("max_len"));
            Assert.IsFalse(l4.Has("pulse_on_end"));
            Assert.IsFalse(l4.Has("pulse_off_end"));
            Assert.AreEqual("20", l4.GetString("answer_timeout"));

            var l7 = s.MemoryRoundArgs(7, 300);
            Assert.AreEqual(0.5, l7.GetDouble("pulse_on", 0, 0, 30), 1e-9);
            Assert.AreEqual(0.25, l7.GetDouble("pulse_off", 0, 0, 30), 1e-9);
            Assert.AreEqual("7", l7.GetString("len"));

            var l5 = s.MemoryRoundArgs(5, 300);
            Assert.AreEqual(1.0 + (0.5 - 1.0) / 3.0, l5.GetDouble("pulse_on", 0, 0, 30), 1e-6);
        }

        [TestMethod]
        public void MemoryRoundArgs_TwelveBeamsWithTheCorners_EveryRound_RawAndMergedOverTheDefaultRow()
        {
            // round 4 playtest: twice the default 6 beams a pulse, one in each corner of the room; pinned whatever the
            // settings string (which still says 6) or the shard row says
            var s = GraelSettings.Defaults;
            StringAssert.Contains(GraelSettings.DefaultP3Memory, "beams_per_pulse=6", "the shipped string is unchanged (config defaults snapshot)");

            for (var length = s.MemoryOpeningLength; length < s.MemoryOpeningLength + GraelSettings.MemoryRounds; length++)
            {
                foreach (var args in new[] { s.MemoryRoundArgs(length, 300), HazardArgs.Merge(s.MemoryRoundArgs(length, 300), HazardArgs.Parse(HazardDefaults.Memory)) })
                {
                    var read = MemorySettings.Read(args);

                    Assert.AreEqual(12, read.BeamsPerPulse, "L=" + length);
                    Assert.AreEqual(2 * MemorySettings.DefaultBeamsPerPulse, read.BeamsPerPulse, "doubled");
                    Assert.IsTrue(read.BeamCorners, "L=" + length);
                }
            }

            // the Pinnacle room is a Rect: four corners, none on a pad
            Assert.IsTrue(HazardRegionPresets.TryGet(HazardRegionPresets.PinnacleName, 0, out var room));
            var pads = MemoryLayout.QuadrantCentres(room);
            Assert.AreEqual(4, MemoryLayout.CornerBeamPoints(room, pads, (float)MemorySettings.DefaultPadRadius, (float)HazardDefaults.EdgeInset).Count);
        }

        [TestMethod]
        public void P2SeriesFlat_HoldsEveryRampKeyAtItsEnd_PinnedOverARow_AndValidates()
        {
            var s = GraelSettings.Defaults;
            var (pattern, flat) = s.P2SeriesFlat(250);
            var (rampPattern, ramp) = s.P2Series(250);

            Assert.AreEqual(rampPattern, pattern);
            CollectionAssert.AreEquivalent(new[] { "move", "hold", "rest" }, GraelSettings.WispRampKeys);

            foreach (var key in GraelSettings.WispRampKeys)
            {
                Assert.AreEqual(ramp.GetString(key + "_end"), flat.GetString(key), key);
                Assert.AreEqual(flat.GetString(key), flat.GetString(key + "_end"), key + " flat");
            }

            foreach (var key in ramp.Keys.Where(k => !GraelSettings.WispRampKeys.Any(r => k == r || k == r + "_end")))
                Assert.AreEqual(ramp.GetString(key), flat.GetString(key), key + " unchanged");

            // a row that says otherwise cannot bring a ramp back
            var odd = HazardArgs.Merge(flat, HazardArgs.Parse("move_end=9 hold_end=9 rest_end=9"));
            foreach (var key in GraelSettings.WispRampKeys)
                Assert.AreEqual(flat.GetString(key), odd.GetString(key + "_end"), key);

            Assert.IsNull(WispRules.Validate(Region, flat, pattern));
            Assert.IsNull(WispRules.Validate(Region, HazardArgs.Merge(flat, HazardArgs.Parse(HazardDefaults.Wisps)), pattern));

            // no end key: held at the start value
            var noEnd = GraelSettings.From("pattern=scatter cycles=6 move=2.5 hold=1.5 rest=1.0", null, 5, null, null, 4, null, 20, 3).P2SeriesFlat(250).Args;
            Assert.AreEqual("2.5", noEnd.GetString("move"));
            Assert.AreEqual("2.5", noEnd.GetString("move_end"));
        }

        [TestMethod]
        public void MemoryOpeningLength_IsClampedSoTheLastRoundStaysWithinSixteen()
        {
            var s = GraelSettings.From(null, "len=15 pulse_on=1.0", 5, null, null, 4, null, 20, 3);

            Assert.AreEqual(13, s.MemoryOpeningLength);
            Assert.AreEqual(1, GraelSettings.From(null, "len=0", 5, null, null, 4, null, 20, 3).MemoryOpeningLength);
        }

        [TestMethod]
        public void MemoryRoundArgs_StripEveryEndKey()
        {
            var s = GraelSettings.From(null, "len=4 pulse_on=1.0 pulse_on_end=0.5 answer_timeout=20 answer_timeout_end=10 pad_gap_end=1", 5, null, null, 4, null, 20, 3);
            var args = s.MemoryRoundArgs(4, 300);

            Assert.IsFalse(args.Keys.Any(k => k.EndsWith("_end", System.StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual("20", args.GetString("answer_timeout"));
        }

        // ================================================================ wave 3: level-scaled pct, the new pins

        private static readonly string[] BallSeries = { BallPatterns.Fan, BallPatterns.RingAlt, BallPatterns.Row, BallPatterns.Cross, BallPatterns.Random };

        private static bool FromHead(string pattern) => pattern == BallPatterns.Fan || pattern == BallPatterns.RingAlt;

        private static double Pct(HazardArgs args, string key = "pct") => args.GetDouble(key, double.NaN, -1, 99);

        /// <summary>Every hazard the script starts, as it starts it at <paramref name="level"/>: (name, kind, pattern, args).</summary>
        private static List<(string Name, string Kind, string Pattern, HazardArgs Args)> Started(GraelSettings s, int level)
        {
            var list = new List<(string, string, string, HazardArgs)>();
            var (p2, p2Args) = s.P2Series(level);
            var (p5, p5Args) = s.P5Series(level);
            var (corners, cornersArgs) = s.P4CornersSeries(level);

            list.Add(("p2 wisps", HazardMechanicRegistry.Wisps, p2, p2Args));
            list.Add(("p5 wisps", HazardMechanicRegistry.Wisps, p5, p5Args));
            list.Add(("p4 corners", HazardMechanicRegistry.Zones, corners, cornersArgs));

            foreach (var pattern in BallSeries)
                list.Add(("p4 " + pattern, HazardMechanicRegistry.Balls, pattern, FromHead(pattern) ? WithHead(s.P4Args(pattern, level)) : s.P4Args(pattern, level)));

            return list;
        }

        [TestMethod]
        public void Pct_AtL200_L300_L400_PerAccessor_DefaultClamp()
        {
            var s = GraelSettings.Defaults;

            foreach (var (level, benchmark) in new[] { (200, 0.25), (300, 0.50), (400, 0.75) })
            {
                Assert.AreEqual(benchmark, Pct(s.P2Series(level).Args), 1e-9, $"p2 wisps L{level}");
                Assert.AreEqual(benchmark, Pct(s.P5Series(level).Args), 1e-9, $"p5 wisps L{level}");

                foreach (var pattern in BallSeries)
                    Assert.AreEqual(benchmark, Pct(s.P4Args(pattern, level)), 1e-9, $"{pattern} L{level}");

                var corners = s.P4CornersSeries(level).Args;
                Assert.AreEqual(benchmark * 0.05 / 0.60, Pct(corners), 1e-6, $"corners pct L{level}");
                Assert.AreEqual(benchmark * 0.35 / 0.60, Pct(corners, "pct_end"), 1e-6, $"corners pct_end L{level}");
            }

            Assert.AreEqual(0.60, GraelSettings.BallPct);
            Assert.AreEqual(0.60, GraelSettings.WispPct);
            Assert.AreEqual(0.05, GraelSettings.CornerPct);
            Assert.AreEqual(0.35, GraelSettings.CornerPctEnd);
        }

        [TestMethod]
        public void Pct_CustomClamp_IsTheFightsClamp()
        {
            var s = GraelSettings.Defaults with { HazardPctMin = 0.40, HazardPctMax = 0.50 };

            Assert.AreEqual(0.40, Pct(s.P4Args(BallPatterns.Row, 200)), 1e-9, "raised to the custom min");
            Assert.AreEqual(0.45, Pct(s.P4Args(BallPatterns.Row, 280)), 1e-9, "inside the band: 0.25 + 0.0025 x 80");
            Assert.AreEqual(0.50, Pct(s.P4Args(BallPatterns.Row, 400)), 1e-9, "cut to the custom max");
            Assert.AreEqual(0.50, Pct(s.P2Series(400).Args), 1e-9);
            Assert.AreEqual(0.50 * 0.35 / 0.60, Pct(s.P4CornersSeries(400).Args, "pct_end"), 1e-6);
        }

        [TestMethod]
        public void Pct_ARowCannotOverrideIt()
        {
            var row = HazardArgs.Parse("pct=5");

            foreach (var x in Started(GraelSettings.Defaults, 300))
                Assert.AreNotEqual(5.0, Pct(HazardArgs.Merge(x.Args, row)), x.Name + ": the scaled pct is pinned over the shard row");
        }

        [TestMethod]
        public void Grael_CarriesTheLiveClamp_ReadAtCreation()
        {
            Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(ACE.Server.Pinnacles.PinnacleTunables.HazardPctMinKey, 0.30));
            Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(ACE.Server.Pinnacles.PinnacleTunables.HazardPctMaxKey, 0.60));

            try
            {
                var s = ACE.Server.Pinnacles.PinnacleTunables.Grael();

                Assert.AreEqual(0.30, s.HazardPctMin, 1e-9);
                Assert.AreEqual(0.60, s.HazardPctMax, 1e-9);
                Assert.AreEqual(0.60, Pct(s.P4Args(BallPatterns.Fan, 450)), 1e-9);
                Assert.AreEqual(0.30, Pct(s.P4Args(BallPatterns.Fan, 100)), 1e-9);
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyDouble(ACE.Server.Pinnacles.PinnacleTunables.HazardPctMinKey, ACE.Server.Pinnacles.PinnacleTunables.DefaultHazardPctMin);
                ACE.Server.Managers.PropertyManager.ModifyDouble(ACE.Server.Pinnacles.PinnacleTunables.HazardPctMaxKey, ACE.Server.Pinnacles.PinnacleTunables.DefaultHazardPctMax);
            }

            Assert.AreEqual((0.25, 0.75), (GraelSettings.Defaults.HazardPctMin, GraelSettings.Defaults.HazardPctMax), "the pure defaults are the shipped clamp");
        }

        [TestMethod]
        public void EveryStartedHazard_PassesItsMechanicsValidate_RawAndOverTheShardDefaults()
        {
            var failures = new List<string>();
            var rows = new Dictionary<string, HazardArgs>
            {
                [HazardMechanicRegistry.Wisps] = HazardArgs.Parse(HazardDefaults.Wisps),
                [HazardMechanicRegistry.Balls] = HazardArgs.Parse(HazardDefaults.Balls),
                [HazardMechanicRegistry.Zones] = HazardArgs.Empty,   // zones has no settings row of its own
            };
            var zones = new ZonesMechanic();

            string Validate(string kind, string pattern, HazardArgs args)
                => kind == HazardMechanicRegistry.Wisps ? WispRules.Validate(Region, args, pattern)
                 : kind == HazardMechanicRegistry.Balls ? BallRules.Validate(Region, args, pattern)
                 : zones.Validate(Region, args, pattern);

            foreach (var level in new[] { 0, 200, 300, 400, 500 })
                foreach (var x in Started(GraelSettings.Defaults, level))
                {
                    var raw = Validate(x.Kind, x.Pattern, x.Args);
                    var merged = Validate(x.Kind, x.Pattern, HazardArgs.Merge(x.Args, rows[x.Kind]));

                    if (raw != null) failures.Add($"L{level} {x.Name}: {raw}");
                    if (merged != null) failures.Add($"L{level} {x.Name} merged: {merged}");
                }

            Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));
            Assert.AreEqual(8, Started(GraelSettings.Defaults, 300).Count, "control: two wisps, the corners and five ball series were checked");
        }

        [TestMethod]
        public void DistinctDirs_IsRefusedWithoutItsPins_SoThePinsAreWhatPass()
        {
            // control: the cross without its dirs/cap pins, and the rows with a fixed dir, are refused by the balls Validate
            var s = GraelSettings.Defaults;
            var balls = HazardArgs.Parse(HazardDefaults.Balls);

            StringAssert.Contains(BallRules.Validate(Region, HazardArgs.Merge(s.P4CrossArgs().With("max_in_flight", "3").With("max_in_flight_end", "3"), balls), BallPatterns.Cross), "distinct_dirs");
            StringAssert.Contains(BallRules.Validate(Region, HazardArgs.Merge(s.P4CrossArgs().With("dirs", "ne"), balls), BallPatterns.Cross), "dirs=random");
            StringAssert.Contains(BallRules.Validate(Region, HazardArgs.Merge(s.P4RowArgs().With("dir", "n"), balls), BallPatterns.Row), "dir=random");
            Assert.IsNull(BallRules.Validate(Region, HazardArgs.Merge(s.P4CrossArgs(), balls), BallPatterns.Cross));
        }

        [TestMethod]
        public void NewPins_SurviveARowThatSaysOtherwise()
        {
            var s = GraelSettings.Defaults;
            var odd = HazardArgs.Parse("origin_clear=0 no_overlap=0 distinct_dirs=0 dir=n dirs=ne max_in_flight=9 max_in_flight_end=9");

            Assert.AreEqual("3", HazardArgs.Merge(s.P4FanArgs(), odd).GetString("origin_clear"), "fan clears the head");
            Assert.AreEqual("3", HazardArgs.Merge(s.P4RingArgs(), odd).GetString("origin_clear"), "rings clear the head");

            foreach (var (name, args) in new[] { ("row", s.P4RowArgs()), ("cross", s.P4CrossArgs()) })
            {
                Assert.AreEqual("1", HazardArgs.Merge(args, odd).GetString("no_overlap"), name);
                Assert.AreEqual("1", HazardArgs.Merge(args, odd).GetString("distinct_dirs"), name);
            }

            Assert.AreEqual("random", HazardArgs.Merge(s.P4RowArgs(), odd).GetString("dir"));
            Assert.AreEqual("random", HazardArgs.Merge(s.P4CrossArgs(), odd).GetString("dirs"));
            Assert.AreEqual("2", HazardArgs.Merge(s.P4CrossArgs(), odd).GetString("max_in_flight"));
            Assert.AreEqual("2", HazardArgs.Merge(s.P4CrossArgs(), odd).GetString("max_in_flight_end"));
            Assert.AreEqual("2", HazardArgs.Merge(s.P4RowArgs(), odd).GetString("max_in_flight"), "rows keep 2 -> 3");
            Assert.AreEqual("3", HazardArgs.Merge(s.P4RowArgs(), odd).GetString("max_in_flight_end"));
            Assert.IsFalse(s.P4StreamArgs().Has("origin_clear"), "the stream does not clear the head");

            // review 2026-10-20: the non-row/cross series pin both off, so a row that turns them on cannot refuse them
            foreach (var pattern in new[] { BallPatterns.Fan, BallPatterns.RingAlt, BallPatterns.Random })
            {
                Assert.AreEqual("0", HazardArgs.Merge(s.P4Args(pattern, 300), HazardArgs.Parse("distinct_dirs=1 no_overlap=1")).GetString("distinct_dirs"), pattern);
                Assert.AreEqual("0", HazardArgs.Merge(s.P4Args(pattern, 300), HazardArgs.Parse("distinct_dirs=1 no_overlap=1")).GetString("no_overlap"), pattern);
            }
        }

        [TestMethod]
        public void EveryBallSeries_Validates_OverAShardRowThatTurnsDistinctDirsAndNoOverlapOn()
        {
            // review 2026-10-20 (reproduced: the fan was refused "distinct_dirs=1 only works with the row and cross patterns")
            var row = HazardArgs.Merge(HazardArgs.Parse("distinct_dirs=1 no_overlap=1"), HazardArgs.Parse(HazardDefaults.Balls));
            var failures = new List<string>();

            foreach (var level in new[] { 200, 300, 400 })
                foreach (var x in Started(GraelSettings.Defaults, level).Where(x => x.Kind == HazardMechanicRegistry.Balls))
                {
                    var refusal = BallRules.Validate(Region, HazardArgs.Merge(x.Args, row), x.Pattern);

                    if (refusal != null)
                        failures.Add($"L{level} {x.Name}: {refusal}");
                }

            Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));

            // control: an unpinned fan over the same row IS refused, so the pins are what pass
            StringAssert.Contains(BallRules.Validate(Region, HazardArgs.Merge(WithHead(HazardArgs.Parse(GraelSettings.DefaultP4Balls)), row), BallPatterns.Fan), "distinct_dirs");
        }

        [TestMethod]
        public void HeadFaceYawOffset_IsPi()
        {
            Assert.AreEqual((float)System.Math.PI, GraelData.HeadFaceYawOffset);
            Assert.AreEqual((float)System.Math.PI, GraelData.Script().HeadFaceYawOffset);
        }

        [TestMethod]
        public void VariantPoints_NineOnTheFloor_ClearOfTheHeadColumn()
        {
            Assert.AreEqual(9, GraelData.VariantPoints.Count);
            Assert.AreEqual(9, GraelData.VariantPoints.Select(p => (p.X, p.Y)).Distinct().Count(), "nine distinct points");

            foreach (var p in GraelData.VariantPoints)
            {
                Assert.AreEqual(GraelData.SpawnZ, p.Z, $"({p.X}, {p.Y}) at SpawnZ -47.70");
                var d = System.Math.Sqrt((p.X - GraelData.HeadPoint.X) * (p.X - GraelData.HeadPoint.X) + (p.Y - GraelData.HeadPoint.Y) * (p.Y - GraelData.HeadPoint.Y));
                Assert.IsTrue(d > 5.0, $"({p.X}, {p.Y}) is {d:0.0} m from the head column");
            }

            Assert.AreEqual(-47.70f, GraelData.SpawnZ);
            Assert.IsTrue(GraelData.P3WaveSize <= GraelData.VariantPoints.Count);
            Assert.AreEqual(GraelData.VariantPoints.Count, GraelData.P3FinalEach * GraelData.Variants.Count);
        }

        [TestMethod]
        public void MemoryRoundArgs_NoEndKey_DoesNotRamp()
        {
            var s = GraelSettings.From(null, "len=4 pulse_on=0.8 pulse_off=0.4", 5, null, null, 4, null, 20, 3);

            Assert.AreEqual(0.8, s.MemoryRoundArgs(7, 300).GetDouble("pulse_on", 0, 0, 30), 1e-9);
            Assert.AreEqual(0.4, s.MemoryRoundArgs(7, 300).GetDouble("pulse_off", 0, 0, 30), 1e-9);
        }

        [TestMethod]
        public void MemoryRoundArgs_PadPillar_EveryRound_EvenOverARowThatSaysOtherwise()
        {
            // the P3 memory row ITSELF says pad_pillar=0 pct=0.01 (neither leftover token may survive into the round args)
            var zeroRow = GraelSettings.From(null, "len=4 pad_pillar=0 pct=0.01 pulse_on=1.0 pulse_off=0.5", 5, null, null, 4, null, 20, 3);

            for (var length = zeroRow.MemoryOpeningLength; length < zeroRow.MemoryOpeningLength + GraelSettings.MemoryRounds; length++)
                Assert.AreEqual("1", zeroRow.MemoryRoundArgs(length, 300).GetString("pad_pillar"), "L=" + length);

            // owner 2026-10-25: a fire pillar on the answer pad of each shown colour, pinned whatever the settings string or the shard row says
            var s = GraelSettings.Defaults;
            var row = HazardArgs.Parse("pad_pillar=0 pct=0.01");

            for (var length = s.MemoryOpeningLength; length < s.MemoryOpeningLength + GraelSettings.MemoryRounds; length++)
            {
                Assert.IsTrue(MemorySettings.Read(s.MemoryRoundArgs(length, 300)).PadPillar, "L=" + length);
                Assert.IsTrue(MemorySettings.Read(HazardArgs.Merge(s.MemoryRoundArgs(length, 300), row)).PadPillar, "L=" + length + ": the pin wins over a row");
                Assert.IsFalse(s.MemoryRoundArgs(length, 300).Has("pad_beams"), "the retired key is gone");
            }

            // damage scales with the Knot level like every other Grael hazard, pinned over the row
            foreach (var level in new[] { 200, 300, 400 })
            {
                var expected = s.ScaledPct(level, GraelSettings.MemoryPct);
                var merged = HazardArgs.Merge(s.MemoryRoundArgs(4, level), row);

                Assert.AreEqual(expected, s.MemoryRoundArgs(4, level).GetDouble("pct", 0, 0, 5), 1e-6, "L" + level + " raw");
                Assert.AreEqual(expected, merged.GetDouble("pct", 0, 0, 5), 1e-6, "L" + level + ": the pin wins over a row");
            }

            Assert.AreNotEqual(s.ScaledPct(200, GraelSettings.MemoryPct), s.ScaledPct(400, GraelSettings.MemoryPct), "positive control: the level moves the pct");
            Assert.AreEqual(s.ScaledPct(300, GraelSettings.WispPct), s.ScaledPct(300, GraelSettings.MemoryPct), 1e-9, "the same ratio as a wisp pillar");
        }
    }
}
