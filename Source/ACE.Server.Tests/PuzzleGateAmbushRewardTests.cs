using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A puzzle-gate ambush creature is summoned for free by a wrong lever pull, so its death must pay nothing.
    /// OnDeath and Die need a live world, so their wiring is pinned on source (the PooledLootWiringTests
    /// pattern); the predicate itself is driven directly.
    /// </summary>
    [TestClass]
    public class PuzzleGateAmbushRewardTests
    {
        private const string DeathFile = "Source/ACE.Server/WorldObjects/Creature_Death.cs";
        private const string OnDeathSig = "public virtual DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)";
        private const string DieSig = "protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)";

        [TestMethod]
        public void Predicate_IsKeyedOnTheAmbushFlag()
        {
            Assert.IsTrue(Creature.IsRewardlessDeath(true));
            Assert.IsFalse(Creature.IsRewardlessDeath(false));
        }

        [TestMethod]
        public void OnDeath_ReturnsBeforeEveryOnKillReward()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(DeathFile), OnDeathSig);

            var guard = body.IndexOf("if (IsRewardlessDeath(IsPuzzleAmbush))", StringComparison.Ordinal);
            Assert.IsTrue(guard >= 0, "OnDeath must test the ambush flag");

            var ret = body.IndexOf("return GetDeathMessage(", guard, StringComparison.Ordinal);
            Assert.IsTrue(ret > guard && !body.Substring(guard, ret - guard).Contains("{"), "the guard returns directly");

            foreach (var reward in new[]
            {
                "ApplyCreatureDeathClassAbilities(this)",
                "ApplyWeaponModCreatureDeath(this)",
                "ApplyKillFillVesselCreatureDeath(this)",
                "ApplyMuleFormTokenCreatureDeath(this)",
                "OnDeath_HandleKillTask(KillQuest,",
                "OnDeath_HandleKillTask(KillQuest2,",
                "OnDeath_HandleKillTask(KillQuest3,",
                "DeathSpawner.TrySpawn(this)",
                "OnDeath_GrantXP()",
            })
            {
                var at = body.IndexOf(reward, StringComparison.Ordinal);
                Assert.IsTrue(at > ret, $"'{reward}' must sit after the ambush return");
            }
        }

        [TestMethod]
        public void Die_SuppressesCorpseEmoteAndReports_ForAnAmbush()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(DeathFile), DieSig);

            var decide = body.IndexOf("var rewardless = IsRewardlessDeath(IsPuzzleAmbush);", StringComparison.Ordinal);
            var corpseLocal = body.IndexOf("var suppressCorpse = ", StringComparison.Ordinal);
            var corpseLine = body.Substring(corpseLocal, body.IndexOf(';', corpseLocal) - corpseLocal);

            Assert.IsTrue(decide >= 0 && decide < corpseLocal, "decided before the corpse local");
            StringAssert.Contains(corpseLine, "rewardless", "the corpse (and with it all treasure) is suppressed");

            var emoteGuard = body.IndexOf("if (!rewardless)\n", StringComparison.Ordinal);
            var emote = body.IndexOf("EmoteManager.OnDeath(", StringComparison.Ordinal);
            Assert.IsTrue(emoteGuard >= 0 && emote > emoteGuard && emote - emoteGuard < 80
                && body.Substring(emoteGuard, emote - emoteGuard).Trim().Replace("\n", "").Replace(" ", "") == "if(!rewardless)",
                "the Death emote is the statement guarded by if (!rewardless)");
            StringAssert.Contains(body, "if (!rewardless && GetProperty(PropertyBool.SpeedChallengeBoss) == true)");
            StringAssert.Contains(body, "if (!rewardless && ObjectiveLockKey != null)");
            StringAssert.Contains(body, "if (topDamager.IsPlayer && !rewardless)");

            // Every unguarded call to these would be a second, ungated path.
            Assert.AreEqual(1, Count(body, "EmoteManager.OnDeath("));
            Assert.AreEqual(1, Count(body, "CreateCorpse("));
        }

        /// <summary>The death-path guard is only as good as the line that ARMS it: every ambush must be flagged before EnterWorld.</summary>
        [TestMethod]
        public void Manager_ArmsTheFlagOnEveryAmbushSpawn()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/PuzzleGates/PuzzleGateManager.cs");

            var prepare = PooledLootSourceText.MethodBody(src, "private static string PrepareAmbush(WorldObject wo)");
            StringAssert.Contains(prepare, "creature.IsPuzzleAmbush = true;");

            var spawn = PooledLootSourceText.MethodBody(src, "private static void SpawnAmbush(PuzzleGatePlacement p, Landblock landblock)");
            var create = spawn.IndexOf("CreateWcid(", StringComparison.Ordinal);
            Assert.IsTrue(create >= 0, "SpawnAmbush creates through CreateWcid");
            var call = spawn.Substring(create, spawn.IndexOf(';', create) - create);
            StringAssert.Contains(call, "PrepareAmbush", "the ambush spawn passes PrepareAmbush as its prepare step");
            Assert.AreEqual(1, Count(spawn, "CreateWcid("), "no second, unprepared spawn path");
        }

        private static int Count(string s, string needle)
        {
            var n = 0;

            for (var i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
