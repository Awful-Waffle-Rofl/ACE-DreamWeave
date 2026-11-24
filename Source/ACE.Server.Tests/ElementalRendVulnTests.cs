using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The RETURNED Elemental Rend (Archmage T3, ElementalRendVulnAbility), added 2026-11-03 beside Elemental
    /// Proficiency. Pins the new internals (so they can never collide with the Proficiency ability that still
    /// owns ClassAbilityId.ElementalRend 42 / "elementalrend" / EquipmentModId.ElementalRend 23), the approved
    /// 10/18/26% chance table, the affinity cap, the gear-mod contribution and its stack cap, the token wcids,
    /// and the PvP exclusion. Player's static initializer cannot run in this host, so the handler body is pinned
    /// by source text rather than called. Every key read is seeded to its shipped default and restored in a
    /// finally, so the class passes alone and in any order.
    /// </summary>
    [TestClass]
    public class ElementalRendVulnTests
    {
        private const string BaseKey = "class_ability_elementalrend_chance_base";
        private const string StepKey = "class_ability_elementalrend_chance_step";
        private const string CapKey = "class_ability_affinity_chance_cap";

        private static readonly string[] Keys = { BaseKey, StepKey, CapKey };

        private static void WithSeededKeys(Action body)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            try
            {
                foreach (var k in Keys)
                    Assert.IsTrue(PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item), k);

                body();
            }
            finally
            {
                foreach (var k in Keys)
                    PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item);
            }
        }

        private static string Read(string path) => PooledLootSourceText.Read(path);

        // ---- definition and identity ----------------------------------------------------------------------

        [TestMethod]
        public void Definition_IsTheReturnedRend_WithFreshInternals()
        {
            var def = new ElementalRendVulnAbility().Definition;

            Assert.AreEqual(ClassAbilityId.ElementalRendVuln, def.Id);
            Assert.AreEqual(97, (int)def.Id);
            Assert.AreEqual("elementalrendvuln", def.Name, "Name is the persisted quest-key suffix");
            Assert.AreEqual("Elemental Rend", def.DisplayName);
            Assert.AreEqual(ClassAbilityClass.Archmage, def.AbilityClass);
            Assert.AreEqual(3, def.Tier);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
            Assert.IsTrue(def.Implemented);
            Assert.AreEqual(Skill.ArcaneLore, def.AffinitySkill);
        }

        [TestMethod]
        public void Identity_NeverCollidesWithElementalProficiency()
        {
            var rend = new ElementalRendVulnAbility().Definition;
            var proficiency = new ElementalRendAbility().Definition;

            // the Proficiency ability keeps the OLD Rend internals untouched
            Assert.AreEqual(42, (int)proficiency.Id);
            Assert.AreEqual("elementalrend", proficiency.Name);
            Assert.AreEqual("Elemental Proficiency", proficiency.DisplayName);

            Assert.AreNotEqual(proficiency.Id, rend.Id);
            Assert.AreNotEqual(proficiency.Name, rend.Name);
            Assert.AreNotEqual(ClassAbilityRegistry.QuestKey(proficiency), ClassAbilityRegistry.QuestKey(rend));
            Assert.AreEqual("ClassAbility_elementalrendvuln", ClassAbilityRegistry.QuestKey(rend));
        }

        [TestMethod]
        public void Registry_HoldsBothAbilities_AndRendIsASpellHitHandler()
        {
            Assert.IsTrue(ClassAbilityRegistry.Abilities.ContainsKey(ClassAbilityId.ElementalRend));
            Assert.IsTrue(ClassAbilityRegistry.Abilities.ContainsKey(ClassAbilityId.ElementalRendVuln));

            Assert.IsTrue(ClassAbilityRegistry.SpellHitAbilities.Any(a => a is ElementalRendVulnAbility));
            Assert.IsTrue(ClassAbilityRegistry.SpellHitAbilities.Any(a => a is ElementalRendAbility),
                "Proficiency still dispatches; the two stack");
        }

        [TestMethod]
        public void Description_PromisesNoNetherOrHealthCoverage()
        {
            var text = ClassAbilityText.Describe(new ElementalRendVulnAbility().Definition, ClassAbilityText.Defaults);

            var definition = new ElementalRendVulnAbility().Definition;
            Assert.AreEqual("10% chance", ClassAbilityText.RankTail(definition, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("26% chance", ClassAbilityText.RankTail(definition, 3, ClassAbilityText.Defaults));
            StringAssert.Contains(text, "Only elemental damage types have a matching Vulnerability");
            Assert.AreEqual("Affinity: Arcane Lore", ClassAbilityText.AffinityLine(definition));
        }

        // ---- the proc chance ------------------------------------------------------------------------------

        [TestMethod]
        public void Tunables_AreRegisteredAtTheApprovedDefaults()
        {
            WithSeededKeys(() =>
            {
                Assert.AreEqual(0.10, PropertyManager.GetDouble(BaseKey).Item, 1e-9);
                Assert.AreEqual(0.08, PropertyManager.GetDouble(StepKey).Item, 1e-9);
            });
        }

        [TestMethod]
        public void RendChance_PerRank_IsTenEighteenTwentySix()
        {
            WithSeededKeys(() =>
            {
                var chanceBase = PropertyManager.GetDouble(BaseKey).Item;
                var chanceStep = PropertyManager.GetDouble(StepKey).Item;

                Assert.AreEqual(0.10f, ElementalRendVulnAbility.RendChance(1, chanceBase, chanceStep, 0.0), 1e-5f);
                Assert.AreEqual(0.18f, ElementalRendVulnAbility.RendChance(2, chanceBase, chanceStep, 0.0), 1e-5f);
                Assert.AreEqual(0.26f, ElementalRendVulnAbility.RendChance(3, chanceBase, chanceStep, 0.0), 1e-5f);
                // unlearned -> no chance, whatever the rider or the gear
                Assert.AreEqual(0.0f, ElementalRendVulnAbility.RendChance(0, chanceBase, chanceStep, 0.5, 0.06), 1e-5f);
            });
        }

        [TestMethod]
        public void RankBonus_MatchesTheChanceTable()
        {
            Assert.AreEqual(0.0, ElementalRendVulnAbility.RankBonus(0, 0.10, 0.08), 1e-12);
            Assert.AreEqual(0.10, ElementalRendVulnAbility.RankBonus(1, 0.10, 0.08), 1e-12);
            Assert.AreEqual(0.18, ElementalRendVulnAbility.RankBonus(2, 0.10, 0.08), 1e-12);
            Assert.AreEqual(0.26, ElementalRendVulnAbility.RankBonus(3, 0.10, 0.08), 1e-12);
        }

        [TestMethod]
        public void RendChance_AffinityCap_BoundsAnAbsurdRider_ButNotTheGearMod()
        {
            // an uncapped rider of +209 points pushes the roll past certainty (the live bug the cap exists for)
            Assert.IsTrue(ElementalRendVulnAbility.RendChance(3, 0.08, 0.06, 2.09) >= 1.0f);

            // capped at 20 points: 0.20 + 0.20
            Assert.AreEqual(0.40f, ElementalRendVulnAbility.RendChance(3, 0.08, 0.06, 2.09, 0.0, 0.20), 1e-5f);
            // the gear mod is added OUTSIDE the cap
            Assert.AreEqual(0.45f, ElementalRendVulnAbility.RendChance(3, 0.08, 0.06, 2.09, 0.05, 0.20), 1e-5f);
            // a rider under the cap passes through untouched
            Assert.AreEqual(0.36f, ElementalRendVulnAbility.RendChance(3, 0.08, 0.06, 0.16, 0.0, 0.20), 1e-5f);
            // cap 0 = uncapped
            Assert.AreEqual(2.29f, ElementalRendVulnAbility.RendChance(3, 0.08, 0.06, 2.09, 0.0, 0.0), 1e-5f);
        }

        [TestMethod]
        public void RendChance_GearMod_AddsPercentagePointsInsideTheAbilitysOwnRoll()
        {
            // 10% rank-1 chance + one perfect 2-point roll
            Assert.AreEqual(0.12f, ElementalRendVulnAbility.RendChance(1, 0.10, 0.08, 0.0, 0.02), 1e-6f);
            // three perfect rolls (the stack cap) on rank 3: 26% + 6 points
            Assert.AreEqual(0.32f, ElementalRendVulnAbility.RendChance(3, 0.10, 0.08, 0.0, 0.06), 1e-6f);
            // no gear is bit-identical to the pre-mod behaviour
            Assert.AreEqual(0.10f, ElementalRendVulnAbility.RendChance(1, 0.10, 0.08, 0.0));
            // a negative gear value never subtracts
            Assert.AreEqual(0.10f, ElementalRendVulnAbility.RendChance(1, 0.10, 0.08, 0.0, -0.5), 1e-6f);
        }

        [TestMethod]
        public void Readout_NullPlayer_ReportsTheBareRankChance()
        {
            WithSeededKeys(() =>
            {
                var rend = new ElementalRendVulnAbility();

                foreach (var (rank, expected) in new[] { (1, 10.0), (2, 18.0), (3, 26.0) })
                {
                    var r = rend.GetReadout(null, rank);
                    Assert.IsTrue(r.HasValue);
                    Assert.AreEqual(expected, r.Skill, 1e-9);
                    Assert.AreEqual(0.0, r.Affinity, 1e-9);
                    Assert.AreEqual(0.0, r.Gear, 1e-9);
                    Assert.AreEqual(expected, r.Effective, 1e-9);
                    Assert.AreEqual("%", r.Unit);
                    Assert.IsNull(r.CapNote);
                    Assert.AreEqual(r.Skill + r.Affinity + r.Gear, r.Effective, 1e-9);
                }
            });
        }

        // ---- the gear mod ---------------------------------------------------------------------------------

        [TestMethod]
        public void GearMod_IsTheOldRendRowUnderNewIds()
        {
            var mod = EquipmentModRegistry.Get(EquipmentModId.ElementalRendVuln);

            Assert.AreEqual(50, (int)EquipmentModId.ElementalRendVuln);
            Assert.AreEqual(8169, (int)PropertyFloat.GearModElementalRendVuln);
            Assert.AreEqual(PropertyFloat.GearModElementalRendVuln, mod.Property);
            Assert.AreEqual("Elemental Rend", mod.DisplayName);
            Assert.AreEqual(ClassAbilityId.ElementalRendVuln, mod.LinkedAbility);
            Assert.IsFalse(mod.Standalone, "machinery: needs the ability's rank");
            Assert.AreEqual(0.02, mod.MaxMagnitude, 1e-12, "one perfect roll = +2 percentage points");
            Assert.AreEqual(3.0, mod.StackCap, 1e-12);
            Assert.AreEqual(EquipmentModHookKind.AbilityMachinery, mod.HookKind);
            Assert.AreEqual("+2% vulnerability proc chance", mod.Format(0.02));
        }

        [TestMethod]
        public void GearMod_StackCapBoundsTheSixRollSumAtSixPoints()
        {
            var mod = EquipmentModRegistry.Get(EquipmentModId.ElementalRendVuln);
            var sixPerfect = 6.0;   // six perfect-potency items
            var capped = mod.StackCap > 0.0 ? Math.Min(sixPerfect, mod.StackCap) : sixPerfect;

            Assert.AreEqual(3.0, capped, 1e-12);
            Assert.AreEqual(0.06, capped * EquipmentModValue.MagnitudePerPotency(mod), 1e-9, "stack cap = +6 points of proc chance");
        }

        [TestMethod]
        public void GearMod_DoesNotDisturbTheProficiencyAmplifierRow()
        {
            var proficiency = EquipmentModRegistry.Get(EquipmentModId.ElementalRend);

            Assert.AreEqual(23, (int)EquipmentModId.ElementalRend);
            Assert.AreEqual(PropertyFloat.GearModElementalRend, proficiency.Property);
            Assert.AreEqual(ClassAbilityId.ElementalRend, proficiency.LinkedAbility);
            Assert.AreEqual("Elemental Proficiency", proficiency.DisplayName);
            Assert.AreEqual(0.05, proficiency.MaxMagnitude, 1e-12);
            Assert.AreEqual("+5% elemental effect", proficiency.Format(0.05));

            Assert.AreNotEqual(proficiency.Property, EquipmentModRegistry.Get(EquipmentModId.ElementalRendVuln).Property);
        }

        // ---- handler wiring (source scan) -----------------------------------------------------------------

        [TestMethod]
        public void Handler_ReadsItsOwnGearModNotProficienciesAndRefusesPlayers()
        {
            var src = Read("Source/ACE.Server/ClassAbilities/Abilities/ElementalRendVulnAbility.cs");

            StringAssert.Contains(src, "GetEquippedModValue(EquipmentModId.ElementalRendVuln)");
            Assert.IsFalse(src.Contains("GetEquippedModValue(EquipmentModId.ElementalRend)"),
                "must not read the Proficiency amplifier mod");
            StringAssert.Contains(src, "if (primaryTarget is Player)");
            StringAssert.Contains(src, "GetClassAbilityAffinityMultiplier(Skill.ArcaneLore)");
            StringAssert.Contains(src, "var arcaneLore = rankBonus * affinity - rankBonus;", "the rider is the AMOUNT the multiplier adds");
            StringAssert.Contains(src, "ElementalRendAbility.GetVulnerabilitySpell(");
            Assert.IsFalse(src.Contains("IsClassAbilitySpawned"),
                "fires on Spell AOE and Echo Cast wisp casts: it spawns no projectile, so no cascade risk");
        }

        [TestMethod]
        public void Dispatch_DropsPlayerTargetsBeforeAnyHandlerRuns()
        {
            var src = Read("Source/ACE.Server/WorldObjects/Player_ClassAbilities.cs");

            StringAssert.Contains(src, "learned.Length == 0 || target == null || target is Player");
        }

        // ---- tier structure -------------------------------------------------------------------------------

        /// <summary>
        /// DESIGN.md section 3's structural rule is 4-5 entries at T1, 3-4 at T2, 2-3 at T3. Spellsword T2 was
        /// widened to 3-5 by the owner ruling of 2026-10-30 (SpellswordAbilityDefinitionTests). Archmage T3 is
        /// widened the same way: the returned Elemental Rend sits BESIDE Elemental Proficiency, so T3 holds five
        /// entries (Echo Cast, Elemental Proficiency, Mana Barrier, Volatile Wall, Elemental Rend) - owner ruling
        /// 2026-11-03. Every other class and tier keeps its DESIGN.md band.
        /// </summary>
        [TestMethod]
        public void ArchmageTierCounts_T3IsWidenedToFive_ByOwnerRuling()
        {
            var defs = ClassAbilityRegistry.Abilities.Values.ToList();
            var t1 = ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Archmage, 1);
            var t2 = ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Archmage, 2);
            var t3 = ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Archmage, 3);

            Assert.IsTrue(t1 >= 4 && t1 <= 5, $"T1 entry count {t1} is outside the 4-5 structural rule");
            Assert.IsTrue(t2 >= 3 && t2 <= 4, $"T2 entry count {t2} is outside the 3-4 structural rule");
            // Archmage T3 band widened from 2-3 to 2-5: owner ruling 2026-11-03.
            Assert.IsTrue(t3 >= 2 && t3 <= 5, $"T3 entry count {t3} is outside the 2-5 structural rule");

            Assert.AreEqual(5, t1);
            Assert.AreEqual(4, t2);
            Assert.AreEqual(5, t3);

            var t3Names = defs
                .Where(d => d.AbilityClass == ClassAbilityClass.Archmage && d.Tier == 3)
                .Select(d => d.DisplayName)
                .ToList();
            CollectionAssert.AreEquivalent(
                new[] { "Echo Cast", "Elemental Proficiency", "Mana Barrier", "Volatile Wall", "Elemental Rend" },
                t3Names);
        }

        // ---- token catalog --------------------------------------------------------------------------------

        [TestMethod]
        public void Tokens_MintTheReservedWcids_AndLeaveProficiencysAlone()
        {
            Assert.AreEqual(1002216u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ElementalRendVuln, 1));
            Assert.AreEqual(1002217u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ElementalRendVuln, 2));
            Assert.AreEqual(1002218u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ElementalRendVuln, 3));

            // Elemental Proficiency's committed tokens did not move
            Assert.AreEqual(1000818u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ElementalRend, 1));
            Assert.AreEqual(1000820u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ElementalRend, 3));

            Assert.IsTrue(ClassAbilityTokenCatalog.TryResolve(ClassAbilityId.ElementalRendVuln, 3, out var offering));
            Assert.AreEqual(1002218u, offering.Wcid);
            Assert.IsFalse(ClassAbilityTokenCatalog.TryResolve(ClassAbilityId.ElementalRendVuln, 4, out _));
        }
    }
}
