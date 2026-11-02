using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Weakened Blood, Blood Mage buff pass (2026-10-17): the per-target proc, the projectile source gate,
    /// the slow (its math, its target-side state, and its composition into monster attack speed and cast
    /// time), the once-per-cast announcement rule, and the readout's two secondaries.
    ///
    /// Every PropertyManager key a test here reads is SEEDED by that test and restored in a finally, so the
    /// class passes alone and in any order (PropertyManager's caches are process-wide).
    /// </summary>
    [TestClass]
    public class WeakenedBloodProcAndSlowTests
    {
        private const float Epsilon = 0.0001f;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static readonly (string Key, double Value)[] SeededDoubles =
        {
            ("class_ability_weakenedblood_resist_r1", 2.00),
            ("class_ability_weakenedblood_resist_r2", 2.50),
            ("class_ability_weakenedblood_resist_r3", 3.10),
            ("class_ability_weakenedblood_proc_chance_r1", 0.25),
            ("class_ability_weakenedblood_proc_chance_r2", 0.35),
            ("class_ability_weakenedblood_proc_chance_r3", 0.50),
            ("class_ability_weakenedblood_slow_r1", 0.10),
            ("class_ability_weakenedblood_slow_r2", 0.20),
            ("class_ability_weakenedblood_slow_r3", 0.30),
            ("class_ability_weakenedblood_slow_floor", 0.50),
            ("monster_effect_speed_cap", 2.0),
        };

        /// <summary>
        /// Seeds every key this class reads (plus monster_effects_enabled = true), runs the body, and puts
        /// every one back exactly as it found it.
        /// </summary>
        private static void WithSeededTunables(Action body)
        {
            var savedDoubles = new Dictionary<string, double>();

            foreach (var (key, _) in SeededDoubles)
                savedDoubles[key] = PropertyManager.GetDouble(key).Item;

            var savedEnabled = PropertyManager.GetBool("monster_effects_enabled").Item;

            try
            {
                foreach (var (key, value) in SeededDoubles)
                    Assert.IsTrue(PropertyManager.ModifyDouble(key, value), $"could not seed {key}");

                Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", true));

                body();
            }
            finally
            {
                foreach (var kvp in savedDoubles)
                    PropertyManager.ModifyDouble(kvp.Key, kvp.Value);

                PropertyManager.ModifyBool("monster_effects_enabled", savedEnabled);
            }
        }

        // ---- the proc ---------------------------------------------------------------------------------

        [TestMethod]
        public void Proc_ARollEqualToTheChanceDoesNotProc()
        {
            Assert.IsFalse(WeakenedBloodMath.Procs(0.25, 0.25), "strictly less than: roll == chance must not proc");
            Assert.IsTrue(WeakenedBloodMath.Procs(0.2499, 0.25));
            Assert.IsFalse(WeakenedBloodMath.Procs(0.0, 0.0), "a zero chance can never fire, even on a zero roll");
            Assert.IsTrue(WeakenedBloodMath.Procs(0.9999, 1.0));
        }

        [TestMethod]
        public void Proc_ChanceTableIs25_35_50ByRank_AndClampsOutOfRange()
        {
            WithSeededTunables(() =>
            {
                double C(int rank) => WeakenedBloodMath.ProcChance(rank,
                    PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r1").Item,
                    PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r2").Item,
                    PropertyManager.GetDouble("class_ability_weakenedblood_proc_chance_r3").Item);

                Assert.AreEqual(0.25, C(1), 1e-9);
                Assert.AreEqual(0.35, C(2), 1e-9);
                Assert.AreEqual(0.50, C(3), 1e-9);
                Assert.AreEqual(0.0, C(0), 1e-9, "no ability, no chance");
                Assert.AreEqual(0.50, C(9), 1e-9, "a rank above 3 clamps to rank 3");
            });

            Assert.AreEqual(1.0, WeakenedBloodMath.ProcChance(1, 1.7, 0, 0), 1e-9, "a chance cannot exceed 1");
            Assert.AreEqual(0.0, WeakenedBloodMath.ProcChance(1, -0.3, 0, 0), 1e-9, "or go below 0");
        }

        [TestMethod]
        public void Proc_TryApplyWeakenedBlood_RollsOncePerCall_WithTheStrictComparison()
        {
            var source = ReadServerSource("ACE.Server/WorldObjects/Player_WeakenedBlood.cs");

            var start = source.IndexOf("public void TryApplyWeakenedBlood(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "TryApplyWeakenedBlood not found");

            var end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
            var body = source.Substring(start, end - start);

            Assert.AreEqual(1, Count(body, "ThreadSafeRandom.Next(0.0f, 1.0f)"), "exactly one roll per call, i.e. per target struck");
            Assert.IsTrue(body.Contains("WeakenedBloodMath.Procs("), "the roll goes through the strict-less-than helper");
            Assert.IsTrue(body.IndexOf("WeakenedBloodMath.Procs(", StringComparison.Ordinal) < body.IndexOf("target.ApplyWeakenedBlood(", StringComparison.Ordinal),
                "the roll gates the application - it must come first");

            var claim = body.IndexOf("weakenedBloodRollLedger.TryClaimRoll(castNum, target.Guid.Full)", StringComparison.Ordinal);
            Assert.IsTrue(claim >= 0, "the per-target-per-cast ledger must gate the roll");
            Assert.IsTrue(claim < body.IndexOf("ThreadSafeRandom.Next(0.0f, 1.0f)", StringComparison.Ordinal),
                "the ledger check must come BEFORE the roll, so a repeat target draws no roll at all");
        }

        [TestMethod]
        public void RollLedger_SameCastSameTargetDrawsNoSecondRoll_NewCastRollsAgain()
        {
            var ledger = new WeakenedBloodCastRollLedger();

            Assert.IsTrue(ledger.TryClaimRoll(5, 0x80000001), "first strike on a target in a cast rolls");
            Assert.IsFalse(ledger.TryClaimRoll(5, 0x80000001), "a second projectile of the same cast on the same target draws no roll");
            Assert.IsFalse(ledger.TryClaimRoll(5, 0x80000001), "nor a third (three Raven Fury projectiles on one large monster are one roll)");

            Assert.IsTrue(ledger.TryClaimRoll(5, 0x80000002), "a different target in the same cast rolls once of its own");

            Assert.IsTrue(ledger.TryClaimRoll(6, 0x80000001), "a new cast number rolls again for the same target");
            Assert.IsFalse(ledger.TryClaimRoll(6, 0x80000001));
            Assert.IsTrue(ledger.TryClaimRoll(6, 0x80000002), "the new cast cleared the old cast's set");
        }

        // ---- sources: the projectile gate --------------------------------------------------------------

        [TestMethod]
        public void ProjectileGate_HecatombAndBloodstoneShapesApply_WarBoltAndManaLifeProjectileDoNot()
        {
            // Martyr's Hecatomb / Curse of Raven Fury: Life Magic LifeProjectile, Health (e_Type 128)
            Assert.IsTrue(WeakenedBloodMath.AppliesFromSpellProjectile(MagicSchool.LifeMagic, SpellType.LifeProjectile, DamageType.Health));

            // Bloodstone Bolt: Life Magic Projectile, Health
            Assert.IsTrue(WeakenedBloodMath.AppliesFromSpellProjectile(MagicSchool.LifeMagic, SpellType.Projectile, DamageType.Health));

            // a war bolt: wrong school (and wrong damage type)
            Assert.IsFalse(WeakenedBloodMath.AppliesFromSpellProjectile(MagicSchool.WarMagic, SpellType.Projectile, DamageType.Fire));

            // a war-school Health projectile still fails the school test - the gate is not damage type alone
            Assert.IsFalse(WeakenedBloodMath.AppliesFromSpellProjectile(MagicSchool.WarMagic, SpellType.Projectile, DamageType.Health));

            // Martyr's Blight: a Life Magic LifeProjectile on Mana (e_Type 512)
            Assert.IsFalse(WeakenedBloodMath.AppliesFromSpellProjectile(MagicSchool.LifeMagic, SpellType.LifeProjectile, DamageType.Mana));

            // a life ENCHANTMENT projectile is not a damage projectile
            Assert.IsFalse(WeakenedBloodMath.AppliesFromSpellProjectile(MagicSchool.LifeMagic, SpellType.EnchantmentProjectile, DamageType.Health));
        }

        [TestMethod]
        public void ProjectileSource_IsASpellHitHookInItsBucket_AndDeclaresCreatureEnchantment()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.WeakenedBlood);

            Assert.IsInstanceOfType(handler, typeof(ISpellHitAbility));
            Assert.IsFalse(handler is IPassiveStatAbility, "it is dispatched from a hook now, so it is no longer a passive marker");
            Assert.IsTrue(ClassAbilityRegistry.SpellHitAbilities.Contains((ISpellHitAbility)handler), "auto-bucketed into SpellHitAbilities");
            Assert.AreEqual(Skill.CreatureEnchantment, handler.Definition.AffinitySkill);
        }

        // ---- the slow: math ------------------------------------------------------------------------------

        [TestMethod]
        public void SlowMath_FactorIsOneMinusSlow_AndTheFloorBinds()
        {
            Assert.AreEqual(0.80, WeakenedBloodMath.SpeedFactor(0.20, 0.50), 1e-9);
            Assert.AreEqual(0.50, WeakenedBloodMath.SpeedFactor(0.50, 0.50), 1e-9);

            // the floor binds: an affinity-boosted 0.75 slow stops at half speed
            Assert.AreEqual(0.50, WeakenedBloodMath.SpeedFactor(0.75, 0.50), 1e-9);

            Assert.AreEqual(1.0, WeakenedBloodMath.SpeedFactor(0.0, 0.50), 1e-9, "no slow, no change");
            Assert.AreEqual(1.0, WeakenedBloodMath.SpeedFactor(-0.3, 0.50), 1e-9, "a negative slow is never a haste");

            // a floor of 0 or less can never produce a zero divisor
            Assert.AreEqual(WeakenedBloodMath.MinSpeedFactor, WeakenedBloodMath.SpeedFactor(5.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void SlowMath_RankTable()
        {
            Assert.AreEqual(0.20, WeakenedBloodMath.RankSlow(1, 0.20, 0.35, 0.50), 1e-9);
            Assert.AreEqual(0.35, WeakenedBloodMath.RankSlow(2, 0.20, 0.35, 0.50), 1e-9);
            Assert.AreEqual(0.50, WeakenedBloodMath.RankSlow(3, 0.20, 0.35, 0.50), 1e-9);
            Assert.AreEqual(0.0, WeakenedBloodMath.RankSlow(0, 0.20, 0.35, 0.50), 1e-9);
            Assert.AreEqual(0.50, WeakenedBloodMath.RankSlow(7, 0.20, 0.35, 0.50), 1e-9);
        }

        // ---- the slow: target-side state ---------------------------------------------------------------

        [TestMethod]
        public void SlowState_MergesByTheStrongerSlow_SharesTheMarksExpiry_AndClearsOnLapse()
        {
            var creature = TestCreatures.CreateDefender();

            const double t0 = 1000.0;

            Assert.IsTrue(creature.ApplyWeakenedBlood(3.10, 20.0, 0.50, t0), "a fresh landing takes hold");
            Assert.AreEqual(0.50, creature.GetWeakenedBloodSpeedFactor(t0 + 1.0), 1e-9);

            // a weaker slow later does not weaken it (and is a refresh, not a fresh landing)
            Assert.IsFalse(creature.ApplyWeakenedBlood(2.00, 20.0, 0.80, t0 + 10.0));
            Assert.AreEqual(0.50, creature.GetWeakenedBloodSpeedFactor(t0 + 11.0), 1e-9, "merged by max slow (min factor)");

            // ...but its refresh extended the SHARED expiry, so the strong slow lives to t0 + 30
            Assert.AreEqual(0.50, creature.GetWeakenedBloodSpeedFactor(t0 + 29.9), 1e-9, "the slow rides the mark's refreshed expiry");

            // a stronger one takes over
            creature.ApplyWeakenedBlood(2.00, 20.0, 0.40, t0 + 29.0);
            Assert.AreEqual(0.40, creature.GetWeakenedBloodSpeedFactor(t0 + 30.0), 1e-9);

            // lapse: past the mark's expiry (t0 + 49) the slow is gone and stays gone
            Assert.AreEqual(1.0, creature.GetWeakenedBloodSpeedFactor(t0 + 49.0), 1e-9, "expires at the boundary with the mark");
            Assert.AreEqual(1.0, creature.GetWeakenedBloodSpeedFactor(t0 + 30.0), 1e-9, "a lapse CLEARS the stored factor, it does not merely hide it");

            // and a lapsed strong slow cannot resurrect through a later weak application
            creature.ApplyWeakenedBlood(2.00, 20.0, 0.80, t0 + 100.0);
            Assert.AreEqual(0.80, creature.GetWeakenedBloodSpeedFactor(t0 + 101.0), 1e-9);
        }

        [TestMethod]
        public void SlowState_ARejectedMarkCarriesNoSlow()
        {
            var creature = TestCreatures.CreateDefender();

            Assert.IsFalse(creature.ApplyWeakenedBlood(1.0, 20.0, 0.50, 1000.0), "a non-vulnerability magnitude is rejected");
            Assert.AreEqual(1.0, creature.GetWeakenedBloodSpeedFactor(1001.0), 1e-9);

            Assert.IsFalse(creature.ApplyWeakenedBlood(2.0, 0.0, 0.50, 1000.0), "a non-positive duration is rejected");
            Assert.AreEqual(1.0, creature.GetWeakenedBloodSpeedFactor(1001.0), 1e-9);
        }

        [TestMethod]
        public void SlowState_AChessPieceTakesTheMarkButNeverTheSlow()
        {
            var wcid = TestCreatures.NextWcid();

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.GamePiece,
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = 50 } },
                },
            };

            var piece = new GamePiece(weenie, new ObjectGuid(0x7D000000 + wcid));

            Assert.IsFalse(piece.CanBeWeakenedBloodSlowed);

            Assert.IsTrue(piece.ApplyWeakenedBlood(3.10, 20.0, 0.50, 1000.0), "the vulnerability half still lands");
            Assert.AreEqual(1.0, piece.GetWeakenedBloodSpeedFactor(1001.0), 1e-9, "but the slow is refused at the state boundary");

            // control: an ordinary creature in the same call does take the slow
            var creature = TestCreatures.CreateDefender();
            Assert.IsTrue(creature.CanBeWeakenedBloodSlowed);
            creature.ApplyWeakenedBlood(3.10, 20.0, 0.50, 1000.0);
            Assert.AreEqual(0.50, creature.GetWeakenedBloodSpeedFactor(1001.0), 1e-9);
        }

        // ---- the slow: composition into monster attack speed and cast time -----------------------------

        [TestMethod]
        public void Composition_AttackSpeedIsSlowed_WithNoMonsterEffects()
        {
            WithSeededTunables(() =>
            {
                var creature = TestCreatures.CreateDefender();

                Assert.IsNull(creature.MonsterEffects, "fixture precondition: the common case, no authored effects");

                var baseline = creature.GetAnimSpeed();

                creature.ApplyWeakenedBlood(2.0, 600.0, 0.65, Time.GetUnixTime());

                Assert.AreEqual(baseline * 0.65f, creature.GetAnimSpeed(), Epsilon,
                    "the slow must reach a monster that carries no MonsterEffects at all");
            });
        }

        [TestMethod]
        public void Composition_AttackSlowMultipliesAfterTheMonsterEffectSpeed_SoAnAuthoredSlowMonsterIsStillSlowed()
        {
            WithSeededTunables(() =>
            {
                var creature = TestCreatures.CreateDefender();

                var baseline = creature.GetAnimSpeed();

                MonsterEffectParser.Parse("speed axis=attack pct=-0.2", out var specs, out _);
                creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((new SpeedEffect(), specs[0])));

                Assert.AreEqual(baseline * 0.8f, creature.GetAnimSpeed(), Epsilon, "control: the authored slow alone");

                creature.ApplyWeakenedBlood(2.0, 600.0, 0.5, Time.GetUnixTime());

                Assert.AreEqual(baseline * 0.8f * 0.5f, creature.GetAnimSpeed(), Epsilon,
                    "the floor is relative to the monster's own effect-adjusted speed: 0.8 x 0.5, not clamped to 0.5 absolute");
            });
        }

        [TestMethod]
        public void Composition_CastTimeIsStretched_WithAndWithoutMonsterEffects()
        {
            WithSeededTunables(() =>
            {
                var plain = TestCreatures.CreateDefender();

                Assert.AreEqual(3.0f, plain.ScaleMonsterEffectCastTime(3.0f), Epsilon, "control: unslowed is unchanged");

                plain.ApplyWeakenedBlood(2.0, 600.0, 0.5, Time.GetUnixTime());

                Assert.AreEqual(6.0f, plain.ScaleMonsterEffectCastTime(3.0f), Epsilon,
                    "MonsterEffects == null must still be slowed - a duration divides by the factor");

                var hasted = TestCreatures.CreateDefender();

                MonsterEffectParser.Parse("speed axis=cast pct=1.0", out var specs, out _);
                hasted.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((new SpeedEffect(), specs[0])));

                Assert.AreEqual(1.5f, hasted.ScaleMonsterEffectCastTime(3.0f), Epsilon, "control: a 2.0x cast speed halves it");

                hasted.ApplyWeakenedBlood(2.0, 600.0, 0.5, Time.GetUnixTime());

                Assert.AreEqual(3.0f, hasted.ScaleMonsterEffectCastTime(3.0f), Epsilon, "the slow applies after the effect multiplier");
            });
        }

        [TestMethod]
        public void Composition_CadenceDelaysAndTheMagicGateStretchTogether()
        {
            var creature = TestCreatures.CreateDefender();

            Assert.AreEqual(1.0f, creature.ScaleWeakenedBloodSlowDuration(1.0f), 0.0f, "unslowed: bit-identical");
            Assert.AreEqual(3.0, creature.GetWeakenedBloodScaledMagicDelay(), 0.0, "unslowed: the 3.0 default, exactly");

            creature.ApplyWeakenedBlood(2.0, 600.0, 0.5, Time.GetUnixTime());

            Assert.AreEqual(2.0f, creature.ScaleWeakenedBloodSlowDuration(1.0f), Epsilon);
            Assert.AreEqual(2.0, creature.ScaleWeakenedBloodSlowDuration(1.0), 1e-9);
            Assert.AreEqual(6.0, creature.GetWeakenedBloodScaledMagicDelay(), 1e-9);

            // the magic gate reads the same helper DoAttackStance aligns against
            creature.PrevAttackTime = 100.0;
            Assert.AreEqual(106.0, creature.NextMagicAttackTime, 1e-9);
        }

        [TestMethod]
        public void Composition_EveryCadenceSiteIsWired()
        {
            AssertContains("ACE.Server/WorldObjects/Monster_Melee.cs",
                "var meleeDelay = ScaleWeakenedBloodSlowDuration(ThreadSafeRandom.Next(0.0f, (float)(PowerupTime ?? 1.0f)));");

            AssertContains("ACE.Server/WorldObjects/Monster_Missile.cs",
                "NextMoveTime = NextAttackTime = PrevAttackTime + timeOffset + ScaleWeakenedBloodSlowDuration(MissileDelay);",
                "PrevAttackTime = NextMoveTime - GetWeakenedBloodScaledMagicDelay();");

            AssertContains("ACE.Server/WorldObjects/Monster_Magic.cs",
                "var postDelay = ScaleWeakenedBloodSlowDuration(ThreadSafeRandom.Next(0.0f, powerupTime));");

            AssertContains("ACE.Server/WorldObjects/Monster_Combat.cs",
                "var magicDelay = GetWeakenedBloodScaledMagicDelay();",
                "PrevAttackTime = NextAttackTime + missileDelay - magicDelay;",
                "NextAttackTime += missileDelay;");

            var combat = ReadServerSource("ACE.Server/WorldObjects/Monster_Combat.cs");
            Assert.IsFalse(combat.Contains("(AiUseMagicDelay ?? 3.0f)"),
                "every magic-delay read must go through the slow-scaled helper, or the alignment math disagrees with NextMagicAttackTime");

            var missile = ReadServerSource("ACE.Server/WorldObjects/Monster_Missile.cs");
            Assert.IsFalse(missile.Contains("NextMoveTime - (AiUseMagicDelay ?? 3.0f)"));
        }

        // ---- the announcement ----------------------------------------------------------------------------

        [TestMethod]
        public void Announce_AtMostOncePerCast()
        {
            Assert.IsTrue(WeakenedBloodMath.ShouldAnnounce(true, 7, -1), "first landing of a cast announces");
            Assert.IsFalse(WeakenedBloodMath.ShouldAnnounce(true, 7, 7), "a second creature marked by the same cast does not");
            Assert.IsTrue(WeakenedBloodMath.ShouldAnnounce(true, 8, 7), "the next cast may");
            Assert.IsFalse(WeakenedBloodMath.ShouldAnnounce(false, 8, 7), "a refresh never announces");
        }

        // ---- the readout -----------------------------------------------------------------------------------

        [TestMethod]
        public void Readout_PrimaryUnchanged_SecondariesAreProcChanceAndSlow_NullPlayerSafe()
        {
            WithSeededTunables(() =>
            {
                var ability = new WeakenedBloodAbility();

                var expected = new[] { (2.00, 25.0, 10.0), (2.50, 35.0, 20.0), (3.10, 50.0, 30.0) };

                for (var rank = 1; rank <= 3; rank++)
                {
                    var readout = ability.GetReadout(null, rank);
                    var (vuln, proc, slow) = expected[rank - 1];

                    Assert.AreEqual(vuln, readout.Skill, 1e-9);
                    Assert.AreEqual("life vuln", readout.Label);
                    Assert.AreEqual(0.0, readout.Affinity, 1e-9, "the affinity rides the slow, not the vulnerability");

                    Assert.IsNotNull(readout.Secondary);
                    Assert.AreEqual(2, readout.Secondary.Count);

                    var procSeg = readout.Secondary[0];
                    Assert.AreEqual("proc", procSeg.Label);
                    Assert.AreEqual("%", procSeg.Unit);
                    Assert.AreEqual(proc, procSeg.Effective, 1e-9);

                    var slowSeg = readout.Secondary[1];
                    Assert.AreEqual("atk slow", slowSeg.Label);
                    Assert.AreEqual("%", slowSeg.Unit);
                    Assert.AreEqual(slow, slowSeg.Skill, 1e-9);
                    Assert.AreEqual(0.0, slowSeg.Affinity, 1e-9, "a null player has an affinity multiplier of 1.0");
                    Assert.AreEqual(slow, slowSeg.Effective, 1e-9);
                    Assert.IsFalse(slowSeg.Capped, "at defaults the rank-3 slow (30%) sits well inside the 50% floor");
                }
            });
        }

        [TestMethod]
        public void Readout_SlowSegmentNotesTheFloorWhenItBinds()
        {
            WithSeededTunables(() =>
            {
                Assert.IsTrue(PropertyManager.ModifyDouble("class_ability_weakenedblood_slow_floor", 0.85));

                var slowSeg = new WeakenedBloodAbility().GetReadout(null, 3).Secondary[1];

                Assert.AreEqual(30.0, slowSeg.Total, 1e-9);
                Assert.AreEqual(15.0, slowSeg.Effective, 1e-9, "a 0.85 floor caps a 30% slow at 15%");
                Assert.AreEqual("speed floor", slowSeg.CapNote);
            });
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        private static string ReadServerSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "repo root (a directory containing Source/ACE.Server/WorldObjects) not found above the test output dir");

            var path = Path.Combine(dir.FullName, "Source", relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"expected source file not found: {path}");

            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        private static int Count(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static void AssertContains(string relativePath, params string[] required)
        {
            var source = ReadServerSource(relativePath);

            foreach (var line in required)
                Assert.IsTrue(source.Contains(line), $"{relativePath} must contain: {line}");
        }
    }
}
