using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Hunter's Echo (Archer T3): the pure rules in HuntersEchoMath and the handler's definition and readout. The live
    /// summon, attack and absorb need a Player, whose static initializer cannot run under the test host, so those are
    /// pinned by HuntersEchoWiringSourceScanTests and the in-game checklist instead.
    /// </summary>
    [TestClass]
    public class HuntersEchoMathTests
    {
        private static readonly string[] SeededDoubles =
        {
            "class_ability_huntersecho_duration_seconds",
            "class_ability_huntersecho_affinity_cap_seconds",
        };

        // ------------------------------------------------------------ duration

        [TestMethod]
        public void Duration_NoAffinity_IsTheBase()
        {
            Assert.AreEqual(6.0, HuntersEchoMath.Duration(6.0, 1.0, 2.0), 1e-9);
        }

        [TestMethod]
        public void Duration_AffinityAddsUpToTheCap()
        {
            // x1.2 on 6 s adds 1.2 s, under the 2 s cap
            Assert.AreEqual(7.2, HuntersEchoMath.Duration(6.0, 1.2, 2.0), 1e-9);

            // x1.5 would add 3 s; the cap holds it to 2 s, so 8 s is the most it can be
            Assert.AreEqual(8.0, HuntersEchoMath.Duration(6.0, 1.5, 2.0), 1e-9);
            Assert.AreEqual(2.0, HuntersEchoMath.AddedSeconds(6.0, 1.5, 2.0), 1e-9);
        }

        [TestMethod]
        public void Duration_CapOfZeroIsUncapped()
        {
            Assert.AreEqual(9.0, HuntersEchoMath.Duration(6.0, 1.5, 0.0), 1e-9);
        }

        [TestMethod]
        public void Duration_MultiplierBelowOneIsFlooredAndANonPositiveBaseIsZero()
        {
            Assert.AreEqual(6.0, HuntersEchoMath.Duration(6.0, 0.0, 2.0), 1e-9, "a 0.0 multiplier must not shorten the base");
            Assert.AreEqual(6.0, HuntersEchoMath.Duration(6.0, 0.5, 2.0), 1e-9);
            Assert.AreEqual(0.0, HuntersEchoMath.Duration(0.0, 1.5, 2.0), 1e-9);
            Assert.AreEqual(0.0, HuntersEchoMath.Duration(-1.0, 1.5, 2.0), 1e-9);
        }

        // ------------------------------------------------------------ cooldown

        [TestMethod]
        public void CooldownReady_ZeroCooldownOrNoPriorTrigger_AlwaysReady()
        {
            Assert.IsTrue(HuntersEchoMath.CooldownReady(100.0, 99.9, 0.0));
            Assert.IsTrue(HuntersEchoMath.CooldownReady(100.0, 0.0, 30.0));
        }

        [TestMethod]
        public void CooldownReady_WaitsTheFullCooldown()
        {
            Assert.IsFalse(HuntersEchoMath.CooldownReady(100.0, 80.0, 30.0));
            Assert.IsTrue(HuntersEchoMath.CooldownReady(110.0, 80.0, 30.0), "exactly the cooldown is ready");
            Assert.IsTrue(HuntersEchoMath.CooldownReady(200.0, 80.0, 30.0));
        }

        // ------------------------------------------------------------ the moved cloak roll gate

        /// <summary>
        /// THE EQUIVALENCE THE MOVE RESTS ON. The roll used to run after the health write, gated on health above zero;
        /// it now runs before it. For a player who cannot reserve an Echo absorb, the new pre-write gate must make the
        /// identical decision on every (health, hit) pair, given that the write clamps at zero (UpdateVitalDelta).
        /// </summary>
        [TestMethod]
        public void ShouldRollCloakProc_WithoutReserve_EqualsThePostWriteGate_Exhaustive()
        {
            for (uint health = 0; health <= 60; health++)
            {
                for (uint amount = 0; amount <= 60; amount++)
                {
                    var afterWrite = health > amount ? health - amount : 0u;

                    Assert.AreEqual(afterWrite > 0, HuntersEchoMath.ShouldRollCloakProc(health, amount, false),
                        $"health {health}, hit {amount}");
                }
            }
        }

        /// <summary>The spell-projectile twin: the write is Math.Round(damage), so the gate compares against that.</summary>
        [TestMethod]
        public void ShouldRollCloakProc_SpellOverload_WithoutReserve_EqualsThePostWriteGate()
        {
            for (uint health = 0; health <= 40; health++)
            {
                for (var damage = 0.0; damage <= 40.0; damage += 0.25)
                {
                    var written = (uint)Math.Round(damage);
                    var afterWrite = health > written ? health - written : 0u;

                    Assert.AreEqual(afterWrite > 0, HuntersEchoMath.ShouldRollCloakProc(health, damage, false),
                        $"health {health}, damage {damage}");
                }
            }
        }

        [TestMethod]
        public void ShouldRollCloakProc_WithReserve_AlsoRollsOnALethalHit()
        {
            Assert.IsFalse(HuntersEchoMath.ShouldRollCloakProc(100u, 100u, false), "control: a lethal hit does not roll without the Echo");
            Assert.IsTrue(HuntersEchoMath.ShouldRollCloakProc(100u, 100u, true));
            Assert.IsTrue(HuntersEchoMath.ShouldRollCloakProc(100u, 5000u, true));
            Assert.IsTrue(HuntersEchoMath.ShouldRollCloakProc(100u, 150.0, true));
        }

        /// <summary>
        /// F3: the floor-aware spell gate equals the old post-write target.IsAlive for EVERY (health, bolt, floor)
        /// triple, against an independent model of Creature.UpdateVital: clamp at zero, then a lower value is raised to
        /// min(current, floor). Floor 0 is a Player (never floored) and must equal the floor-free overload.
        /// </summary>
        [TestMethod]
        public void ShouldRollCloakProc_FloorAware_WithoutReserve_EqualsThePostWriteGate_Exhaustive()
        {
            for (uint health = 0; health <= 30; health++)
            {
                for (uint floor = 0; floor <= 12; floor++)
                {
                    for (var damage = 0.0; damage <= 40.0; damage += 0.5)
                    {
                        var written = (long)Math.Round(damage);
                        var clamped = (uint)Math.Max(0L, health - written);

                        // the floor rule, written out independently of Creature.ClampToHealthFloor
                        var afterWrite = floor == 0 || clamped >= health ? clamped : Math.Max(clamped, Math.Min(health, floor));

                        Assert.AreEqual(afterWrite > 0, HuntersEchoMath.ShouldRollCloakProc(health, damage, floor, false),
                            $"health {health}, damage {damage}, floor {floor}");

                        if (floor == 0)
                            Assert.AreEqual(HuntersEchoMath.ShouldRollCloakProc(health, damage, false), HuntersEchoMath.ShouldRollCloakProc(health, damage, 0u, false),
                                $"floor 0 must equal the floor-free gate (health {health}, damage {damage})");
                    }
                }
            }
        }

        [TestMethod]
        public void ShouldRollCloakProc_FloorAware_FlooredBossStillRollsOnAnOverkill()
        {
            Assert.IsFalse(HuntersEchoMath.ShouldRollCloakProc(100u, 500.0, false), "control: the floor-free gate refuses an overkill");
            Assert.IsTrue(HuntersEchoMath.ShouldRollCloakProc(100u, 500.0, 10u, false), "a floored creature survives the overkill, so the old IsAlive gate rolled");
            Assert.IsTrue(HuntersEchoMath.ShouldRollCloakProc(100u, 500.0, 0u, true), "a reserve always rolls");
        }

        /// <summary>F10: a refreshed Echo within RepositionSkipDistance of its owner is not moved.</summary>
        [TestMethod]
        public void ShouldReposition_SkipsWithinOneMetre()
        {
            Assert.AreEqual(1.0, HuntersEchoMath.RepositionSkipDistance, 1e-9);
            Assert.IsFalse(HuntersEchoMath.ShouldReposition(0.0));
            Assert.IsFalse(HuntersEchoMath.ShouldReposition(0.99));
            Assert.IsFalse(HuntersEchoMath.ShouldReposition(1.0), "exactly the skip distance counts as on the owner");
            Assert.IsTrue(HuntersEchoMath.ShouldReposition(1.01));
            Assert.IsTrue(HuntersEchoMath.ShouldReposition(39.0));
            Assert.IsTrue(HuntersEchoMath.ShouldReposition(double.NaN), "an unknown distance moves rather than strands the Echo");
        }

        /// <summary>NEW (refresh): the Echo is moved back onto the owner only in the same landblock AND instance.</summary>
        [TestMethod]
        public void RefreshAction_RepositionsOnlyInTheSameLandblockAndInstance()
        {
            Assert.AreEqual(EchoRefresh.Reposition, HuntersEchoMath.RefreshAction(true, true));
            Assert.AreEqual(EchoRefresh.Replace, HuntersEchoMath.RefreshAction(true, false));
            Assert.AreEqual(EchoRefresh.Replace, HuntersEchoMath.RefreshAction(false, true));
            Assert.AreEqual(EchoRefresh.Replace, HuntersEchoMath.RefreshAction(false, false));
        }
        [TestMethod]
        public void ShouldAbsorb_NeedsBothTheWinningRollAndTheReserve()
        {
            Assert.IsTrue(HuntersEchoMath.ShouldAbsorb(true, true));
            Assert.IsFalse(HuntersEchoMath.ShouldAbsorb(true, false));
            Assert.IsFalse(HuntersEchoMath.ShouldAbsorb(false, true));
            Assert.IsFalse(HuntersEchoMath.ShouldAbsorb(false, false));
        }

        // ------------------------------------------------------------ appearance and snapshot

        [TestMethod]
        public void Translucency_IsClampedAndNeverInvisible()
        {
            Assert.AreEqual(0.3f, HuntersEchoMath.Translucency(0.3), 1e-6);
            Assert.AreEqual(0.0f, HuntersEchoMath.Translucency(-1.0), 1e-6);
            Assert.AreEqual((float)HuntersEchoMath.MaxTranslucency, HuntersEchoMath.Translucency(1.0), 1e-6);
            Assert.AreEqual(0.0f, HuntersEchoMath.Translucency(double.NaN), 1e-6);
        }

        [TestMethod]
        public void MitigationFactor_MultipliesTheThreeTermsAndClamps()
        {
            Assert.AreEqual(0.5f * 0.8f * 0.9f, HuntersEchoMath.MitigationFactor(0.5, 0.8, 0.9), 1e-6);
            Assert.AreEqual(HuntersEchoMath.MinMitigationFactor, HuntersEchoMath.MitigationFactor(0.0, 1.0, 1.0), 1e-6, "the Echo always takes some damage");
            Assert.AreEqual(HuntersEchoMath.MaxMitigationFactor, HuntersEchoMath.MitigationFactor(3.0, 2.0, 1.0), 1e-6);
            Assert.AreEqual(1.0f, HuntersEchoMath.MitigationFactor(double.NaN, 1.0, 1.0), 1e-6);
            Assert.AreEqual(1.0f, HuntersEchoMath.MitigationFactor(double.PositiveInfinity, 1.0, 1.0), 1e-6);
        }

        [TestMethod]
        public void MatchedStartingValue_LandsTheOwnersMaximum()
        {
            // the weenie: starting 100, max 150 (50 from Endurance); the owner has 1200, so starting becomes 1150
            Assert.AreEqual(1150u, HuntersEchoMath.MatchedStartingValue(100, 150, 1200));

            // an owner below the attribute share never drives the starting value under 1
            Assert.AreEqual(1u, HuntersEchoMath.MatchedStartingValue(100, 150, 10));
        }

        [TestMethod]
        public void MatchedInitLevel_LandsTheOwnersSkill()
        {
            // skill current 150 from InitLevel 100 plus 50 formula; owner 600 -> InitLevel 550
            Assert.AreEqual(550u, HuntersEchoMath.MatchedInitLevel(100, 150, 600));
            Assert.AreEqual(0u, HuntersEchoMath.MatchedInitLevel(100, 150, 20), "never below 0");
        }

        // ------------------------------------------------------------ handler definition and readout

        [TestMethod]
        public void Definition_IsArcherTier3_OneRankAtThreeCap_AssessCreature()
        {
            var def = new HuntersEchoAbility().Definition;

            Assert.AreEqual(ClassAbilityId.HuntersEcho, def.Id);
            Assert.AreEqual(98, (int)def.Id);
            Assert.AreEqual(ClassAbilityClass.Archer, def.AbilityClass);
            Assert.AreEqual(3, def.Tier);
            Assert.AreEqual("hunters_echo", def.Name);
            Assert.AreEqual("Hunter's Echo", def.DisplayName);
            Assert.AreEqual(1, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 3 }, def.CostPerRank);
            Assert.AreEqual(Skill.AssessCreature, def.AffinitySkill);
            Assert.IsTrue(def.Implemented);
        }

        [TestMethod]
        public void Definition_StatesTheAffinityDegree()
        {
            StringAssert.Contains(new HuntersEchoAbility().Definition.DescriptionTemplate,
                "by at most {v:class_ability_huntersecho_affinity_cap_seconds|num} seconds");
        }

        [TestMethod]
        public void Registry_HoldsTheHandler()
        {
            Assert.IsInstanceOfType(ClassAbilityRegistry.GetHandler(ClassAbilityId.HuntersEcho), typeof(HuntersEchoAbility));
        }

        [TestMethod]
        public void Readout_NullPlayer_ReportsTheBaseDuration()
        {
            var saved = Seed();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble("class_ability_huntersecho_duration_seconds", 6.0), "duration key must be registered");
                Assert.IsTrue(PropertyManager.ModifyDouble("class_ability_huntersecho_affinity_cap_seconds", 2.0), "affinity cap key must be registered");

                var readout = new HuntersEchoAbility().GetReadout(null, 1);

                Assert.IsTrue(readout.HasValue);
                Assert.AreEqual(6.0, readout.Skill, 1e-9);
                Assert.AreEqual(0.0, readout.Affinity, 1e-9);
                Assert.AreEqual(6.0, readout.Effective, 1e-9);
                Assert.AreEqual("s", readout.Unit);
                Assert.IsNull(readout.CapNote);

                Assert.AreEqual(6.0, HuntersEchoAbility.DurationFor(null), 1e-9);

                var unowned = new HuntersEchoAbility().GetReadout(null, 0);
                Assert.IsFalse(unowned.HasValue);
            }
            finally
            {
                Restore(saved);
            }
        }

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double[] Seed()
        {
            var saved = new double[SeededDoubles.Length];

            for (var i = 0; i < SeededDoubles.Length; i++)
                saved[i] = PropertyManager.GetDouble(SeededDoubles[i]).Item;

            return saved;
        }

        private static void Restore(double[] saved)
        {
            for (var i = 0; i < SeededDoubles.Length; i++)
                PropertyManager.ModifyDouble(SeededDoubles[i], saved[i]);
        }
    }
}
