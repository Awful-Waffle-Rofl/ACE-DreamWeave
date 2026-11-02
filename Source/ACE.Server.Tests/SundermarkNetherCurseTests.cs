using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Sundermark's Nether branch, owner ruling 2026-10-30 (option B), which replaced the 2026-10-27 Nether-MELEE
    /// gate: ANY hit that dealt Nether - weapon path or spell path, whatever the weapon - applies ONE of
    /// Corruption, Corrosion or Destructive Curse, uniformly. Both the chance and (owner correction 2026-10-30)
    /// the curse LEVEL ride Life Magic exactly as the vulnerability path does - the rung Life Magic reaches on
    /// the shared 8-rung ladder, capped by class_ability_sundermark_nether_max_level (clamped 1-8) as a safety
    /// valve. Void Magic plays no part any more.
    /// </summary>
    [TestClass]
    public class SundermarkNetherCurseTests
    {
        // ---- the 24 ids -----------------------------------------------------------------------------

        [TestMethod]
        public void NetherCurseFamilies_AreTheTwentyFourVoidDotIds_InLevelOrder()
        {
            Assert.AreEqual(3, SpellswordSpellTables.NetherCurseFamilies.Length);

            CollectionAssert.AreEqual(new uint[] { 5395, 5396, 5397, 5398, 5399, 5400, 5401, 5402 },
                SpellswordSpellTables.NetherCurseFamilies[0].Select(s => (uint)s).ToArray(), "Corruption I-VIII");
            CollectionAssert.AreEqual(new uint[] { 5387, 5388, 5389, 5390, 5391, 5392, 5393, 5394 },
                SpellswordSpellTables.NetherCurseFamilies[1].Select(s => (uint)s).ToArray(), "Corrosion I-VIII");
            CollectionAssert.AreEqual(new uint[] { 5339, 5340, 5341, 5342, 5343, 5344, 5337, 5338 },
                SpellswordSpellTables.NetherCurseFamilies[2].Select(s => (uint)s).ToArray(), "Destructive Curse I-VIII (VII and VIII sit below I)");

            Assert.AreEqual(24, SpellswordSpellTables.NetherCurseFamilies.SelectMany(f => f).Distinct().Count());

            foreach (var family in SpellswordSpellTables.NetherCurseFamilies)
                Assert.AreEqual(SpellswordSpellTables.VulnerabilityLevels.Length, family.Length, "parallel to the 8-rung ladder");
        }

        // ---- uniform pick ---------------------------------------------------------------------------

        [TestMethod]
        public void RollNetherCurseFamily_AsksForTheInclusiveRangeZeroToTwo()
        {
            int? lo = null, hi = null;

            SundermarkAbility.RollNetherCurseFamily((a, b) => { lo = a; hi = b; return a; });

            Assert.AreEqual(0, lo);
            Assert.AreEqual(2, hi, "ThreadSafeRandom.Next(int, int) is inclusive, so the upper bound is the last index");
        }

        [TestMethod]
        public void RollNetherCurseFamily_IsUniformAcrossTheThreeFamilies()
        {
            var rng = new Random(12345);
            var counts = new int[3];
            const int draws = 30000;

            for (var i = 0; i < draws; i++)
                counts[SundermarkAbility.RollNetherCurseFamily((a, b) => rng.Next(a, b + 1))]++;

            foreach (var c in counts)
                Assert.AreEqual(draws / 3.0, c, draws * 0.02, $"counts [{string.Join(", ", counts)}]");
        }

        [TestMethod]
        public void SelectNetherCurse_EachFamilyRoll_PicksThatFamily()
        {
            Assert.AreEqual(SpellId.Corruption8, SundermarkAbility.SelectNetherCurse(0, 1000, 8, 1.0));
            Assert.AreEqual(SpellId.Corrosion8, SundermarkAbility.SelectNetherCurse(1, 1000, 8, 1.0));
            Assert.AreEqual(SpellId.CurseDestructionOther8, SundermarkAbility.SelectNetherCurse(2, 1000, 8, 1.0));
        }

        // ---- level by Life Magic, capped by the safety valve (owner correction 2026-10-30) --------------

        [TestMethod]
        public void SelectNetherCurse_LevelFollowsTheSkill_OnTheSharedMinPowerLadder()
        {
            Assert.AreEqual(SpellId.Corruption1, SundermarkAbility.SelectNetherCurse(0, 0, 8, 1.0), "below every threshold still applies rung I");
            Assert.AreEqual(SpellId.Corruption5, SundermarkAbility.SelectNetherCurse(0, 249, 8, 1.0), "249 meets MinPower(5)=200, not (6)=250");
            Assert.AreEqual(SpellId.Corruption7, SundermarkAbility.SelectNetherCurse(0, 399, 8, 1.0));
            Assert.AreEqual(SpellId.Corruption8, SundermarkAbility.SelectNetherCurse(0, 400, 8, 1.0));
        }

        /// <summary>
        /// The curse picks the SAME rung index the vulnerability path picks for the same skill, tunable cap and
        /// scale, so the two branches cannot drift: one SelectRungIndex call shape, one ladder.
        /// </summary>
        [TestMethod]
        public void SelectNetherCurse_PicksTheSameRungAsTheVulnerabilityPath()
        {
            foreach (var skill in new uint[] { 0, 50, 100, 150, 200, 249, 250, 300, 350, 399, 400, 600 })
            {
                foreach (var cap in new long[] { 1, 4, 8 })
                {
                    foreach (var scale in new[] { 0.5, 1.0, 1.5 })
                    {
                        var rung = SpellswordSpellTables.SelectRungIndex(SpellswordSpellTables.VulnerabilityLevels, (uint)cap, skill, scale);

                        Assert.AreEqual(SpellswordSpellTables.NetherCurseFor(1, rung), SundermarkAbility.SelectNetherCurse(1, skill, cap, scale),
                            $"skill {skill}, cap {cap}, scale {scale}");
                    }
                }
            }
        }

        [TestMethod]
        public void SelectNetherCurse_MaxLevelTunable_ClampsToOneThroughEight()
        {
            Assert.AreEqual(SpellId.CurseDestructionOther5, SundermarkAbility.SelectNetherCurse(2, 1000, 5, 1.0));
            Assert.AreEqual(SpellId.CurseDestructionOther1, SundermarkAbility.SelectNetherCurse(2, 1000, 0, 1.0), "0 clamps up to 1");
            Assert.AreEqual(SpellId.CurseDestructionOther1, SundermarkAbility.SelectNetherCurse(2, 1000, -5, 1.0));
            Assert.AreEqual(SpellId.CurseDestructionOther8, SundermarkAbility.SelectNetherCurse(2, 1000, 99, 1.0), "99 clamps down to 8");

            Assert.AreEqual(1u, SundermarkAbility.NetherMaxLevel(0));
            Assert.AreEqual(8u, SundermarkAbility.NetherMaxLevel(long.MaxValue));
        }

        [TestMethod]
        public void NetherMaxLevelTunable_IsRegisteredAtEight_AsASafetyValveOverLifeMagic()
        {
            var entry = DefaultPropertyManager.DefaultLongProperties["class_ability_sundermark_nether_max_level"];

            Assert.AreEqual(8L, entry.Item);
            StringAssert.Contains(entry.Description, "Safety valve; the level is otherwise set by Life Magic");
            Assert.IsFalse(entry.Description.Contains("Void Magic"), "the tunable is no longer a safety valve over Void Magic");
        }

        // ---- the gate: ANY Nether hit -------------------------------------------------------------------

        [TestMethod]
        public void IsNetherCurseHit_IsTrueForNetherAlone()
        {
            Assert.IsTrue(SundermarkAbility.IsNetherCurseHit(DamageType.Nether));

            foreach (var other in new[] { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire,
                         DamageType.Cold, DamageType.Acid, DamageType.Electric, DamageType.Health, DamageType.Base })
                Assert.IsFalse(SundermarkAbility.IsNetherCurseHit(other), $"{other} takes the vulnerability path");
        }

        /// <summary>
        /// The gate consults the dealt type only: the shared proc body passes nothing else into it, and both
        /// dispatch paths feed the shared body. Source scan, because the body needs a live Player.
        /// </summary>
        [TestMethod]
        public void TryApplyVulnerability_GatesOnTheDealtTypeOnly_AndLevelsTheCurseOnLifeMagic()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/SundermarkAbility.cs");
            var body = PooledLootSourceText.MethodBody(src, "private static void TryApplyVulnerability(");

            StringAssert.Contains(body, "IsNetherCurseHit(damageType)");
            StringAssert.Contains(body, "AffinityMultiplier(attacker)");
            StringAssert.Contains(body,
                "SelectNetherCurse(RollNetherCurseFamily(ThreadSafeRandom.Next),\n" +
                "                    attacker.GetCreatureSkill(Skill.LifeMagic).Current,\n" +
                "                    PropertyManager.GetLong(\"class_ability_sundermark_nether_max_level\").Item,\n" +
                "                    PropertyManager.GetDouble(\"class_ability_spellsword_level_skill_scale\").Item);");

            Assert.IsFalse(src.Contains("Skill.VoidMagic"), "no Void Magic input remains anywhere in Sundermark");

            var nether = body.IndexOf("if (netherCurse)", StringComparison.Ordinal);
            var vuln = body.IndexOf("ElementalRendAbility.GetVulnerabilitySpell(", StringComparison.Ordinal);
            Assert.IsTrue(nether >= 0 && nether < vuln, "the Nether branch must be decided and return before the vulnerability lookup");

            // the elemental half is unchanged
            StringAssert.Contains(body, "PropertyManager.GetLong(\"class_ability_sundermark_max_level\")");
            StringAssert.Contains(body, "attacker.GetCreatureSkill(Skill.LifeMagic).Current");
            StringAssert.Contains(body, "ElementalRendAbility.GetVulnerabilitySpell(damageType, spellLevel)");

            // both dispatch paths reach the shared body with the dealt type
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "public void ModifyOutgoingDamage("),
                "TryApplyVulnerability(attacker, rank, target, damageEvent.DamageType, damageEvent.Weapon)");
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "public void OnSpellHit("),
                "TryApplyVulnerability(caster, rank, primaryTarget, projectile.Spell.DamageType, projectile.ProjectileLauncher)");
        }

        // ---- affinity: Life Magic only ------------------------------------------------------------------

        [TestMethod]
        public void Definition_DeclaresLifeMagicOnly_AndTheRulingDescription()
        {
            var def = new SundermarkAbility().Definition;

            Assert.AreEqual(Skill.LifeMagic, def.AffinitySkill);
            Assert.IsTrue(def.AdditionalAffinitySkills == null || def.AdditionalAffinitySkills.Length == 0,
                "Void Magic was dropped from AdditionalAffinitySkills (owner ruling 2026-10-30)");

            Assert.AreEqual("Your landed hits with any weapon and your damaging spell projectiles have a 7/13/19% chance (by rank) " +
                            "to inflict the matching element's Vulnerability on the target. The level applied is set by your Life " +
                            "Magic, and higher Life Magic also increases the chance. A Nether hit instead inflicts a random void " +
                            "curse - Corruption, Corrosion or Destructive Curse - at the level your Life Magic sets.", def.Description);
        }

        /// <summary>
        /// GetReadout and the proc need a live Player, so their wiring is pinned by source scan: both go
        /// through the one AffinityMultiplier helper, which is a literal Life Magic call.
        /// </summary>
        [TestMethod]
        public void ReadoutAndProc_ShareTheLifeMagicAffinityHelper()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/SundermarkAbility.cs");

            var readout = PooledLootSourceText.MethodBody(src, "public ClassAbilityReadout GetReadout(");
            StringAssert.Contains(readout, "AffinityMultiplier(player)");
            Assert.IsFalse(readout.Contains("GetClassAbilityAffinityMultiplier("), "the readout must not compute its own affinity");

            var helper = PooledLootSourceText.MethodBody(src, "private static double AffinityMultiplier(");
            StringAssert.Contains(helper, "GetClassAbilityAffinityMultiplier(Skill.LifeMagic)");
        }
    }
}
