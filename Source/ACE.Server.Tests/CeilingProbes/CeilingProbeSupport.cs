using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Scaffolding shared by the per-class ceiling probe files and readout presence tests, so every class batch
    /// builds probes and presence checks the same way. Use with <c>using static ACE.Server.Tests.CeilingProbeSupport;</c>.
    /// </summary>
    public static class CeilingProbeSupport
    {
        /// <summary>A multiplier far past any cap, so the clamp is what decides the result.</summary>
        public const double Saturating = 1000.0;

        /// <summary>The declared ceiling of an ability by id.</summary>
        public static ClassAbilityCeiling Ceiling(ClassAbilityId ability, string id) =>
            ClassAbilityRegistry.Abilities[ability].Ceilings.First(c => c.Id == id);

        /// <summary>The declared ceiling's raw Value at default tunables (rank 3).</summary>
        public static double Max(ClassAbilityId ability, string id) => Ceiling(ability, id).Value(ClassAbilityText.Live, 3);

        /// <summary>Helpers that return float lose double precision; compare in float.</summary>
        public static double F(double x) => (float)x;

        public static double Tunable(string key) => PropertyManager.GetDouble(key).Item;

        /// <summary>The applied gear value of a mod with 99 perfect rolls stacked, after the StackCap.</summary>
        public static double GearAtSaturation(EquipmentModId id)
        {
            var def = EquipmentModRegistry.Get(id);
            return EquipmentModValue.CapPotencySum(def, 99.0) * EquipmentModValue.MagnitudePerPotency(def);
        }

        private static IEnumerable<ClassAbilityDefinition> Declared(ClassAbilityClass cls) =>
            ClassAbilityRegistry.Abilities.Values.Where(d => d.AbilityClass == cls && d.Ceilings != null && d.Ceilings.Count > 0);

        private static IAbilityReadout Handler(ClassAbilityId id) =>
            (IAbilityReadout)ClassAbilityRegistry.Handlers.First(h => h.Definition.Id == id);

        /// <summary>Every declared ceiling of the class (bar the unsupported abilities) shows in GetReadout(null) at its declared max.</summary>
        public static void AssertReadoutReportsEveryDeclaredCeiling(ClassAbilityClass cls, IReadOnlyDictionary<ClassAbilityId, string> nullPlayerUnsupported)
        {
            Assert.IsTrue(Declared(cls).Any(), $"no {cls} ability declares a ceiling");

            foreach (var def in Declared(cls).Where(d => !nullPlayerUnsupported.ContainsKey(d.Id)))
            {
                const int rank = 3;
                var readout = Handler(def.Id).GetReadout(null, rank);

                Assert.IsNotNull(readout.Ceilings, $"{def.Name}: GetReadout carries no ceilings");
                CollectionAssert.AreEquivalent(def.Ceilings.Select(c => c.Id).ToList(), readout.Ceilings.Select(r => r.Id).ToList(), $"{def.Name}: a declared ceiling has no reading");

                foreach (var ceiling in def.Ceilings)
                {
                    var reading = readout.Ceilings.Single(r => r.Id == ceiling.Id);
                    Assert.AreEqual(ceiling.DisplayValue(ClassAbilityText.Live, rank), reading.Max, 1e-9, $"{def.Name}/{ceiling.Id}: reading Max is not the declared max");
                }
            }
        }

        /// <summary>Each NullPlayerUnsupported entry still declares a ceiling and still throws on a null player.</summary>
        public static void AssertNullPlayerUnsupportedIsHonest(ClassAbilityClass cls, IReadOnlyDictionary<ClassAbilityId, string> nullPlayerUnsupported)
        {
            var declaredIds = Declared(cls).Select(d => d.Id).ToHashSet();

            foreach (var id in nullPlayerUnsupported.Keys)
            {
                Assert.IsTrue(declaredIds.Contains(id), $"{id} is listed unsupported but declares no ceiling");
                Assert.ThrowsExactly<NullReferenceException>(() => Handler(id).GetReadout(null, 3), $"{id} no longer throws on a null player: remove it from NullPlayerUnsupported");
            }
        }
    }
}
