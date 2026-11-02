using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The two seams lifted out of ThreadDungeonSpawner for Speed Season 3 (BUILD-PLAN step 3):
    /// ShapeCreature's Rewards = false switch (Creature.StripDeathRewards, RoleNamePrefix) and
    /// DungeonPopulationLimits.WithMonsterCounts.
    ///
    /// A live Creature cannot be constructed under this harness (its constructor needs the world database), so
    /// StripDeathRewards is exercised on an UNINITIALIZED Creature given just a runtime biota and its lock - every
    /// write it makes is a biota property or a plain field, so nothing else of the object is reached. Reads no
    /// PropertyManager key, so there is nothing to seed.
    /// </summary>
    [TestClass]
    public class ThreadDungeonShapeSeamTests
    {
        private static Creature BareCreature(ACE.Entity.Models.Biota biota)
        {
            var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

            var biotaField = typeof(WorldObject).GetField("<Biota>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(biotaField, "WorldObject.Biota is no longer an auto-property; rebuild this fixture");
            biotaField.SetValue(creature, biota);

            var lockField = typeof(WorldObject).GetField("BiotaDatabaseLock", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(lockField, "WorldObject.BiotaDatabaseLock moved; rebuild this fixture");
            lockField.SetValue(creature, new ReaderWriterLockSlim());

            return creature;
        }

        /// <summary>A creature carrying every reward the death path can pay, as a Thread would leave it.</summary>
        private static Creature PayingCreature()
        {
            var creature = BareCreature(new ACE.Entity.Models.Biota
            {
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.XpOverride] = 125000, [PropertyInt.LuminanceAward] = 900 },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { [PropertyDataId.DeathTreasureType] = 453 },
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.CanGenerateRare] = true },
            });

            creature.DeathTreasureOverride = new ACE.Database.Models.World.TreasureDeath { Tier = 8 };
            creature.P_DungeonSalvageAffinities = new List<(int MaterialId, uint BaseWcid, double Chance)> { (20, 3000, 0.5) };
            creature.LootGateLevel = 300;

            return creature;
        }

        [TestMethod]
        public void The_fixture_pays_before_the_switch()
        {
            // Positive control: without it every assertion below could pass on a creature that never paid anything.
            var creature = PayingCreature();

            Assert.AreEqual(125000, creature.XpOverride);
            Assert.AreEqual(900, creature.LuminanceAward);
            Assert.AreEqual(453u, creature.DeathTreasureType);
            Assert.IsNotNull(creature.DeathTreasure, "DeathTreasureOverride wins over the DID, so no database read happens");
            Assert.IsTrue(creature.CanGenerateRare);
            Assert.IsNotNull(creature.P_DungeonSalvageAffinities);
            Assert.IsTrue(Creature.ResolveCanGenerateRare(false, Creature.LootGateLevelFor(creature.LootGateLevel, 300), true, true, false, () => 275),
                "a level-300 gate is rare-eligible for a player killer");
        }

        [TestMethod]
        public void StripDeathRewards_zeroes_everything_the_death_path_pays_from()
        {
            var creature = PayingCreature();

            creature.StripDeathRewards();

            // XP: OnDeath_GrantXP pays (XpOverride ?? 0) x share.
            Assert.AreEqual(0, creature.XpOverride, "XpOverride is SET to 0, not removed");
            Assert.IsTrue(creature.Biota.PropertiesInt.ContainsKey(PropertyInt.XpOverride));

            // Luminance: paid only when non-null.
            Assert.IsNull(creature.LuminanceAward);
            Assert.IsFalse(creature.Biota.PropertiesInt.ContainsKey(PropertyInt.LuminanceAward));

            // Treasure roll: DeathTreasure = override ?? DID lookup; both gone, so null and no database read.
            Assert.IsNull(creature.DeathTreasureOverride);
            Assert.IsNull(creature.DeathTreasureType);
            Assert.IsFalse(creature.Biota.PropertiesDID.ContainsKey(PropertyDataId.DeathTreasureType));
            Assert.IsNull(creature.DeathTreasure);

            // Salvage affinity roll.
            Assert.IsNull(creature.P_DungeonSalvageAffinities);

            // Rares: the flag, and the gate level that CreateCorpse recomputes it from.
            Assert.IsFalse(creature.CanGenerateRare);
            Assert.AreEqual(0, creature.LootGateLevel);
        }

        [TestMethod]
        public void After_the_switch_no_player_killer_can_reopen_the_rare_gate()
        {
            var creature = PayingCreature();
            creature.StripDeathRewards();

            // CreateCorpse: CanGenerateRare = ResolveCanGenerateRare(current, LootGateLevelFor(LootGateLevel, Level), ...)
            // for a player killer. The gate level outranks the creature's live Level, so even a level-500 boss reads 0.
            foreach (var creatureLevel in new int?[] { null, 1, 99, 100, 275, 500 })
                foreach (var killerLevel in new int?[] { null, 1, 50, 275, 500 })
                {
                    var gate = Creature.LootGateLevelFor(creature.LootGateLevel, creatureLevel);
                    Assert.AreEqual(0, gate);
                    Assert.IsFalse(Creature.ResolveCanGenerateRare(creature.CanGenerateRare, gate, true, true, false, () => killerLevel),
                        $"creature level {creatureLevel}, killer level {killerLevel}");
                }
        }

        [TestMethod]
        public void RoleNamePrefix_names_elites_and_bosses_only_when_rewards_are_on()
        {
            Assert.AreEqual("Elite ", ThreadDungeonSpawner.RoleNamePrefix(DungeonRole.Elite, rewards: true));
            Assert.AreEqual("Dread ", ThreadDungeonSpawner.RoleNamePrefix(DungeonRole.Boss, rewards: true));

            foreach (DungeonRole role in Enum.GetValues(typeof(DungeonRole)))
            {
                Assert.IsNull(ThreadDungeonSpawner.RoleNamePrefix(role, rewards: false), $"{role} with rewards off");

                if (role != DungeonRole.Elite && role != DungeonRole.Boss)
                    Assert.IsNull(ThreadDungeonSpawner.RoleNamePrefix(role, rewards: true), $"{role} carries no role word");
            }
        }

        [TestMethod]
        public void ShapeCreature_routes_rewards_through_the_switch_and_guards_the_run_stamp()
        {
            // Source pin: ShapeCreature needs a live plan and PropertyManager, so the wiring is pinned on text.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            var shape = PooledLootSourceText.MethodBody(src, "internal static void ShapeCreature(Creature creature, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, uint observedMaxNonBoss, CreatureShapeOptions options)");

            // Merged 2026-10-24 onto master's PrepareForPlacement extraction: the shaping body (rewards, role word) is
            // PrepareForPlacement's, with the options as its last parameter; ShapeCreature adds only the run stamp.
            var prepared = PooledLootSourceText.MethodBody(src, "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,");

            StringAssert.Contains(prepared, "var rewards = options?.Rewards ?? true;");

            var rewardsIf = prepared.IndexOf("if (rewards)", StringComparison.Ordinal);
            var xp = prepared.IndexOf("creature.XpOverride = (int)Math.Clamp(", StringComparison.Ordinal);
            var profile = prepared.IndexOf("creature.DeathTreasureOverride = plan.Profile;", StringComparison.Ordinal);
            var affinity = prepared.IndexOf("creature.P_DungeonSalvageAffinities = plan.SalvageAffinities;", StringComparison.Ordinal);
            var off = prepared.IndexOf("creature.StripDeathRewards();", StringComparison.Ordinal);
            var name = prepared.IndexOf("RoleNamePrefix(entry.Role, rewards)", StringComparison.Ordinal);

            Assert.IsTrue(rewardsIf >= 0 && rewardsIf < xp && xp < profile && profile < affinity && affinity < off && off < name,
                "the Threads reward writes sit inside if (rewards), Creature.StripDeathRewards is the else, then the role word");
            Assert.AreEqual(1, Count(prepared, "creature.XpOverride ="), "exactly one XP write in the shaping");
            Assert.AreEqual(1, Count(prepared, "StripDeathRewards("));
            Assert.IsFalse(prepared.Contains("\"Elite \" + creature.Name") || prepared.Contains("\"Dread \" + creature.Name"), "role words only through RoleNamePrefix");
            Assert.IsTrue(prepared.Contains("run?.RecordBossHealth(ratio, clamped);"), "no run, no boss-health row");

            var guard = shape.IndexOf("if (options.Run != null)", StringComparison.Ordinal);
            var stamp = shape.IndexOf("creature.SetProperty(PropertyInt.ThreadDungeonRunId, (int)options.Run.RunId);", StringComparison.Ordinal);
            Assert.IsTrue(guard >= 0 && stamp > guard, "the run stamp is written only with a run");
            StringAssert.Contains(shape, "PrepareForPlacement(options.Run, creature, entry, plan, observedMaxNonBoss, out _, options);");
            Assert.IsFalse(shape.Contains("EnterWorld("), "ShapeCreature must not enter the world itself");
        }

        [TestMethod]
        public void WithMonsterCounts_replaces_only_the_two_counts()
        {
            var group = GroupScaling.Compute(3, 1.0, 0.5, 180, 15, 300, 0.05, 1.5, 0.0);
            var original = new DungeonPopulationLimits(120, 2.5, 3.5, 7, 0.4, lootQuantityCap: 1.7, xpScale: 1.9, bossNormalize: false,
                minMonsters: 40, group: group, modifierLevelExponent: 1.3, healthCurveTopLevel: 450, reachUp: false);

            var copy = original.WithMonsterCounts(5, 9);

            Assert.AreEqual(5, copy.MinMonsters);
            Assert.AreEqual(9, copy.MaxMonsters);
            Assert.AreEqual(40, original.MinMonsters, "the source is untouched");
            Assert.AreEqual(120, original.MaxMonsters);
            Assert.AreSame(group, copy.Group, "the private group snapshot is carried");

            var fields = typeof(DungeonPopulationLimits).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsTrue(fields.Length > 10, "the reflection sweep sees the struct's fields");

            foreach (var field in fields.Where(f => f.Name != nameof(DungeonPopulationLimits.MinMonsters) && f.Name != nameof(DungeonPopulationLimits.MaxMonsters)))
                Assert.AreEqual(field.GetValue(original), field.GetValue(copy), $"{field.Name} must be copied unchanged");
        }

        private static int Count(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
