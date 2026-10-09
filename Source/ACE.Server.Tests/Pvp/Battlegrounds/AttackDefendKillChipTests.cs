using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal": the pure decision (<see cref="CrystalKillRules"/>), the landblock-side apply
    /// (<see cref="LivePvpMatchSpaces.ApplyKillEffect"/>) on a real test Creature, and the source pins for the two lines no unit host
    /// can drive (the death report carrying the position, and the chip going through Creature.TakeDamage). The coordinator wiring is
    /// driven end to end in AttackDefendCoordinatorTests ("kill chip and heal").
    /// </summary>
    [TestClass]
    public class AttackDefendKillChipTests
    {
        private const uint Instance = 0x0001_0400;

        private const uint Landblock = 0x003C;

        private static readonly BattlegroundDials Dials = BattlegroundTunables.Defaults;

        /// <summary>The mines plan: Great Hall (110, -70, -6), West Cavern (54.5, -55.4, -30), Pit Hall (89, -164, -42); sequential by default.</summary>
        private static AttackDefendPlan Plan(bool sequential = true) => AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, Dials with { AdSequentialCrystals = sequential }, 2);

        private static Position At(uint cellLow, float x, float y, float z, uint instance = Instance) => new Position((Landblock << 16) | cellLow, x, y, z, 0f, 0f, 0f, 1f, instance);

        private static readonly Position NearGreatHall = At(0x01EE, 110f, -72f, -6f);

        private const int Attacker = CrystalWinCondition.AttackerTeam;

        private const int Defender = CrystalWinCondition.DefenderTeam;

        // ================= the pure decision =================

        [TestMethod]
        public void Fixture_TheMinesPlanHasTheThreeSurveyedSites()
        {
            var plan = Plan();

            Assert.AreEqual(3, plan.Count);
            Assert.AreEqual(("Great Hall", 110f, -70f, -6f), (plan.Crystals[0].Site.Name, plan.Crystals[0].Site.X, plan.Crystals[0].Site.Y, plan.Crystals[0].Site.Z));
            Assert.AreEqual(25.0, Dials.AdKillRange, "the shipped range");
            Assert.AreEqual(1.0, Dials.AdKillChipPercent, "the shipped chip");
            Assert.AreEqual(0.5, Dials.AdKillHealPercent, "the shipped heal");
        }

        [TestMethod]
        public void AttackerKillsDefender_Chips_DefenderKillsAttacker_Heals()
        {
            Assert.AreEqual(new CrystalKillEffect(0, true, 1.0), CrystalKillRules.Decide(Plan(), Dials, Landblock, Instance, Attacker, Defender, NearGreatHall));
            Assert.AreEqual(new CrystalKillEffect(0, false, 0.5), CrystalKillRules.Decide(Plan(), Dials, Landblock, Instance, Defender, Attacker, NearGreatHall));
        }

        [TestMethod]
        public void SameSide_OrAnUnknownTeam_DoesNothing()
        {
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials, Landblock, Instance, Attacker, Attacker, NearGreatHall), "attacker teamkill");
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials, Landblock, Instance, Defender, Defender, NearGreatHall), "defender teamkill");
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials, Landblock, Instance, 2, Defender, NearGreatHall), "no third team");
        }

        /// <summary>The range is measured from the victim's death to the vulnerable crystal, 3D, inclusive at the edge.</summary>
        [TestMethod]
        public void Range_IsInclusive_AndThreeDimensional()
        {
            var plan = Plan();

            Assert.IsNotNull(CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, At(0x01EE, 110f, -95f, -6f)), "exactly 25 m: counts");
            Assert.IsNull(CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, At(0x01EE, 110f, -95.1f, -6f)), "25.1 m: does not");

            // 20 m across and 20 m up: 28.3 m in 3D although the 2D distance is 20.
            Assert.IsNull(CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, At(0x01EE, 110f, -90f, 14f)), "vertical distance counts");

            Assert.AreEqual(25.0, CrystalKillRules.DistanceToSite(At(0x01EE, 110f, -95f, -6f), plan.Crystals[0].Site, Landblock, Instance), 1e-4);
        }

        [TestMethod]
        public void ADeathInAnotherInstance_OrWithNoPosition_DoesNothing()
        {
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials, Landblock, Instance, Attacker, Defender, At(0x01EE, 110f, -72f, -6f, Instance + 1)));
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials, Landblock, Instance, Attacker, Defender, null));
        }

        [TestMethod]
        public void Sequential_OnlyTheVulnerableCrystalCounts()
        {
            var plan = Plan();
            var nearWestCavern = At(0x0161, 56f, -57f, -30f);

            Assert.IsNull(CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, nearWestCavern), "the West Cavern is sealed while the Great Hall stands");

            plan.Sequence.MarkDestroyed(0);

            Assert.AreEqual(new CrystalKillEffect(1, true, 1.0), CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, nearWestCavern));
            Assert.IsNull(CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, NearGreatHall), "a fallen crystal is never chipped or healed");

            plan.Sequence.MarkDestroyed(1);
            plan.Sequence.MarkDestroyed(2);

            Assert.IsNull(CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Defender, Attacker, At(0x013E, 89f, -165f, -42f)), "none left");
        }

        /// <summary>Any-order play (and a one-crystal plan, which has no sequence) picks the nearest planned crystal within range.</summary>
        [TestMethod]
        public void AnyOrder_PicksTheNearestCrystalInRange()
        {
            var plan = Plan(sequential: false);
            Assert.IsNull(plan.Sequence, "fixture: any-order play");

            Assert.AreEqual(1, CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, At(0x0161, 56f, -57f, -30f))?.CrystalIndex);
            Assert.AreEqual(2, CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Defender, Attacker, At(0x013E, 89f, -165f, -42f))?.CrystalIndex);
            Assert.IsNull(CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, At(0x01DD, 80f, -120f, -6f)), "near none of them");

            var one = AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, Dials with { AdCrystalCount = 1 }, 2);
            Assert.IsNull(one.Sequence, "fixture: one crystal has no sequence");
            Assert.AreEqual(0, CrystalKillRules.Decide(one, Dials, Landblock, Instance, Attacker, Defender, NearGreatHall)?.CrystalIndex);
        }

        [TestMethod]
        [DataRow(0.0)]
        [DataRow(-1.0)]
        [DataRow(double.NaN)]
        [DataRow(double.PositiveInfinity)]
        public void APercentThatIsOffOrInvalid_DoesNothing(double percent)
        {
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials with { AdKillChipPercent = percent }, Landblock, Instance, Attacker, Defender, NearGreatHall), "chip");
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials with { AdKillHealPercent = percent }, Landblock, Instance, Defender, Attacker, NearGreatHall), "heal");
        }

        [TestMethod]
        [DataRow(0.0)]
        [DataRow(-5.0)]
        [DataRow(double.NaN)]
        [DataRow(double.PositiveInfinity)]
        public void ARangeThatIsOffOrInvalid_CountsNoKill(double range)
        {
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials with { AdKillRange = range }, Landblock, Instance, Attacker, Defender, At(0x01EE, 110f, -70f, -6f)));
        }

        /// <summary>
        /// Any-order play skips a fallen crystal: with crystal 0 destroyed and a death in range of crystals 0 AND 1 (nearer 0), the effect
        /// lands on crystal 1. Without the predicate the nearer, fallen crystal 0 would be picked and the kill silently swallowed.
        /// </summary>
        [TestMethod]
        public void AnyOrder_AFallenCrystalIsSkipped_TheStandingOneInRangeTakesTheKill()
        {
            // Two crystals 10 m apart, both inside 25 m of the death point: one plan, same sites as the mines for 0 and a stand-in for 1.
            var plan = new AttackDefendPlan(new[]
            {
                new PlannedCrystal(0, new BattlegroundCrystalSite("A", 0x01EE, 110f, -70f, -6f), 1000, "north"),
                new PlannedCrystal(1, new BattlegroundCrystalSite("B", 0x01EF, 110f, -80f, -6f), 1000, "south"),
            }, 1000, 2);
            Assert.IsNull(plan.Sequence, "fixture: any-order play");

            var death = At(0x01EE, 110f, -72f, -6f); // 2 m from crystal 0, 8 m from crystal 1

            Assert.AreEqual(0, CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, death)?.CrystalIndex, "fixture: crystal 0 is the nearer");
            Assert.AreEqual(1, CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, death, i => i != 0)?.CrystalIndex, "crystal 0 fallen: crystal 1 takes it");
            Assert.IsNull(CrystalKillRules.Decide(plan, Dials, Landblock, Instance, Attacker, Defender, death, _ => false), "none standing");
        }

        /// <summary>Sequential play honours the predicate too (belt and braces: the cursor never names a fallen crystal).</summary>
        [TestMethod]
        public void Sequential_ACursorReportedFallen_DoesNothing()
        {
            Assert.IsNull(CrystalKillRules.Decide(Plan(), Dials, Landblock, Instance, Attacker, Defender, NearGreatHall, _ => false));
        }

        [TestMethod]
        public void APercentAboveOneHundred_CountsAsOneHundred()
        {
            Assert.AreEqual(100.0, CrystalKillRules.Decide(Plan(), Dials with { AdKillChipPercent = 250.0 }, Landblock, Instance, Attacker, Defender, NearGreatHall)?.Percent);
        }

        [TestMethod]
        public void Amount_IsAPercentOfMax_RoundedWithAFloorOfOne()
        {
            Assert.AreEqual(100, CrystalKillRules.Amount(10000, 1.0), "1% of 10000");
            Assert.AreEqual(50, CrystalKillRules.Amount(10000, 0.5), "0.5% of 10000");
            Assert.AreEqual(1, CrystalKillRules.Amount(50, 0.5), "0.25 is raised to the floor of 1");
            Assert.AreEqual(1, CrystalKillRules.Amount(250, 0.5), "1.25 rounds to 1");
            Assert.AreEqual(2, CrystalKillRules.Amount(300, 0.5), "1.5 rounds away from zero");
            Assert.AreEqual(10, CrystalKillRules.Amount(10, 100.0), "never more than the maximum");
            Assert.AreEqual(0, CrystalKillRules.Amount(0, 1.0), "no maximum, nothing");
            Assert.AreEqual(0, CrystalKillRules.Amount(100, 0.0), "no percent, nothing");
        }

        // ================= the landblock-side apply, on a real test Creature =================

        private static Creature Crystal(uint maxHealth = 990)
        {
            // CreateDefender adds endurance / 2 = 10 to the authored health: 990 authored is a 1000-point crystal.
            var crystal = TestCreatures.CreateDefender(maxHealth: maxHealth);
            Assert.IsTrue(crystal.SetBattlegroundObjective(new BattlegroundObjectiveTag(Guid.NewGuid(), 0, Defender)));
            Assert.AreEqual(1000u, crystal.Health.MaxValue, "fixture: a 1000-point crystal");
            return crystal;
        }

        [TestMethod]
        public void Apply_Chip_TakesThePercentOfMax_ThroughTakeDamage_CreditedToTheKiller()
        {
            var crystal = Crystal();
            var killer = TestCreatures.CreateAttacker();

            Assert.IsNotNull(LivePvpMatchSpaces.ApplyKillEffect(crystal, new CrystalKillEffect(0, true, 1.0), killer));

            Assert.AreEqual(990u, crystal.Health.Current, "1% of 1000 off");
            Assert.AreEqual(killer.Guid, crystal.DamageHistory.LastDamager?.Guid, "TakeDamage recorded the killer as the last damager (the credit a chip to 0 reports)");
        }

        [TestMethod]
        public void Apply_Heal_RestoresThePercentOfMax_CappedAtMax()
        {
            var crystal = Crystal();
            crystal.UpdateVital(crystal.Health, 900u);

            LivePvpMatchSpaces.ApplyKillEffect(crystal, new CrystalKillEffect(0, false, 0.5), null);
            Assert.AreEqual(905u, crystal.Health.Current, "0.5% of 1000 back");

            crystal.UpdateVital(crystal.Health, 998u);
            LivePvpMatchSpaces.ApplyKillEffect(crystal, new CrystalKillEffect(0, false, 0.5), null);
            Assert.AreEqual(1000u, crystal.Health.Current, "never above the maximum");
        }

        [TestMethod]
        public void Apply_AnUntaggedCreature_OrACrystalAtZero_IsLeftAlone()
        {
            var untagged = TestCreatures.CreateDefender(maxHealth: 990);
            Assert.IsNull(LivePvpMatchSpaces.ApplyKillEffect(untagged, new CrystalKillEffect(0, true, 50.0), null));
            Assert.AreEqual(1000u, untagged.Health.Current);

            var fallen = Crystal();
            fallen.UpdateVital(fallen.Health, 0u);
            Assert.IsNull(LivePvpMatchSpaces.ApplyKillEffect(fallen, new CrystalKillEffect(0, false, 50.0), null), "a destroyed crystal is never revived");
            Assert.AreEqual(0u, fallen.Health.Current);

            Assert.IsNull(LivePvpMatchSpaces.ApplyKillEffect(null, new CrystalKillEffect(0, true, 1.0), null));
        }

        /// <summary>
        /// A chip to 0 goes through the crystal's own death path: TakeDamage runs OnDeath and Die, and Die reports the destruction to the
        /// match exactly as a killing hit does (Creature_BattlegroundObjective.ReportBattlegroundObjectiveDestroyed), carrying the crystal's
        /// match, its index and nothing else from this code. The rest of Die needs a world this host does not have, so anything it throws
        /// after the report is tolerated here; the report is the observable that matters.
        /// </summary>
        [TestMethod]
        public void Apply_ChipToZero_ReportsTheDestructionThroughTheNormalDeathPath()
        {
            PvpMatchManager.ClearForTests();

            try
            {
                var matchId = Guid.NewGuid();
                var crystal = TestCreatures.CreateDefender(maxHealth: 990);
                Assert.IsTrue(crystal.SetBattlegroundObjective(new BattlegroundObjectiveTag(matchId, 2, Defender)));
                crystal.UpdateVital(crystal.Health, 5u);

                try
                {
                    LivePvpMatchSpaces.ApplyKillEffect(crystal, new CrystalKillEffect(2, true, 1.0), TestCreatures.CreateAttacker());
                }
                catch (Exception)
                {
                    // Die's tail (physics, motion, the death chain) needs a live world; the report already happened above it.
                }

                Assert.AreEqual(0u, crystal.Health.Current, "the 10-point chip took the last 5");
                Assert.IsTrue(PvpMatchManager.TryDequeue(out var intent), "Die reported the destruction");
                Assert.AreEqual(PvpIntentKind.ObjectiveDestroyed, intent.Kind);
                Assert.AreEqual(matchId, intent.MatchId);
                Assert.AreEqual(2, intent.Count, "the crystal's own plan index");
                Assert.IsFalse(PvpMatchManager.TryDequeue(out _), "reported once");
            }
            finally
            {
                PvpMatchManager.ClearForTests();
            }
        }

        // ================= source pins =================

        private static string RepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "could not find the repo root by walking up from the test directory");

            return File.ReadAllText(Path.Combine(new[] { dir.FullName, "Source", "ACE.Server" }.Concat(relative).ToArray())).Replace("\r\n", "\n");
        }

        /// <summary>Null when Player.LatchPvpMatchDeath reports the death WITH the victim's position (the range check's only input).</summary>
        private static string DeathReportFault(string arena)
            => arena.Contains("PvpMatchManager.Report(PvpMatchManager.Death(Guid.Full, binding.Match.MatchId, DateTime.UtcNow, killerId, Location));")
                ? null
                : "the match death is not reported with the victim's Location";

        [TestMethod]
        public void TheMatchDeathReport_CarriesTheVictimsPosition()
        {
            var arena = RepoFile("WorldObjects", "Player_PvpArena.cs");

            Assert.IsNull(DeathReportFault(arena));
            Assert.IsNotNull(DeathReportFault(arena.Replace("killerId, Location));", "killerId));")), "the four-argument report (no position) must fail the check");
        }

        /// <summary>The five-argument Death intent keeps a COPY of the position (the player's Location object keeps moving after it is queued).</summary>
        [TestMethod]
        public void TheDeathIntent_CopiesThePosition()
        {
            var where = At(0x01EE, 110f, -72f, -6f);
            var intent = PvpMatchManager.Death(1, Guid.NewGuid(), DateTime.UtcNow, 2, where);

            Assert.AreNotSame(where, intent.DeathPosition);
            Assert.AreEqual(where.ToString(), intent.DeathPosition.ToString());
            Assert.AreEqual(Instance, intent.DeathPosition.Instance);

            where.PositionX = 0f;
            Assert.AreEqual(110f, intent.DeathPosition.PositionX, "a later move of the source does not reach the queued intent");

            Assert.IsNull(PvpMatchManager.Death(1, Guid.NewGuid(), DateTime.UtcNow, 2, null).DeathPosition);
        }

        /// <summary>
        /// Null when the apply chips through Creature.TakeDamage and has no destruction path of its own: it never calls Die or OnDeath and
        /// never reports ObjectiveDestroyed itself (the TakeDamage it calls does both, at 0).
        /// </summary>
        private static string ApplyFault(string body)
        {
            if (!body.Contains("crystal.TakeDamage(killer, DamageType.Undef, amount);"))
                return "the chip does not go through Creature.TakeDamage";

            foreach (var forbidden in new[] { "Die(", "OnDeath(", "ObjectiveDestroyed(", "ReportBattlegroundObjectiveDestroyed" })
            {
                if (body.Contains(forbidden))
                    return $"the apply has its own destruction path ({forbidden})";
            }

            return null;
        }

        private static string ApplyBody()
        {
            var text = RepoFile("Pvp", "PvpLiveAdapters.cs");
            var start = text.IndexOf("internal static string ApplyKillEffect(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "ApplyKillEffect not found");
            var end = text.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "the end of ApplyKillEffect not found");
            return text.Substring(start, end - start);
        }

        /// <summary>
        /// Null when AdjustObjective calls ApplyKillEffect only from inside the delegate it hands landblock.EnqueueAction, i.e. on the
        /// crystal's own landblock thread, never directly on the world thread that calls the seam.
        /// </summary>
        private static string EnqueueFault(string body)
        {
            var enqueue = body.IndexOf("landblock.EnqueueAction(new ActionEventDelegate(() =>", StringComparison.Ordinal);
            var apply = body.IndexOf("ApplyKillEffect(", StringComparison.Ordinal);

            if (enqueue < 0)
                return "AdjustObjective does not enqueue on the landblock";

            if (apply < 0)
                return "AdjustObjective does not call ApplyKillEffect";

            if (apply < enqueue || body.IndexOf("ApplyKillEffect(", apply + 1, StringComparison.Ordinal) >= 0)
                return "ApplyKillEffect is called outside the enqueued delegate";

            return null;
        }

        private static string AdjustBody()
        {
            var text = RepoFile("Pvp", "PvpLiveAdapters.cs");
            var start = text.IndexOf("public void AdjustObjective(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "AdjustObjective not found");
            var end = text.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "the end of AdjustObjective not found");
            return text.Substring(start, end - start);
        }

        [TestMethod]
        public void TheAdjust_AppliesOnlyInsideTheLandblockQueue()
        {
            var body = AdjustBody();

            Assert.IsNull(EnqueueFault(body));

            // A direct call on the calling (world) thread before the enqueue must fail the check.
            var direct = body.Replace("            landblock.EnqueueAction(", "            ApplyKillEffect(null, effect, null);\n\n            landblock.EnqueueAction(");
            Assert.IsNotNull(EnqueueFault(direct), "a direct call must fail the check");
        }

        [TestMethod]
        public void TheApply_ChipsThroughTakeDamage_WithNoParallelDestructionPath()
        {
            var body = ApplyBody();

            Assert.IsNull(ApplyFault(body));
            Assert.IsNotNull(ApplyFault(body.Replace("crystal.TakeDamage(killer, DamageType.Undef, amount);", "crystal.UpdateVitalDelta(crystal.Health, -amount);")), "a raw vital write must fail the check");
            Assert.IsNotNull(ApplyFault(body + "\ncrystal.Die();"), "a direct Die must fail the check");
        }
    }
}
