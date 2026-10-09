using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The corpse-independent loot rolls both loot models call. Parity is the point: the corpse path and the
    /// pooled path must produce the same categories from the same draws, so the rolls live in one place and
    /// GenerateTreasure is pinned to calling them.
    /// </summary>
    [TestClass]
    public class CreatureLootRollTests
    {
        [TestMethod]
        public void Death_treasure_roll_with_no_profile_is_empty()
        {
            var items = Creature.RollDeathTreasureItems(null);

            Assert.IsNotNull(items);
            Assert.AreEqual(0, items.Count);
        }

        [TestMethod]
        public void Salvage_affinity_draws_once_per_affinity_in_order_and_creates_on_a_pass()
        {
            var affinities = new List<(int MaterialId, uint BaseWcid, double Chance)> { (12, 1001u, 0.1), (23, 1002u, 0.5), (7, 1003u, 0.2) };
            var draws = new Queue<float>(new[] { 0.05f, 0.9f, 0.15f });
            var created = new List<(uint, int, int)>();

            var items = Creature.RollSalvageAffinityItems(affinities, 6, () => draws.Dequeue(),
                (wcid, material, tier) => { created.Add((wcid, material, tier)); return Item(wcid); });

            Assert.AreEqual(0, draws.Count, "exactly one draw per affinity");
            CollectionAssert.AreEqual(new List<(uint, int, int)> { (1001u, 12, 6), (1003u, 7, 6) }, created);
            CollectionAssert.AreEqual(new[] { 1001u, 1003u }, items.Select(i => i.WeenieClassId).ToArray());
        }

        [TestMethod]
        public void Salvage_affinity_skips_a_failed_create_but_still_draws_for_every_affinity()
        {
            var affinities = new List<(int MaterialId, uint BaseWcid, double Chance)> { (12, 1001u, 1.0), (23, 1002u, 1.0) };
            var drawn = 0;

            var items = Creature.RollSalvageAffinityItems(affinities, 3, () => { drawn++; return 0f; },
                (wcid, material, tier) => wcid == 1001u ? null : Item(wcid));

            Assert.AreEqual(2, drawn);
            CollectionAssert.AreEqual(new[] { 1002u }, items.Select(i => i.WeenieClassId).ToArray());
        }

        [TestMethod]
        public void Salvage_affinity_with_no_affinities_draws_nothing()
        {
            var items = Creature.RollSalvageAffinityItems(null, 6, () => throw new AssertFailedException("must not draw"), null);

            Assert.AreEqual(0, items.Count);
        }

        private static Func<int?> Never => () => throw new AssertFailedException("the killer level must not be read");

        [TestMethod]
        public void Rare_rule_matches_CreateCorpse()
        {
            Assert.IsFalse(Creature.ResolveCanGenerateRare(true, 200, killerPresent: false, false, false, Never), "no killer -> false");
            Assert.IsFalse(Creature.ResolveCanGenerateRare(true, 200, true, killerIsPlayer: false, false, Never), "non-player killer -> false");
            Assert.IsFalse(Creature.ResolveCanGenerateRare(true, 200, true, true, killerIsOlthoiPlayer: true, Never), "Olthoi killer -> false");
            Assert.IsTrue(Creature.ResolveCanGenerateRare(false, 100, true, true, false, Never), "level 100 creature -> true without reading the killer");
            Assert.IsTrue(Creature.ResolveCanGenerateRare(false, 50, true, true, false, () => 40), "creature above killer -> true");
            Assert.IsTrue(Creature.ResolveCanGenerateRare(true, 50, true, true, false, () => 60), "creature below killer keeps the current value (true)");
            Assert.IsFalse(Creature.ResolveCanGenerateRare(false, 50, true, true, false, () => 60), "creature below killer keeps the current value (false)");
            Assert.IsTrue(Creature.ResolveCanGenerateRare(true, 50, true, true, false, () => null), "killer object gone keeps the current value");
            Assert.IsFalse(Creature.ResolveCanGenerateRare(false, null, true, true, false, () => 10), "unlevelled creature keeps the current value");
        }

        [TestMethod]
        public void GenerateTreasure_and_CreateCorpse_call_the_shared_rolls()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");

            var treasure = PooledLootSourceText.MethodBody(src, "private List<WorldObject> GenerateTreasure(DamageHistoryInfo killer, Corpse corpse)");
            StringAssert.Contains(treasure, "RollDeathTreasureItems(DeathTreasure)");
            StringAssert.Contains(treasure, "RollSalvageAffinityItems(P_DungeonSalvageAffinities, DeathTreasure?.Tier ?? 1)");
            Assert.IsFalse(treasure.Contains("LootGenerationFactory.CreateRandomLootObjects"), "the DeathTreasure roll must go through the shared static");
            Assert.IsFalse(treasure.Contains("ThreadSafeRandom.Next(0.0f, 1.0f)"), "the affinity draw must go through the shared static");

            var corpse = PooledLootSourceText.MethodBody(src, "void CreateCorpse(DamageHistoryInfo killer");
            // The old write pattern, exactly: a non-candidate killer clears the flag, a qualifying player killer
            // sets it, and a player killer that does not qualify writes nothing (no same-value re-assignment).
            // lootKiller, not killer: resolved through DamageHistoryInfo.ResolvePetOwnerAsKiller so a CombatPet's
            // kill reads as its owner's.
            StringAssert.Contains(corpse, "if (!(lootKiller != null && lootKiller.IsPlayer && !lootKiller.IsOlthoiPlayer))\n                    CanGenerateRare = false;");
            StringAssert.Contains(corpse, "else if (ResolveCanGenerateRare(false, LootGateLevelFor(LootGateLevel, Level), true, true, false, () => lootKiller.TryGetAttacker()?.Level))\n                    CanGenerateRare = true;");
            Assert.IsFalse(corpse.Contains("CanGenerateRare = ResolveCanGenerateRare("), "the keep-current branch must not re-assign the flag");
        }

        [TestMethod]
        public void CreateCorpse_resolves_the_pet_owner_before_rolling_the_rare_and_the_treasure_map()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");

            var corpse = PooledLootSourceText.MethodBody(src, "void CreateCorpse(DamageHistoryInfo killer");

            // The local must be resolved through the pet-owner helper, and TryGenerateRare / TryDropTreasureMap /
            // MlRelariaTrophy.TryDropTrophy must all be passed that resolved local, not the raw killer -
            // otherwise a pet's kill stays ineligible for rares, treasure maps and the Relaria trophy alike.
            StringAssert.Contains(corpse, "lootKiller = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);");
            StringAssert.Contains(corpse, "corpse.TryGenerateRare(lootKiller);");
            StringAssert.Contains(corpse, "MlTreasureDrop.TryDropTreasureMap(corpse, lootKiller, corpseLandblock, corpseRealm, Level ?? 0);");
            StringAssert.Contains(corpse, "MlRelariaTrophy.TryDropTrophy(this, corpse, lootKiller);");
        }
    }
}
