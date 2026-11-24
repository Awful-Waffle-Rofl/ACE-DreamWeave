using System;
using System.Text.RegularExpressions;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Source pins for the creature half of the Gauntlet death wiring in Creature_Death.cs, which needs a live world to run:
    /// (1) OnDeath's kill-progress gate also excludes a Gauntlet creature, (2) Die's one corpse local ORs the Gauntlet
    /// predicate, and (3) Die reports the death through the exact if-head after the Balance Trials hook. Each pin is a checker
    /// returning the first fault, run once over the real source (null expected) and again over mutated copies of it (each
    /// must fault), so every pin is mutation-proved in the test itself. The player half (the death hold latch) is Task 9's.
    /// </summary>
    [TestClass]
    public class GauntletDeathHookSiteTests
    {
        private const string DeathPath = "Source/ACE.Server/WorldObjects/Creature_Death.cs";
        private const string OnDeathSig = "public virtual DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)";
        private const string DieSig = "protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)";

        internal const string FeedsGate = "var feedsProgress = FeedsKillProgressHooks(P_SpeedCourseCreature || P_TrialRep != null);";
        internal const string FeedsExclusion = "if (P_Gauntlet != null)\n                    feedsProgress = false;";
        internal const string CorpseTerm = "|| IsGauntletCorpselessDeath(P_Gauntlet != null) ||";
        internal const string TrialHook = "if (P_TrialRep != null)\n                ACE.Server.BalanceTrials.TrialManager.OnTrialCreatureDied(this);";
        internal const string GauntletHook = "if (P_Gauntlet != null)\n                ACE.Server.Avatars.Gauntlet.GauntletSpawner.OnCreatureDied(this);";
        private const string CorpseStep = "if (!suppressCorpse)\n                    CreateCorpse(topDamager);";

        private static string Source() => PooledLootSourceText.Read(DeathPath);

        private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

        /// <summary>
        /// (1) The one FeedsKillProgressHooks read, then the exact Gauntlet if-head forcing it false, then both progress hooks
        /// behind it: the exclusion sits between the gate and the first hook that reads it.
        /// </summary>
        internal static string FeedsFault(string source)
        {
            var onDeath = PooledLootSourceText.MethodBody(source, OnDeathSig);

            if (Count(onDeath, FeedsGate) != 1)
                return "the kill-progress gate is not read exactly once";

            if (Regex.Matches(source, @"FeedsKillProgressHooks\(").Count != 1)
                return "more than one FeedsKillProgressHooks read";

            if (Count(onDeath, FeedsExclusion) != 1)
                return "the Gauntlet exclusion is not in OnDeath exactly once";

            var gate = onDeath.IndexOf(FeedsGate, StringComparison.Ordinal);
            var exclusion = onDeath.IndexOf(FeedsExclusion, StringComparison.Ordinal);
            var firstUse = onDeath.IndexOf("if (feedsProgress)", StringComparison.Ordinal);

            if (exclusion < gate || firstUse < exclusion)
                return "the Gauntlet exclusion is not between the gate and the first hook that reads it";

            if (!Regex.IsMatch(onDeath.Substring(gate), @"^[\s\S]*?if \(feedsProgress\)\s*\n\s*killingPlayer\.ApplyKillFillVesselCreatureDeath\(this\);"))
                return "the vessel hook is not behind the gate";

            if (!Regex.IsMatch(onDeath.Substring(gate), @"^[\s\S]*?if \(feedsProgress\)\s*\n\s*killingPlayer\.ApplyMuleFormTokenCreatureDeath\(this\);"))
                return "the mule-token hook is not behind the gate";

            return null;
        }

        /// <summary>(2) The Gauntlet predicate is OR-ed into the one corpse local, which the single corpse step reads after.</summary>
        internal static string CorpseFault(string source)
        {
            var die = PooledLootSourceText.MethodBody(source, DieSig);
            var local = die.IndexOf("var suppressCorpse = ", StringComparison.Ordinal);

            if (local < 0)
                return "no corpse local";

            var end = die.IndexOf(';', local);
            var line = die.Substring(local, end - local + 1);

            if (Count(line, CorpseTerm) != 1)
                return "the Gauntlet predicate is not OR-ed into suppressCorpse";

            if (Count(die, "var suppressCorpse = ") != 1)
                return "more than one corpse local";

            var step = die.IndexOf(CorpseStep, StringComparison.Ordinal);

            if (step < end || Count(die, CorpseStep) != 1)
                return "the corpse step is not the one if (!suppressCorpse) after the local";

            return null;
        }

        /// <summary>(3) The exact if-head, once, in Die (never OnDeath), after the Trials hook.</summary>
        internal static string HookFault(string source)
        {
            var onDeath = PooledLootSourceText.MethodBody(source, OnDeathSig);
            var die = PooledLootSourceText.MethodBody(source, DieSig);

            if (Count(die, GauntletHook) != 1)
                return "the Gauntlet death hook is not in Die exactly once";

            if (Count(source, "GauntletSpawner.OnCreatureDied(") != 1)
                return "the Gauntlet death report is called from more than one place";

            if (onDeath.Contains("GauntletSpawner.OnCreatureDied(", StringComparison.Ordinal))
                return "the death report is in OnDeath";

            var trial = die.IndexOf(TrialHook, StringComparison.Ordinal);
            var hook = die.IndexOf(GauntletHook, StringComparison.Ordinal);

            if (trial < 0 || hook < trial)
                return "the Gauntlet hook does not follow the Trials hook";

            return null;
        }

        [TestMethod]
        public void OnDeath_KillProgressGate_ExcludesGauntletCreatures()
        {
            var src = Source();
            Assert.IsNull(FeedsFault(src));

            Assert.IsNotNull(FeedsFault(src.Replace(FeedsExclusion, "")), "mutation: the exclusion dropped");
            Assert.IsNotNull(FeedsFault(src.Replace(FeedsExclusion, "if (P_Gauntlet == null)\n                    feedsProgress = false;")), "mutation: the exclusion inverted");
            Assert.IsNotNull(FeedsFault(src.Replace(FeedsExclusion, "if (P_Gauntlet != null)\n                    feedsProgress = true;")), "mutation: the exclusion forces true");

            // Moved after the vessel hook.
            var vessel = "if (feedsProgress)\n                    killingPlayer.ApplyKillFillVesselCreatureDeath(this);";
            var moved = src.Replace(FeedsExclusion, "").Replace(vessel, vessel + "\n\n                " + FeedsExclusion);
            Assert.AreNotEqual(src, moved, "guard: the move mutation applied");
            Assert.IsNotNull(FeedsFault(moved), "mutation: the exclusion after the first hook");
            Assert.IsNotNull(FeedsFault(src.Replace("if (feedsProgress)\n                    killingPlayer.ApplyKillFillVesselCreatureDeath(this);", "killingPlayer.ApplyKillFillVesselCreatureDeath(this);")),
                "mutation: the vessel hook ungated");
        }

        [TestMethod]
        public void Die_CorpseLocal_OrsTheGauntletPredicate()
        {
            var src = Source();
            Assert.IsNull(CorpseFault(src));

            Assert.IsNotNull(CorpseFault(src.Replace(" || IsGauntletCorpselessDeath(P_Gauntlet != null)", "")), "mutation: the term dropped");
            Assert.IsNotNull(CorpseFault(src.Replace("IsGauntletCorpselessDeath(P_Gauntlet != null)", "IsGauntletCorpselessDeath(P_Gauntlet == null)")), "mutation: the term inverted");
            Assert.IsNotNull(CorpseFault(src.Replace(CorpseStep, "CreateCorpse(topDamager);")), "mutation: the corpse step ungated");
        }

        [TestMethod]
        public void Die_ReportsGauntletDeaths_AfterTheTrialsHook()
        {
            var src = Source();
            Assert.IsNull(HookFault(src));

            Assert.IsNotNull(HookFault(src.Replace(GauntletHook, "")), "mutation: the hook removed");
            Assert.IsNotNull(HookFault(src.Replace(GauntletHook, "if (P_Gauntlet == null)\n                ACE.Server.Avatars.Gauntlet.GauntletSpawner.OnCreatureDied(this);")), "mutation: the if-head inverted");

            // Moved before the Trials hook.
            var moved = src.Replace(GauntletHook + "\n", "").Replace(TrialHook, GauntletHook + "\n\n            " + TrialHook);
            Assert.AreNotEqual(src, moved, "guard: the move mutation applied");
            Assert.IsNotNull(HookFault(moved), "mutation: the hook before the Trials hook");
        }
    }
}
