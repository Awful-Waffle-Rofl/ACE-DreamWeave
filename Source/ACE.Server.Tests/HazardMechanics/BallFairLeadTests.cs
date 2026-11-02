using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// fair_lead=0 (opt-in): no fair-lead floor and no fan sidestep floor, and the lead may be 0, so the balls move the
    /// moment they appear (owner, 2026-10-19: "no hover delay" on Grael's fan). The default, fair_lead=1, is unchanged.
    /// </summary>
    [TestClass]
    public class BallFairLeadTests
    {
        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardRegion Huge() => HazardRegion.Rect("huge", 0xA9B4, 0, true, 1f, 191f, 1f, 191f, HazardGroundMode.Terrain, 0, null, 0, null, 2, 6);

        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static BallParams P(string args, string pattern) => BallRules.Read(Merged(args), pattern);

        [TestMethod]
        public void FairLeadOff_FanLeadIsZero_AtFiveAndTwentyMetres()
        {
            var p = P("fair_lead=0 lead=0 spread=90", BallPatterns.Fan);

            Assert.IsFalse(p.FairLeadFloor);
            Assert.AreEqual(0.0, p.Lead, "lead=0 is no longer clamped up to 0.5");
            Assert.AreEqual(0.0, BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, p, 5.0));
            Assert.AreEqual(0.0, BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, p, 20.0));
            Assert.AreEqual(0.0, BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, p, null), "no distance yet: still 0");
            Assert.AreEqual(0.0, BallRules.FairLead(Pinnacle(), BallPatterns.Fan, p));
            Assert.AreEqual(0.0, BallRules.EffectiveLead(Pinnacle(), BallPatterns.Fan, p));

            // a lead given explicitly is used as written, not raised
            Assert.AreEqual(1.25, BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, P("fair_lead=0 lead=1.25 spread=90", BallPatterns.Fan), 20.0), 1e-9);
        }

        [TestMethod]
        public void Default_IsUnchanged_FloorAndSidestepStillApply()
        {
            var p = P("spread=90", BallPatterns.Fan);

            Assert.IsTrue(p.FairLeadFloor);
            Assert.AreEqual(BallRules.FanLead(90, 20, p.Reach, p.EscapeSpeed), BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, p, 20.0), 1e-9, "the sidestep time at 20 m");
            Assert.IsTrue(BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, p, 20.0) > 2.0);
            Assert.AreEqual(2.0, BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, p, 0.5), 1e-9, "close in, the lead arg floors it");

            // lead=0 is clamped to the 0.5 s minimum, as before, and fair_lead=1 written out is the same as the default
            Assert.AreEqual(HazardDefaults.BallsMinLead, P("lead=0", BallPatterns.Fan).Lead);
            Assert.AreEqual(HazardDefaults.BallsMinLead, P("lead=0 lead_end=0", BallPatterns.Row).LeadEnd);
            Assert.AreEqual(BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, p, 20.0), BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, P("fair_lead=1 spread=90", BallPatterns.Fan), 20.0));

            // the other patterns still get the fair-lead floor
            var row = P("lead=0", BallPatterns.Row);
            Assert.IsTrue(BallRules.EffectiveLead(Pinnacle(), BallPatterns.Row, row) >= HazardDefaults.BallsMinLead);
            Assert.AreEqual(BallRules.FairLead(Pinnacle(), BallPatterns.Row, row), BallRules.LeadFor(Pinnacle(), BallPatterns.Row, row, null));
        }

        [TestMethod]
        public void FairLeadOff_AppliesToEveryPattern_WhenOptedIn()
        {
            foreach (var pattern in new[] { BallPatterns.Row, BallPatterns.Cross, BallPatterns.RingOut, BallPatterns.RingIn, BallPatterns.Random })
                Assert.AreEqual(0.0, BallRules.LeadFor(Pinnacle(), pattern, P("fair_lead=0 lead=0", pattern), null), pattern);
        }

        [TestMethod]
        public void FairLeadOff_LeadEndRamp_IsUsedAsWritten()
        {
            // pinned to 0 at both ends, every wave is 0
            var flat = P("fair_lead=0 lead=0 lead_end=0 waves=6", BallPatterns.Fan);

            for (var wave = 1; wave <= 6; wave++)
                Assert.AreEqual(0.0, BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, BallRules.ForWave(flat, wave), 20.0), "wave " + wave);

            // control: a lead_end above 0 DOES ramp the lead up, which is why Grael pins lead_end=0 too
            var ramp = P("fair_lead=0 lead=0 lead_end=3 waves=4", BallPatterns.Fan);
            Assert.AreEqual(0.0, BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, BallRules.ForWave(ramp, 1), 20.0));
            Assert.AreEqual(3.0, BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, BallRules.ForWave(ramp, 4), 20.0), 1e-9);
        }

        [TestMethod]
        public void Announcement_NoLead_DropsTheMoveClause_NeverSaysZeroSeconds()
        {
            // a real lead is announced as before
            Assert.AreEqual(". They move in 2 s.", BallRules.MoveClause(2.0, ""));
            Assert.AreEqual(". They move in 7.9 s; pillars mark the gaps.", BallRules.MoveClause(7.94, "; pillars mark the gaps"));
            Assert.AreEqual(". They move in 0.1 s.", BallRules.MoveClause(BallRules.AnnouncedLeadMin, ""), "the threshold itself still announces");

            // under 0.05 s the clause is dropped; the sentence still ends, and a marker note stays
            foreach (var lead in new[] { 0.0, 0.01, 0.049 })
            {
                Assert.AreEqual(".", BallRules.MoveClause(lead, ""), "lead " + lead);
                Assert.AreEqual("; pillars mark the gaps.", BallRules.MoveClause(lead, "; pillars mark the gaps"), "lead " + lead);
                Assert.AreEqual(".", BallRules.MoveClause(lead, null), "lead " + lead);
                StringAssert.DoesNotMatch(BallRules.MoveClause(lead, ""), new System.Text.RegularExpressions.Regex("move in"));
            }

            // the zero-lead fan as Grael runs it announces no move time
            var fan = P("fair_lead=0 lead=0 lead_end=0 spread=90", BallPatterns.Fan);
            Assert.AreEqual(".", BallRules.MoveClause(BallRules.LeadFor(Pinnacle(), BallPatterns.Fan, fan, 20.0), ""));
        }

        [TestMethod]
        public void Announcement_BothWaveAnnouncements_UseTheMoveClause()
        {
            // source scan: the sequential and the cadence announcement both end with MoveClause, and neither still
            // formats "They move in" itself (which would say "They move in 0 s" for a zero lead)
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);

            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Source", "ACE.Server", "HazardMechanics", "RollingBallMechanic.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "Could not find Source/ACE.Server/HazardMechanics/RollingBallMechanic.cs by walking up from " + System.AppContext.BaseDirectory);

            var code = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "Source", "ACE.Server", "HazardMechanics", "RollingBallMechanic.cs"));

            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(code, @"Rolling Death, wave \{").Count, "control: the two wave announcements are there");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(code, @"\{BallRules\.MoveClause\(lead, note\)\}").Count, "the sequential announcement");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(code, @"\{BallRules\.MoveClause\(waveLead, note\)\}").Count, "the cadence announcement");
            Assert.IsFalse(code.Contains("They move in", System.StringComparison.Ordinal));
        }

        [TestMethod]
        public void Validate_FairLeadOff_SkipsTheFairnessRefusal_ButNotTheGeometry()
        {
            // a 120 degree fan across a 190 m field needs more than the 30 s cap to sidestep: refused by default
            StringAssert.Contains(BallRules.Validate(Huge(), Merged("spread=120"), BallPatterns.Fan), "too wide to sidestep");
            Assert.IsNull(BallRules.Validate(Huge(), Merged("spread=120 fair_lead=0 lead=0"), BallPatterns.Fan), "fair_lead=0 is not held to the fairness cap");

            // geometry is still refused: an origin outside the region
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("fair_lead=0 lead=0 cx=5000 cy=5000"), BallPatterns.Fan), "fan origin");

            // and a typo is refused, not read as the default
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("fair_lead=maybe"), BallPatterns.Fan), "fair_lead='maybe'");

            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("fair_lead=0 lead=0 spread=90"), BallPatterns.Fan));
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("fair_lead=0 lead=0"), BallPatterns.Row));
        }
    }
}
