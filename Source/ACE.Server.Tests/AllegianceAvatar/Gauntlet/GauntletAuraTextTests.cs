using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The aura's player-facing lines (orchestrator text, 2026-11-26): the placement line to the fighter's audience, the
    /// death line's suffix, and the status line. Each exact text is pinned at the defaults, and again with tuned values so
    /// the numbers are shown to come from the run's tunables. The placement and death lines are read from what the REAL
    /// hooks and run send (AvatarFighterVoice.Sink, the run's TellInside), the status line from the run's own Info.
    /// Reads no PropertyManager key; the voice and aura seams are reset after each test.
    /// </summary>
    [TestClass]
    public class GauntletAuraTextTests
    {
        private readonly List<string> sent = new List<string>();

        [ClassInitialize]
        public static void Init(TestContext _) => GauntletFighterFixtures.EnsureInitialized();

        [TestInitialize]
        public void Setup()
        {
            AvatarFighterVoice.ResetSeams();
            AvatarFighterVoice.Sink = (to, text) => sent.Add(text);
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarFighterVoice.ResetSeams();
            GauntletAura.ResetForTests();
        }

        private static GauntletTuning Tuned => GauntletTuning.Defaults with { FighterAuraRadius = 12.5, FighterAuraDamageRating = 7, FighterAuraResistRating = 13 };

        private const string DefaultPlaced = "The Avatar stands with you. Fight within 30 meters of it for +10 damage rating and +10 damage resistance rating.";
        private const string TunedPlaced = "The Avatar stands with you. Fight within 12.5 meters of it for +7 damage rating and +13 damage resistance rating.";

        // ---- 1. the placement line ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void PlacedLine_ExactText()
        {
            Assert.AreEqual(DefaultPlaced, GauntletAura.PlacedLine(30.0, 10, 10));
            Assert.AreEqual(TunedPlaced, GauntletAura.PlacedLine(12.5, 7, 13));
        }

        [TestMethod]
        public void PlacedLine_IsSentAtSpawnAndAtEveryReturn_WithTheRunsNumbers()
        {
            foreach (var (tuning, expected) in new[] { (GauntletTuning.Defaults, DefaultPlaced), (Tuned, TunedPlaced) })
            {
                sent.Clear();
                var h = new FighterRunHarness { Tuning = tuning };
                h.StartActive();
                Assert.AreEqual(1, sent.Count(t => t == expected), "the first spawn");

                h.Placed.Single().OnDeath(null, DamageType.Slash);
                h.Advance(tuning.FighterRespawnSeconds + 1.5);
                Assert.AreEqual(2, h.Placed.Count, "fixture: respawned");
                Assert.AreEqual(2, sent.Count(t => t == expected), "and the return");
                Assert.AreEqual(0, sent.Count(t => t.StartsWith("The Avatar stands with you.") && t != expected), "never another number");
            }
        }

        [TestMethod]
        public void PlacedLine_NotSentWhileTheAuraIsOff()
        {
            var h = new FighterRunHarness { Tuning = GauntletTuning.Defaults with { FighterAuraDamageRating = 0, FighterAuraResistRating = 0 } };
            h.StartActive();
            Assert.AreEqual(0, sent.Count(t => t.StartsWith("The Avatar stands with you.")), "an aura that gives nothing is not announced");
        }

        // ---- 2. the death line ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void DeathLine_ExactText_AndUnder255()
        {
            Assert.AreEqual("The Avatar has fallen. It returns in 80 seconds. Its blessing fades until it returns.", GauntletRun.FighterDeathLine(80, GauntletTuning.Defaults));
            Assert.AreEqual("The Avatar has fallen. It returns in 80 seconds.", GauntletRun.FighterDeathLine(80, GauntletTuning.Defaults with { FighterAuraRadius = 0 }), "no aura, no fading");
            Assert.IsTrue(GauntletRun.FighterDeathLine(long.MaxValue, GauntletTuning.Defaults).Length < 255, "the longest possible line still fits one chat line");
        }

        [TestMethod]
        public void DeathLine_TheRunTellsTheArena()
        {
            var h = new FighterRunHarness();
            var run = h.StartActive();

            h.Placed.Single().OnDeath(null, DamageType.Slash);
            Assert.AreEqual("The Avatar has fallen. It returns in 60 seconds. Its blessing fades until it returns.", h.Lines.Last(l => l.StartsWith("The Avatar has fallen.")));
        }

        // ---- 3. the status line ------------------------------------------------------------------------------------------------------

        private static GauntletRunInfo Info(GauntletState state, GauntletAuraInfo aura) =>
            new GauntletRunInfo(1, 2, 3, "ow_arwic", 0x10064, state, 4, 275, 3, 90, 12, 9, null, null, aura);

        [TestMethod]
        public void StatusLine_ExactText_StandingAndFallen()
        {
            Assert.AreEqual("Avatar's blessing: +10 damage, +10 damage resistance within 30 m while it stands.",
                GauntletAura.StatusLine(new GauntletAuraInfo(30.0, 10, 10)));
            Assert.AreEqual("Avatar's blessing: +7 damage, +13 damage resistance within 12.5 m while it stands. (fallen - returns in 42s)",
                GauntletAura.StatusLine(new GauntletAuraInfo(12.5, 7, 13, 42)));
            Assert.IsNull(GauntletAura.StatusLine(new GauntletAuraInfo(0, 10, 10)), "radius 0: the aura is off, no line");
            Assert.IsNull(GauntletAura.StatusLine(null));
        }

        [TestMethod]
        public void StatusLine_ShownOnlyWhileTheRunIsLive()
        {
            var aura = new GauntletAuraInfo(30.0, 10, 10);
            const string line = "Avatar's blessing: +10 damage, +10 damage resistance within 30 m while it stands.";

            foreach (var live in new[] { GauntletState.Staging, GauntletState.Active, GauntletState.Intermission })
                CollectionAssert.Contains(GauntletStatusText.Lines(true, Info(live, aura), 0, null, 0).ToArray(), line, live.ToString());

            foreach (var over in new[] { GauntletState.Purchased, GauntletState.Ending, GauntletState.Rewarding, GauntletState.Closed, GauntletState.Aborted })
                Assert.IsFalse(GauntletStatusText.Lines(true, Info(over, aura), 0, null, 0).Any(l => l.StartsWith("Avatar's blessing")), over.ToString());
        }

        [TestMethod]
        public void StatusLine_FromTheRunsInfo_FollowsTheTunablesAndTheRespawn()
        {
            var h = new FighterRunHarness { Tuning = Tuned };
            var run = h.StartActive();

            string Status() => GauntletStatusText.Lines(true, run.Info(h.Now), 0, null, 0).Single(l => l.StartsWith("Avatar's blessing"));

            Assert.AreEqual("Avatar's blessing: +7 damage, +13 damage resistance within 12.5 m while it stands.", Status());

            h.Placed.Single().OnDeath(null, DamageType.Slash);
            Assert.AreEqual("Avatar's blessing: +7 damage, +13 damage resistance within 12.5 m while it stands. (fallen - returns in 60s)", Status());

            h.Advance(18);
            Assert.AreEqual("Avatar's blessing: +7 damage, +13 damage resistance within 12.5 m while it stands. (fallen - returns in 42s)", Status());

            h.Advance(43);
            Assert.AreEqual(2, h.Placed.Count, "fixture: back");
            Assert.AreEqual("Avatar's blessing: +7 damage, +13 damage resistance within 12.5 m while it stands.", Status(), "standing again");
        }
    }
}
