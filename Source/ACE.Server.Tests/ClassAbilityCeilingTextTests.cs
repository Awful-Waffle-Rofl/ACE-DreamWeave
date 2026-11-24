using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    [TestClass]
    public class ClassAbilityCeilingTextTests
    {
        [TestInitialize]
        public void Init() => DefaultPropertyManager.LoadDefaultProperties();

        private static ClassAbilityDefinition Def(params ClassAbilityCeiling[] ceilings) =>
            new ClassAbilityDefinition { Name = "TestAbility", MaxRank = 3, Ceilings = ceilings };

        private static ClassAbilityCeiling Avoidance() => new ClassAbilityCeiling
        {
            Id = "avoidance",
            Bounds = CeilingBounds.Total,
            Source = CeilingSource.Tunable,
            Key = "class_ability_avoidance_cap",
            Label = "max combined block and parry chance",
            Format = "pct",
        };

        private static DictionaryTunableValues Dict(string key, double value) =>
            new DictionaryTunableValues(new Dictionary<string, double> { [key] = value });

        [TestMethod]
        public void CeilingText_Lines_RenderOnePerCeiling_FromDictionaryValues()
        {
            var lines = ClassAbilityCeilingText.Lines(Def(Avoidance()), 1, Dict("class_ability_avoidance_cap", 0.42));
            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("  Limit: max combined block and parry chance 42%.", lines[0]);
        }

        [TestMethod]
        public void Read_Binding_WhenCurrentAtOrAboveMax()
        {
            var r = ClassAbilityCeilings.Read(Def(Avoidance()), "avoidance", 55, 1, ClassAbilityText.Defaults);
            Assert.IsTrue(r.Binding);
            Assert.AreEqual(50.0, r.Max, 1e-9);
            Assert.IsFalse(ClassAbilityCeilings.Read(Def(Avoidance()), "avoidance", 49.9, 1, ClassAbilityText.Defaults).Binding);
        }

        [TestMethod]
        public void Read_UndeclaredId_Throws() =>
            Assert.ThrowsExactly<KeyNotFoundException>(() =>
                ClassAbilityCeilings.Read(Def(Avoidance()), "nope", 1, 1, ClassAbilityText.Defaults));

        [TestMethod]
        public void CeilingText_ZeroValuedCapTunable_RendersNoLimit()
        {
            var v = Dict("class_ability_avoidance_cap", 0.0);
            var lines = ClassAbilityCeilingText.Lines(Def(Avoidance()), 1, v);
            StringAssert.Contains(lines[0], "no limit");
            Assert.IsFalse(lines[0].Contains("0%"), lines[0]);
            Assert.IsFalse(ClassAbilityCeilings.Read(Def(Avoidance()), "avoidance", 99, 1, v).Binding);
        }

        [TestMethod]
        public void GearCeiling_Value_IsStackCapTimesMagnitudeTimesScale()
        {
            var gear = new ClassAbilityCeiling
            {
                Id = "gear",
                Bounds = CeilingBounds.Gear,
                Source = CeilingSource.GearStackCap,
                GearMod = EquipmentModId.AcidProc,
                Label = "max from gear",
                Format = "pct",
            };

            Assert.AreEqual(30.9, gear.Value(Dict("equipment_mod_potency_scale", 1.0), 1), 1e-9);
            Assert.AreEqual(61.8, gear.Value(Dict("equipment_mod_potency_scale", 2.0), 1), 1e-9);
            StringAssert.Contains(ClassAbilityCeilingText.Lines(Def(gear), 1, Dict("equipment_mod_potency_scale", 1.0))[0], "30.9%");
        }

        [TestMethod]
        public void AffinityRateLine_Shared_ReadsLiveRates()
        {
            var def = new ClassAbilityDefinition { Name = "T", MaxRank = 1, AffinitySkill = ACE.Entity.Enum.Skill.Alchemy, AffinityRate = AffinityRateSpec.Shared() };
            Assert.AreEqual("Alchemy adds 9% (trained) / 14% (specialized) of the rank bonus per 100 skill",
                ClassAbilityCeilingText.AffinityRateLine(def, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void AffinityRateLine_Uncapped_SaysNoLimit()
        {
            var def = new ClassAbilityDefinition { Name = "T", MaxRank = 1, AffinitySkill = ACE.Entity.Enum.Skill.Alchemy, AffinityRate = AffinityRateSpec.Uncapped };
            Assert.AreEqual("Alchemy adds 9% (trained) / 14% (specialized) of the rank bonus per 100 skill. No limit on what affinity adds.",
                ClassAbilityCeilingText.AffinityRateLine(def, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void AffinityRateLine_NullRate_WithAffinityCeiling_IsShared()
        {
            var ceiling = new ClassAbilityCeiling { Id = "affinity", Bounds = CeilingBounds.Affinity, Source = CeilingSource.Tunable, Key = "class_ability_affinity_chance_cap", Label = "max chance from affinity", Format = "pct" };
            var def = new ClassAbilityDefinition { Name = "T", MaxRank = 1, AffinitySkill = ACE.Entity.Enum.Skill.Deception, Ceilings = new[] { ceiling } };
            Assert.AreEqual("Deception adds 9% (trained) / 14% (specialized) of the rank bonus per 100 skill",
                ClassAbilityCeilingText.AffinityRateLine(def, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void AffinityRateLine_NullRate_IsShared_RegardlessOfCeilings()
        {
            var def = new ClassAbilityDefinition { Name = "T", MaxRank = 1, AffinitySkill = ACE.Entity.Enum.Skill.Deception };
            Assert.AreEqual("Deception adds 9% (trained) / 14% (specialized) of the rank bonus per 100 skill",
                ClassAbilityCeilingText.AffinityRateLine(def, ClassAbilityText.Defaults));

            var noSkill = new ClassAbilityDefinition { Name = "T", MaxRank = 1 };
            Assert.IsNull(ClassAbilityCeilingText.AffinityRateLine(noSkill, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void AffinityRateLine_ExplicitUncapped_DoubleVolley_SaysNoLimit()
        {
            var def = ClassAbilityRegistry.Abilities[ClassAbilityId.DoubleVolley];
            Assert.AreEqual("Run adds 9% (trained) / 14% (specialized) of the rank bonus per 100 skill. No limit on what affinity adds.",
                ClassAbilityCeilingText.AffinityRateLine(def, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void AffinityRateLine_Own_ReadsOwnKeys_AndNamesTheSkill()
        {
            var v = new DictionaryTunableValues(new Dictionary<string, double>
            {
                ["class_ability_affinity_rate_per_trained"] = 0.12,
                ["class_ability_affinity_rate_per_spec"] = 0.25,
            });
            var own = new ClassAbilityDefinition { Name = "T", MaxRank = 1, AffinitySkill = ACE.Entity.Enum.Skill.Salvaging, AffinityRate = AffinityRateSpec.Own("class_ability_affinity_rate_per_trained", "class_ability_affinity_rate_per_spec") };
            Assert.AreEqual("Salvaging adds 12% (trained) / 25% (specialized) of the rank bonus per 100 skill", ClassAbilityCeilingText.AffinityRateLine(own, v));
            Assert.ThrowsExactly<System.ArgumentNullException>(() => AffinityRateSpec.Own(null, "x"));
            Assert.ThrowsExactly<System.ArgumentNullException>(() => AffinityRateSpec.LegacyAdditive("x", null));
        }

        [TestMethod]
        public void AffinityRateLine_LegacyAdditive_EmpoweredSummonsAndThorns_RealKeys()
        {
            var es = ClassAbilityRegistry.Abilities[ClassAbilityId.EmpoweredSummons];
            Assert.AreEqual("Leadership adds 9% (trained) / 14% (specialized) of the rank bonus per 100 skill; Loyalty adds +1% pet duration per 10 skill (trained) / per 7.5 skill (specialized)",
                ClassAbilityCeilingText.AffinityRateLine(es, ClassAbilityText.Defaults), "Empowered Summons quotes the Leadership primary first, then its Loyalty duration rider");

            var thorns = ClassAbilityRegistry.Abilities[ClassAbilityId.Thorns];
            Assert.AreEqual("Shield adds +1% per 50 skill (trained) / per 36 skill (specialized)",
                ClassAbilityCeilingText.AffinityRateLine(thorns, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void AffinityRateLine_NoSkill_FallsBackToAffinity()
        {
            var def = new ClassAbilityDefinition { Name = "T", MaxRank = 1, AffinityRate = AffinityRateSpec.Shared() };
            Assert.AreEqual("Affinity adds 9% (trained) / 14% (specialized) of the rank bonus per 100 skill", ClassAbilityCeilingText.AffinityRateLine(def, ClassAbilityText.Defaults));
        }

        private static ClassAbilityCeiling ZeroBound() => new ClassAbilityCeiling
        {
            Id = "total", Bounds = CeilingBounds.Total, Source = CeilingSource.Tunable, Key = "class_ability_manabarrier_max_share",
            Label = "max damage diverted to mana", Format = "pct", ZeroIsLimit = true,
        };

        [TestMethod]
        public void ZeroIsLimit_AtZero_RendersZero_AndBinds()
        {
            var v = Dict("class_ability_manabarrier_max_share", 0.0);
            var def = Def(ZeroBound());
            Assert.AreEqual("  Limit: max damage diverted to mana 0%.", ClassAbilityCeilingText.Lines(def, 1, v)[0]);
            Assert.IsFalse(def.Ceilings[0].IsUncapped(v, 1));

            var reading = ClassAbilityCeilings.Read(def, "total", 12.0, 1, v);
            Assert.AreEqual(0.0, reading.Max, 1e-9);
            Assert.IsTrue(reading.Binding);

            var readout = new ClassAbilityReadout { Ceilings = new[] { reading } };
            Assert.AreEqual("Limit: max damage diverted to mana 12% / 0% (at limit)", ClassAbilityCeilingText.ReadingLine(def, readout));
        }

        [TestMethod]
        public void ZeroIsLimitFalse_AtZero_StaysNoLimit_AndNeverBinds()
        {
            var v = Dict("class_ability_avoidance_cap", 0.0);
            var def = Def(Avoidance());
            Assert.IsTrue(def.Ceilings[0].IsUncapped(v, 1));
            var reading = ClassAbilityCeilings.Read(def, "avoidance", 90.0, 1, v);
            Assert.IsFalse(reading.Binding);
            var readout = new ClassAbilityReadout { Ceilings = new[] { reading } };
            Assert.AreEqual("Limit: max combined block and parry chance 90% / no limit", ClassAbilityCeilingText.ReadingLine(def, readout));
        }

        [TestMethod]
        public void ReadingLine_FormatsLikeLines_ForPctIntAndX()
        {
            var pct = Avoidance();
            var intC = new ClassAbilityCeiling { Id = "stacks", Bounds = CeilingBounds.Stacks, Source = CeilingSource.Constant, ConstantValue = _ => 5, Label = "max stacks", Format = "int" };
            var xC = new ClassAbilityCeiling { Id = "attackspeed", Bounds = CeilingBounds.Total, Source = CeilingSource.Constant, ConstantValue = _ => 2.0, Label = "max speed", Format = "x" };
            var def = Def(pct, intC, xC);
            var v = ClassAbilityText.Defaults;
            var readout = new ClassAbilityReadout
            {
                HasValue = true,
                Ceilings = new[]
                {
                    ClassAbilityCeilings.Read(def, "avoidance", 55, 1, v),
                    ClassAbilityCeilings.Read(def, "stacks", 4, 1, v),
                    ClassAbilityCeilings.Read(def, "attackspeed", 1.5, 1, v),
                },
            };

            var line = ClassAbilityCeilingText.ReadingLine(def, readout);
            StringAssert.Contains(line, "max combined block and parry chance 55% / 50% (at limit)");
            StringAssert.Contains(line, "max stacks 4 / 5");
            StringAssert.Contains(line, "max speed 1.5x / 2x");

            // The max in each reading uses the same digits Lines renders.
            var lines = ClassAbilityCeilingText.Lines(def, 1, v);
            StringAssert.Contains(lines[0], "50%");
            StringAssert.Contains(lines[1], "5.");
            StringAssert.Contains(lines[2], "2x");
        }

        [TestMethod]
        public void Sentence_JoinsEveryCeiling_AndIsNullWhenNone()
        {
            var c2 = new ClassAbilityCeiling { Id = "stacks", Bounds = CeilingBounds.Stacks, Source = CeilingSource.Constant, ConstantValue = _ => 5, Label = "max stacks", Format = "int" };
            var def = Def(Avoidance(), c2);
            Assert.AreEqual("Limits: max combined block and parry chance 50%; max stacks 5.", ClassAbilityCeilingText.Sentence(def, ClassAbilityText.Defaults));
            Assert.IsNull(ClassAbilityCeilingText.Sentence(new ClassAbilityDefinition { Name = "T", MaxRank = 1 }, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void TunablePerRank_AppendsRankToPrefix_AndConstantPassesRank()
        {
            var perRank = new ClassAbilityCeiling
            {
                Id = "stacks", Bounds = CeilingBounds.Stacks, Source = CeilingSource.TunablePerRank,
                Key = "class_ability_bloodmage_charge_stack_cap_r", Label = "max stacks", Format = "int",
            };
            var v = ClassAbilityText.Defaults;
            Assert.AreEqual(3.0, perRank.Value(v, 1), 1e-9);
            Assert.AreEqual(4.0, perRank.Value(v, 2), 1e-9);
            Assert.AreEqual(5.0, perRank.Value(v, 3), 1e-9);

            var constant = new ClassAbilityCeiling { Id = "level", Bounds = CeilingBounds.Level, Source = CeilingSource.Constant, ConstantValue = r => 10 * r, Label = "max level", Format = "int" };
            Assert.AreEqual(30.0, constant.Value(v, 3), 1e-9);
            Assert.IsFalse(constant.IsUncapped(v, 3));
        }
    }
}
