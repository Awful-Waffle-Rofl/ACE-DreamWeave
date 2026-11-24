using System.Collections.Generic;
using System.Globalization;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Goldens captured 2026-11-18 from the UNMODIFIED builder (origin/master + docs only, a700f7906) over
    /// FamilyStretchFixture inputs: Limits(), the shipped store and roster, Dungeon(30), Spec(level, seed), new Random(seed),
    /// LevelProfile. Later family-stretch tasks prove "switch off is byte-identical" against these.
    ///
    /// ONE recorded exception (2026-11-21): (200, 2) was a1fbcf4a9d6d6cd81934cd4d (family grievver) and is now
    /// 2fc7db8851ae8f72f7ed70c7 (family golem), because species/slithis.json went "enabled": false and the level-200 draw has one
    /// fewer eligible family (a smaller total for the same single roll). The other nine hashes did not move. Proof the delta is the roster
    /// alone: with the "enabled": false line removed from slithis.json all ten match the original values.
    /// </summary>
    [TestClass]
    public class FamilyStretchGoldenTests
    {
        internal static readonly Dictionary<(int Level, int Seed), string> MasterHashes = new Dictionary<(int, int), string>
        {
            [(150, 1)] = "d3c839ab93ebfdaca39fce64", [(150, 2)] = "8bd4af53fca04678b09cbe57",
            [(200, 1)] = "002feefb5532c9b05b8af872", [(200, 2)] = "2fc7db8851ae8f72f7ed70c7",
            [(300, 1)] = "bac6901ad611e93c8b6663b8", [(300, 2)] = "690aefc51ca2446837e53297",
            [(400, 1)] = "52c18ad604ca242701efa2a6", [(400, 2)] = "4a7909c28e6eb9bba5072808",
            [(480, 1)] = "404a4bb6c6a082a4fd9199de", [(480, 2)] = "2286b400776f6c127de1fb2c",
        };

        [TestInitialize]
        public void Reset() => TestGameTables.EnsureInitialized();

        [TestMethod]
        public void Switch_off_plans_match_origin_master()
        {
            foreach (var kvp in MasterHashes)
            {
                var (l, s) = kvp.Key;
                var plan = FamilyStretchFixture.Build(l, s, FamilyStretchFixture.Limits());
                Assert.AreEqual(kvp.Value, ShippedRosterFixture.Hash(plan), $"level {l} seed {s}: {ShippedRosterFixture.Fingerprint(plan)}");
            }
        }

        private static DungeonBandStandard Anchor()
            => DungeonPopulationBuilder.ThreadsStandardAt(326, FamilyStretchFixture.Limits(), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, FamilyStretchFixture.LevelProfile);

        [TestMethod]
        public void Extrapolate_matches_origin_master()
        {
            var expected = new Dictionary<int, string>
            {
                [400] = "dmg=614|armor=804|attr=1:411,2:415,3:389,4:394,5:404,6:407|eff=6:966,7:871|skill=6:705,7:714,44:728",
                [480] = "dmg=1126|armor=935|attr=1:489,2:500,3:436,4:448,5:471,6:480|eff=6:980,7:907|skill=6:685,7:730,44:738",
            };

            foreach (var kvp in expected)
            {
                var ex = DungeonStatCurve.Extrapolate(Anchor(), 326, kvp.Key, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375,
                    DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300, DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot,
                    DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);
                Assert.AreEqual(kvp.Value, FamilyStretchFixture.StandardText(ex), $"to level {kvp.Key}");
            }
        }

        [TestMethod]
        public void Archetype_extrapolate_matches_origin_master()
        {
            var expected = new Dictionary<int, string>
            {
                [400] = "dmg=614|armor=804|attr=1:411,2:415,3:389,4:394,5:404,6:407|eff=6:966,7:871|skill=6:705,7:714,44:728|175.35810300584",
                [480] = "dmg=1126|armor=935|attr=1:489,2:500,3:436,4:448,5:471,6:480|eff=6:980,7:907|skill=6:685,7:730,44:738|321.8321659776033",
            };

            foreach (var kvp in expected)
            {
                var axes = new ArchetypeAxes(Anchor(), 100.0, 50.0, 10, 5, 0, 0, 0, 0, 1.0);
                var ax = DungeonArchetypeStandards.Extrapolate(axes, 326, kvp.Key, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375,
                    DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300, DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot,
                    DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);
                Assert.AreEqual(kvp.Value, FamilyStretchFixture.StandardText(ax.Core) + "|" + ax.EffectiveDamage.ToString("R", CultureInfo.InvariantCulture), $"to level {kvp.Key}");
            }
        }
    }
}
