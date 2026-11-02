using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-24 Spellsword pass: the per-rank ladders (Spellsurge, Spellstorm, Cascade, Runic Ward's cap),
    /// Cascade's new Item Enchantment affinity and its clamp, the ward shield's max-not-sum rule, Sundermark's
    /// spell reach, and Dispelling Dance's one-roll two-halves shape over the shared Cleanse helper.
    ///
    /// READS NO PropertyManager KEY: every helper exercised here is pure and takes its tunables as arguments,
    /// and the rest are source pins, so this class passes alone or in any order.
    /// </summary>
    [TestClass]
    public class SpellswordPassTests
    {
        private const string Abilities = "Source/ACE.Server/ClassAbilities/Abilities/";

        // ---- per-rank ladders ------------------------------------------------------------------------

        [TestMethod]
        public void Spellsurge_PerStack_TracksTheRankLadder_AndClampsAboveAndBelow()
        {
            Assert.AreEqual(0.0, SpellsurgeAbility.PerStack(0, 0.0067, 0.0133, 0.02), 1e-12);
            Assert.AreEqual(0.0067, SpellsurgeAbility.PerStack(1, 0.0067, 0.0133, 0.02), 1e-12);
            Assert.AreEqual(0.0133, SpellsurgeAbility.PerStack(2, 0.0067, 0.0133, 0.02), 1e-12);
            Assert.AreEqual(0.02, SpellsurgeAbility.PerStack(3, 0.0067, 0.0133, 0.02), 1e-12);
            Assert.AreEqual(0.02, SpellsurgeAbility.PerStack(4, 0.0067, 0.0133, 0.02), 1e-12, "a rank above 3 reads the rank-3 value");
        }

        [TestMethod]
        public void Spellstorm_BaseChance_TracksTheRankLadder()
        {
            Assert.AreEqual(0.0, SpellstormAbility.BaseChance(0, 0.05, 0.10, 0.15), 1e-12);
            Assert.AreEqual(0.05, SpellstormAbility.BaseChance(1, 0.05, 0.10, 0.15), 1e-12);
            Assert.AreEqual(0.10, SpellstormAbility.BaseChance(2, 0.05, 0.10, 0.15), 1e-12);
            Assert.AreEqual(0.15, SpellstormAbility.BaseChance(3, 0.05, 0.10, 0.15), 1e-12);
        }

        [TestMethod]
        public void Cascade_BaseChance_TracksTheRankLadder()
        {
            Assert.AreEqual(0.0, CascadeAbility.BaseChance(0, 0.05, 0.10, 0.15), 1e-12);
            Assert.AreEqual(0.05, CascadeAbility.BaseChance(1, 0.05, 0.10, 0.15), 1e-12);
            Assert.AreEqual(0.10, CascadeAbility.BaseChance(2, 0.05, 0.10, 0.15), 1e-12);
            Assert.AreEqual(0.15, CascadeAbility.BaseChance(3, 0.05, 0.10, 0.15), 1e-12);
        }

        [TestMethod]
        public void RunicWard_CapFraction_TracksTheRankLadder()
        {
            Assert.AreEqual(0.10, RunicWardMath.CapFraction(1, 0.10, 0.20, 0.30), 1e-12);
            Assert.AreEqual(0.20, RunicWardMath.CapFraction(2, 0.10, 0.20, 0.30), 1e-12);
            Assert.AreEqual(0.30, RunicWardMath.CapFraction(3, 0.10, 0.20, 0.30), 1e-12);
        }

        // ---- Cascade's affinity ----------------------------------------------------------------------

        [TestMethod]
        public void Cascade_Affinity_MultipliesTheChance_TheAddedAmountClamped_GearOutsideTheClamp()
        {
            // neutral affinity and no gear: the rank's chance unchanged (the pre-pass call shape)
            Assert.AreEqual(0.15f, CascadeAbility.CascadeChance(3, 0.15), 1e-6f);

            // x1.5 on 0.15 adds 0.075, under a 0.2 cap
            Assert.AreEqual(0.225f, CascadeAbility.CascadeChance(3, 0.15, 0.0, 1.5, 0.2), 1e-6f);

            // x3 on 0.15 would add 0.30; the 0.2 cap holds it to 0.35
            Assert.AreEqual(0.35f, CascadeAbility.CascadeChance(3, 0.15, 0.0, 3.0, 0.2), 1e-6f);

            // the gear term is added AFTER the clamp, so it is never eaten by it
            Assert.AreEqual(0.40f, CascadeAbility.CascadeChance(3, 0.15, 0.05, 3.0, 0.2), 1e-6f);

            // a cap of 0 means uncapped
            Assert.AreEqual(0.45f, CascadeAbility.CascadeChance(3, 0.15, 0.0, 3.0, 0.0), 1e-6f);

            // a multiplier below 1 never lowers the chance; an unowned rank is always 0
            Assert.AreEqual(0.15f, CascadeAbility.CascadeChance(3, 0.15, 0.0, 0.5, 0.2), 1e-6f);
            Assert.AreEqual(0.0f, CascadeAbility.CascadeChance(0, 0.15, 0.05, 3.0, 0.2), 1e-6f);
        }

        [TestMethod]
        public void Cascade_DeclaresItemEnchantment_AndTheGenerationGuardIsUnchanged()
        {
            var def = ClassAbilityRegistry.Abilities[ClassAbilityId.Cascade];

            Assert.AreEqual(Skill.ItemEnchantment, def.AffinitySkill);
            Assert.AreEqual(3, def.MaxRank);

            // the guard: one hop, a non-proc never cascades
            Assert.IsTrue(CascadeAbility.CanCascade(true, 0, 1));
            Assert.IsFalse(CascadeAbility.CanCascade(true, 1, 1));
            Assert.IsFalse(CascadeAbility.CanCascade(false, 0, 1));

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Abilities + "CascadeAbility.cs"), "public void OnSpellHit(");
            StringAssert.Contains(body, "caster.GetClassAbilityAffinityMultiplier(Skill.ItemEnchantment)");
            StringAssert.Contains(body, "PropertyManager.GetDouble(\"class_ability_affinity_chance_cap\").Item");
        }

        // ---- the ward shield -------------------------------------------------------------------------

        [TestMethod]
        public void WardShield_TheLargerWardApplies_NeverTheSum()
        {
            Assert.AreEqual(100.0, WardShieldMath.Level(100.0, 100.0), 1e-12, "both wards up: max, not 200");
            Assert.AreEqual(150.0, WardShieldMath.Level(100.0, 150.0), 1e-12);
            Assert.AreEqual(100.0, WardShieldMath.Level(100.0, 0.0), 1e-12);
            Assert.AreEqual(100.0, WardShieldMath.Level(0.0, 100.0), 1e-12);
            Assert.AreEqual(0.0, WardShieldMath.Level(0.0, 0.0), 1e-12);
            Assert.AreEqual(0.0, WardShieldMath.Level(-50.0, -10.0), 1e-12, "a mis-tuned negative level counts as 0");
        }

        [TestMethod]
        public void WardShield_AfterShieldCap_AddsOnTopOfTheCappedLevel()
        {
            Assert.AreEqual(250.0, WardShieldMath.AfterShieldCap(150.0, 100.0), 1e-12);
            Assert.AreEqual(150.0, WardShieldMath.AfterShieldCap(150.0, 0.0), 1e-12);
            Assert.AreEqual(150.0, WardShieldMath.AfterShieldCap(150.0, -100.0), 1e-12, "a negative ward never lowers the shield");
        }

        [TestMethod]
        public void WardShield_LevelFor_EnemyCreatureInFront_WithAWardUp_GetsTheLevel()
        {
            Assert.AreEqual(100.0, WardShieldMath.LevelFor(true, false, true, true, true, 100, 120), 1e-12, "Runic only: its own level");
            Assert.AreEqual(120.0, WardShieldMath.LevelFor(false, true, true, true, true, 100, 120), 1e-12, "Sanguine only: its own level");
            Assert.AreEqual(120.0, WardShieldMath.LevelFor(true, true, true, true, true, 100, 120), 1e-12, "both wards: the max, never 220");
        }

        [TestMethod]
        public void WardShield_LevelFor_EveryRefusalIsZero()
        {
            Assert.AreEqual(0.0, WardShieldMath.LevelFor(false, false, true, true, true, 100, 100), 1e-12, "no ward up");
            Assert.AreEqual(0.0, WardShieldMath.LevelFor(true, true, false, true, true, 100, 100), 1e-12, "a player (PvP or self) or no live creature attacker");
            Assert.AreEqual(0.0, WardShieldMath.LevelFor(true, true, true, false, true, 100, 100), 1e-12, "attacker behind");
            Assert.AreEqual(0.0, WardShieldMath.LevelFor(true, true, true, true, false, 100, 100), 1e-12, "class abilities disabled");
            Assert.AreEqual(0.0, WardShieldMath.LevelFor(true, false, true, true, true, -50, 100), 1e-12, "a down ward's level is ignored, a negative level floors at 0");
        }

        [TestMethod]
        public void WardShield_IsFrontal_IsTheShieldsHalfCone()
        {
            Assert.IsTrue(WardShieldMath.IsFrontal(0.0));
            Assert.IsTrue(WardShieldMath.IsFrontal(90.0));
            Assert.IsTrue(WardShieldMath.IsFrontal(-90.0));
            Assert.IsFalse(WardShieldMath.IsFrontal(90.5));
            Assert.IsFalse(WardShieldMath.IsFrontal(-180.0));
        }

        [TestMethod]
        public void WardShield_PlayerGathersInputs_AndDelegatesToLevelFor()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityPreWriteBuffs.cs"),
                "public double GetWardShieldLevel(WorldObject attacker, WorldObject facing = null)");

            StringAssert.Contains(body, "ClassAbilityAttackerFilter.EnemyCreature(attacker, this) != null");
            StringAssert.Contains(body, "WardShieldMath.IsFrontal(GetAngle(facing ?? attacker))");
            StringAssert.Contains(body, "WardShieldMath.LevelFor(");
        }
        // ---- Sundermark ------------------------------------------------------------------------------

        [TestMethod]
        public void Sundermark_ProcsOnWeaponHitsAndSpellHits_ThroughOneSharedApply()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.Sundermark);

            Assert.IsInstanceOfType(handler, typeof(IOutgoingDamageAbility));
            Assert.IsInstanceOfType(handler, typeof(ISpellHitAbility), "Sundermark also procs on damaging spell hits");

            var src = PooledLootSourceText.Read(Abilities + "SundermarkAbility.cs");
            var spell = PooledLootSourceText.MethodBody(src, "public void OnSpellHit(");

            StringAssert.Contains(spell, "projectile.Spell.DamageType", "the vulnerability matches the spell's damage type");
            StringAssert.Contains(spell, "TryApplyVulnerability(");
            Assert.IsFalse(spell.Contains("Resist", StringComparison.Ordinal), "no resist check on the spell path");
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "public void ModifyOutgoingDamage("), "TryApplyVulnerability(", "the weapon path uses the same shared apply");

            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, ClassAbilityRegistry.Abilities[ClassAbilityId.Sundermark].CostPerRank);
        }

        // ---- Dispelling Dance ------------------------------------------------------------------------

        [TestMethod]
        public void DispellingDance_IsADisplayRename_TheInternalNameAndIdAreUnchanged()
        {
            var def = ClassAbilityRegistry.Abilities[ClassAbilityId.DispellingEdge];

            Assert.AreEqual("Dispelling Dance", def.DisplayName);
            Assert.AreEqual("dispellingedge", def.Name, "the name keys every persisted rank row, so it must never change");
            Assert.AreEqual(70, (int)ClassAbilityId.DispellingEdge);
            Assert.AreEqual(Skill.ItemTinkering, def.AffinitySkill);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
        }

        [TestMethod]
        public void DispellingDance_OneRoll_StripsAndCleanses_ThroughTheSharedCleanseHelper()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Abilities + "DispellingEdgeAbility.cs"), "private static void TryDispel(");

            var roll = body.IndexOf("if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)", StringComparison.Ordinal);
            var strip = body.IndexOf("StripOneBeneficial(target);", StringComparison.Ordinal);
            var cleanse = body.IndexOf("attacker.CleanseOneHarmfulEnchantment();", StringComparison.Ordinal);

            Assert.IsTrue(roll >= 0 && strip > roll && cleanse > strip, "one roll gates both halves");
            Assert.AreEqual(1, Count(body, "ThreadSafeRandom.Next(0.0f"), "exactly one roll");
            StringAssert.Contains(body, "GetClassAbilityAffinityMultiplier(Skill.ItemTinkering)");

            // the self half is the same code the Cleanse weapon mod runs
            var mods = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_WeaponMods.cs");
            StringAssert.Contains(mods, "public Spell CleanseOneHarmfulEnchantment()");
            StringAssert.Contains(mods, "var spell = CleanseOneHarmfulEnchantment();");
        }

        // ---- the four repriced/re-laddered entries all at 1/1/1 ---------------------------------------

        [TestMethod]
        public void TheFiveRepricedEntries_AreAllThreeRanksAtOne()
        {
            foreach (var id in new[] { ClassAbilityId.Sundermark, ClassAbilityId.Spellsurge, ClassAbilityId.Spellstorm, ClassAbilityId.Cascade, ClassAbilityId.DispellingEdge })
            {
                var def = ClassAbilityRegistry.Abilities[id];
                Assert.AreEqual(3, def.MaxRank, $"{id}: max rank");
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank, $"{id}: cost");
            }
        }

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
