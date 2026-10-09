using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Phase B item 1 of Docs/Pvp/ATTACK-DEFEND.md "Death": a tagged crystal's death path. Creature.Die and Creature.OnDeath need a
    /// live world, so their wiring is pinned on source, with each scan BINDING the predicate identifier and proven to discriminate by
    /// running the same check on a copy with that identifier substituted. Untagged creatures are covered by the predicate answering
    /// false and by the existing death-path tests (PooledLootWiringTests and the rest) staying green and unmodified.
    /// </summary>
    [TestClass]
    public class AttackDefendDeathWiringTests
    {
        private const string Predicate = "IsBattlegroundObjectiveCorpselessDeath";

        private const string DieSignature = "protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)";

        private const string OnDeathSignature = "public virtual DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)";

        private static string Source() => PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");

        [TestMethod]
        public void ThePredicate_IsTrueOnlyForATaggedCreature()
        {
            Assert.IsFalse(Creature.IsBattlegroundObjectiveCorpselessDeath(null), "an untagged creature keeps the full death path");
            Assert.IsTrue(Creature.IsBattlegroundObjectiveCorpselessDeath(new BattlegroundObjectiveTag(Guid.NewGuid(), 0, 1)));
        }

        // ---------------- Die ----------------

        /// <summary>
        /// The Die wiring, as a check over a method body: the predicate is decided once into a local, the destruction report sits
        /// behind it, the local is OR-ed into suppressCorpse, the creature-kill credit is skipped for it, and the corpse step is
        /// still the single "if (!suppressCorpse)" call. Returns the first missing piece, or null when all hold.
        /// </summary>
        private static string DieWiringFault(string body)
        {
            var decide = body.IndexOf($"var objectiveDeath = {Predicate}(BattlegroundObjective);", StringComparison.Ordinal);

            if (decide < 0)
                return "the predicate is not decided into objectiveDeath";

            var report = body.IndexOf(ReportStep, decide, StringComparison.Ordinal);

            if (report < 0)
                return "the destruction report is not behind objectiveDeath";

            var suppressStart = body.IndexOf("var suppressCorpse = ", StringComparison.Ordinal);
            var suppressEnd = suppressStart < 0 ? -1 : body.IndexOf(';', suppressStart);

            if (suppressStart < decide || suppressEnd < 0 || !body.Substring(suppressStart, suppressEnd - suppressStart).Contains("|| objectiveDeath"))
                return "objectiveDeath is not OR-ed into suppressCorpse";

            if (!body.Contains("if (topDamagerPlayer != null && !objectiveDeath)"))
                return "the creature-kill credit is not skipped for an objective";

            var skip = body.IndexOf(CorpseStep, StringComparison.Ordinal);

            if (skip < suppressEnd)
                return "the corpse step does not read suppressCorpse after it is decided";

            if (body.IndexOf(CorpseStep, skip + 1, StringComparison.Ordinal) >= 0)
                return "the corpse step is not a single \"if (!suppressCorpse)\"";

            return null;
        }

        private const string CorpseStep = "if (!suppressCorpse)\n                    CreateCorpse(topDamager);";

        private const string ReportStep = "if (objectiveDeath)\n                ReportBattlegroundObjectiveDestroyed(lastDamager);";

        [TestMethod]
        public void Die_DecidesTheObjectivePredicate_ReportsOnce_AndSuppressesOnlyTheCorpse()
        {
            var body = PooledLootSourceText.MethodBody(Source(), DieSignature);

            Assert.IsNull(DieWiringFault(body));

            // The report runs after the exactly-once dieEntered guard, so a crystal is reported once.
            Assert.IsTrue(body.IndexOf("dieEntered = true;", StringComparison.Ordinal) < body.IndexOf("ReportBattlegroundObjectiveDestroyed(lastDamager);", StringComparison.Ordinal));

            Assert.IsFalse(body.Contains("NoCorpse"), "never the engine corpse property: it still rolls treasure");
        }

        [TestMethod]
        public void Die_WiringCheck_Discriminates_WhenThePredicateIsSubstituted()
        {
            var body = PooledLootSourceText.MethodBody(Source(), DieSignature);

            Assert.IsNotNull(DieWiringFault(body.Replace(Predicate, "IsDigsiteCorpselessDeath")), "a different predicate must fail the check");
            Assert.IsNotNull(DieWiringFault(body.Replace("|| objectiveDeath;", ";")), "dropping the OR from suppressCorpse must fail the check");
            Assert.IsNotNull(DieWiringFault(body.Replace("if (topDamagerPlayer != null && !objectiveDeath)", "if (topDamagerPlayer != null)")), "crediting the kill must fail the check");
        }

        /// <summary>The two sub-checks the substitutions above do not reach: the report behind objectiveDeath, and the single corpse step.</summary>
        [TestMethod]
        public void Die_WiringCheck_Discriminates_OnTheReportGuardAndTheSingleCorpseStep()
        {
            var body = PooledLootSourceText.MethodBody(Source(), DieSignature);
            Assert.IsTrue(body.Contains(ReportStep) && body.Contains(CorpseStep), "fixture: both steps are present as pinned");

            // The report behind a different guard (here: every pooled-loot death) or no guard at all.
            Assert.IsNotNull(DieWiringFault(body.Replace(ReportStep, "if (pooledLoot)\n                ReportBattlegroundObjectiveDestroyed(lastDamager);")),
                "a report behind another guard must fail the check");
            Assert.IsNotNull(DieWiringFault(body.Replace(ReportStep, "ReportBattlegroundObjectiveDestroyed(lastDamager);")),
                "an unguarded report must fail the check");

            // The corpse step gated on another value, or a second corpse step that ignores suppressCorpse.
            Assert.IsNotNull(DieWiringFault(body.Replace(CorpseStep, "if (!pooledLoot)\n                    CreateCorpse(topDamager);")),
                "a corpse step on another value must fail the check");
            Assert.IsNotNull(DieWiringFault(body.Replace(CorpseStep, CorpseStep + "\n" + CorpseStep)),
                "a duplicated corpse step must fail the check");
        }

        // ---------------- OnDeath ----------------

        /// <summary>The OnDeath early branch: after the onDeathEntered latch, before every killer hook, kill quest, death spawn and XP grant.</summary>
        private static string OnDeathWiringFault(string body)
        {
            var branch = body.IndexOf($"if ({Predicate}(BattlegroundObjective))\n                return GetDeathMessage(lastDamager, damageType, criticalHit);", StringComparison.Ordinal);

            if (branch < 0)
                return "no early branch on the predicate";

            if (body.IndexOf("onDeathEntered = true;", StringComparison.Ordinal) > branch)
                return "the branch runs before the onDeathEntered latch";

            foreach (var later in new[] { "ApplyCreatureDeathClassAbilities(this)", "ApplyKillFillVesselCreatureDeath(this)", "ApplyMuleFormTokenCreatureDeath(this)",
                "OnDeath_HandleKillTask(KillQuest,", "DeathSpawner.TrySpawn(this)", "OnDeath_GrantXP()" })
            {
                var at = body.IndexOf(later, StringComparison.Ordinal);

                if (at < 0 || at < branch)
                    return $"{later} is not behind the branch";
            }

            return null;
        }

        [TestMethod]
        public void OnDeath_SkipsKillerHooksQuestsDeathSpawnAndXp_ForATaggedCrystal()
        {
            var body = PooledLootSourceText.MethodBody(Source(), OnDeathSignature);

            Assert.IsNull(OnDeathWiringFault(body));
        }

        [TestMethod]
        public void OnDeath_WiringCheck_Discriminates_WhenThePredicateIsSubstituted()
        {
            var body = PooledLootSourceText.MethodBody(Source(), OnDeathSignature);

            Assert.IsNotNull(OnDeathWiringFault(body.Replace(Predicate, "IsPooledLootDeath")));
        }
    }
}
