using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Second Wind, Mana Well and Cleanse on a combat pet's killing blow (Discord thread 1567935674915623052). The rule
    /// is the pure WeaponModCombat.PetKillPaysOwner, driven directly. Creature.OnDeath needs a live world and a
    /// Player with a session, so its wiring (an else of the killing-player block, queued onto the owner's own action
    /// queue, and nothing but the three utility effects) is pinned against the source text.
    /// </summary>
    [TestClass]
    public class WeaponModPetKillTests
    {
        private const string DeathFile = "Source/ACE.Server/WorldObjects/Creature_Death.cs";
        private const string ModsFile = "Source/ACE.Server/WorldObjects/Player_WeaponMods.cs";
        private const string OnDeathSig = "public virtual DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)";

        // killerIsPlayer, killerWasCombatPet, ownerResolved, victimIsPlayer, victimIsPet

        [TestMethod]
        public void PetKill_OfAMonster_WithALiveOwner_Pays() =>
            Assert.IsTrue(WeaponModCombat.PetKillPaysOwner(false, true, true, false, false));

        /// <summary>
        /// A pet that fired and then expired: its weak reference is gone, so the attacker resolves to null (not a
        /// Player), but the damage record still carries PetOwner. The owner is still paid.
        /// </summary>
        [TestMethod]
        public void PetKill_AfterThePetWasCollected_StillPaysTheOwner() =>
            Assert.IsTrue(WeaponModCombat.PetKillPaysOwner(killerIsPlayer: false, killerWasCombatPet: true, ownerResolved: true, victimIsPlayer: false, victimIsPet: false));

        [TestMethod]
        public void PetKill_OfAPlayer_DoesNotPay() =>
            Assert.IsFalse(WeaponModCombat.PetKillPaysOwner(false, true, true, true, false));

        [TestMethod]
        public void PetKill_OfAnotherPet_DoesNotPay() =>
            Assert.IsFalse(WeaponModCombat.PetKillPaysOwner(false, true, true, false, true));

        [TestMethod]
        public void OwnerlessPet_DoesNotPay() =>
            Assert.IsFalse(WeaponModCombat.PetKillPaysOwner(false, true, false, false, false));

        [TestMethod]
        public void DirectPlayerKill_NeverTakesThePetBranch()
        {
            Assert.IsFalse(WeaponModCombat.PetKillPaysOwner(true, false, false, false, false));
            Assert.IsFalse(WeaponModCombat.PetKillPaysOwner(true, false, true, false, false));
        }

        [TestMethod]
        public void NonPetNonPlayerKiller_DoesNotPay() =>
            Assert.IsFalse(WeaponModCombat.PetKillPaysOwner(false, false, true, false, false));

        [TestMethod]
        public void SourceScan_OnDeath_PetBranchIsTheElseOfTheKillingPlayerBlock_AndQueuesOntoTheOwner()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(DeathFile), OnDeathSig);

            const string blockEnd = "                    killingPlayer.ApplyMuleFormTokenCreatureDeath(this);\n            }\n";
            var end = body.IndexOf(blockEnd, StringComparison.Ordinal);
            Assert.IsTrue(end >= 0, "the killing-player block ends with the mule-token hook");

            // the first statement after the block's closing brace, comments skipped, must be the pet branch's else
            var next = body.Substring(end + blockEnd.Length).Split('\n')
                .Select(l => l.Trim())
                .First(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));

            Assert.AreEqual("else if (ResolveWeaponModPetOwner(lastDamager) is Player petOwner)", next,
                "the pet branch is the ELSE of the killing-player test, so one death pays once");

            var flat = Flat(StripCommentLines(body));

            StringAssert.Contains(flat,
                "else if (ResolveWeaponModPetOwner(lastDamager) is Player petOwner) petOwner.EnqueueAction(new ActionEventDelegate(() => petOwner.ApplyWeaponModPetKill(this)));",
                "the owner's effects are queued onto the owner's own action queue");

            Assert.AreEqual(1, Count(flat, "ApplyWeaponModPetKill("), "one pet-kill payout per death");
            Assert.AreEqual(1, Count(flat, "ApplyWeaponModCreatureDeath("), "the killing-player call is unchanged and alone");
            Assert.AreEqual(0, Count(flat, "petOwner.ApplyCreatureDeathClassAbilities"), "no class abilities on a pet kill");
            Assert.AreEqual(0, Count(flat, "petOwner.ApplyKillFillVesselCreatureDeath"), "no kill-fill vessel on a pet kill");
            Assert.AreEqual(0, Count(flat, "petOwner.ApplyMuleFormTokenCreatureDeath"), "no mule token on a pet kill");

            var rewardless = flat.IndexOf("IsRewardlessDeath(IsPuzzleAmbush)", StringComparison.Ordinal);
            var objective = flat.IndexOf("IsBattlegroundObjectiveCorpselessDeath(BattlegroundObjective)", StringComparison.Ordinal);
            var hook = flat.IndexOf("ApplyWeaponModPetKill(", StringComparison.Ordinal);
            Assert.IsTrue(rewardless >= 0 && objective >= 0 && hook > rewardless && hook > objective, "the early returns still close the pet branch");
        }

        [TestMethod]
        public void SourceScan_Resolver_FeedsThePureRule()
        {
            var body = Flat(StripCommentLines(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(DeathFile),
                "private Player ResolveWeaponModPetOwner(DamageHistoryInfo lastDamager)")));

            // pet identity comes from the damage record's PetOwner, never from the pet's (collectable) weak reference
            StringAssert.Contains(body, "var killerWasCombatPet = lastDamager.PetOwner != null;");
            StringAssert.Contains(body, "var owner = killerWasCombatPet ? lastDamager.TryGetPetOwner() : null;");
            StringAssert.Contains(body,
                "ACE.Server.WeaponMods.WeaponModCombat.PetKillPaysOwner( lastDamager.TryGetAttacker() is Player, killerWasCombatPet, owner != null, this is Player, this is Pet);");
            StringAssert.Contains(body, "return pays ? owner : null;");
            Assert.AreEqual(0, Count(body, "is CombatPet"), "the pet's liveness never decides the payout");
        }

        [TestMethod]
        public void SourceScan_PetKillEntry_SkipsADeadOwner_ThenRunsTheUnchangedThreeEffects()
        {
            var text = PooledLootSourceText.Read(ModsFile);

            Assert.AreEqual("{ if (IsDead) return; ApplyWeaponModCreatureDeath(victim); }",
                Flat(StripCommentLines(PooledLootSourceText.MethodBody(text, "public void ApplyWeaponModPetKill(Creature victim)"))));

            // the shared body is exactly the three utility effects behind the original filter: anything added here
            // (a damage-affecting mod) would also reach pet kills, so it is pinned whole
            Assert.AreEqual(
                "{ if (victim == null || victim == this || PvpClassifier.IsPvp(this, victim) || victim is Pet) return; ApplyWeaponModSecondWind(); ApplyWeaponModManaWell(); ApplyWeaponModCleanse(); }",
                Flat(StripCommentLines(PooledLootSourceText.MethodBody(text, "public void ApplyWeaponModCreatureDeath(Creature victim)"))));
        }

        /// <summary>
        /// Drops every whole-line // comment, so a comment that names a call can neither satisfy nor break a pin
        /// on that call. The source is LF-normalized by PooledLootSourceText.Read.
        /// </summary>
        private static string StripCommentLines(string text) =>
            string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        private static string Flat(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
