using System.Text.RegularExpressions;

using ACE.Entity.Enum.Properties;
using ACE.Server.MlTreasure;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// Owner decision (Discord "ML Aun Relaria Map loot"): Relaria rewards are credited to the player who
    /// DUG the map, not the top damager. The decision is MlRelariaTrophy.DecideRecipient (pure, tested
    /// directly); the runtime wiring that consumes it (TryDropTrophy, ApplyRewards, Deliver, and the spawn
    /// stamp) needs a live Player and world this harness cannot build, so it is pinned against the source
    /// text, the same way CreatureLootRollTests pins CreateCorpse.
    /// </summary>
    [TestClass]
    public class MlRelariaDiggerCreditTests
    {
        private const uint Digger = 0x50000010;
        private const uint ThirdParty = 0x50000020;

        // ---- the pure decision -----------------------------------------------------------------------

        /// <summary>A third party out-damaged the digger: the digger is still credited, by inventory.</summary>
        [TestMethod]
        public void DecideRecipient_DifferentTopDamager_CreditsDiggerViaInventory()
        {
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaInventory,
                MlRelariaTrophy.DecideRecipient(Digger, ThirdParty));
        }

        [TestMethod]
        public void DecideRecipient_DiggerIsTopDamager_KeepsCorpseDelivery()
        {
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaCorpse,
                MlRelariaTrophy.DecideRecipient(Digger, Digger));
        }

        [TestMethod]
        public void DecideRecipient_NoRecordedDigger_AwardsNothing()
        {
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.None, MlRelariaTrophy.DecideRecipient(null, ThirdParty));
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.None, MlRelariaTrophy.DecideRecipient(0u, ThirdParty));
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.None, MlRelariaTrophy.DecideRecipient(null, null));
        }

        [TestMethod]
        public void DecideRecipient_NoLootKiller_StillCreditsDiggerViaInventory()
        {
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaInventory, MlRelariaTrophy.DecideRecipient(Digger, null));
        }

        [TestMethod]
        public void DiggerGuidProperty_IsAnExistingStandardProperty()
        {
            Assert.AreEqual(PropertyInstanceId.Creator, MlRelariaSpawner.DiggerGuidProperty);
            Assert.IsTrue((ushort)MlRelariaSpawner.DiggerGuidProperty < 8000, "must not be a custom property id");
        }

        // ---- the lifted runtime decisions, exercised through delegates ---------------------------------

        private static string Resolve(uint? digger, uint? lootKiller, out MlRelariaTrophy.RewardRecipient recipient, out string fired)
        {
            string f = "";
            var result = MlRelariaTrophy.ResolveDigger<string>(digger, lootKiller,
                () => { f += "killer;"; return "killerPlayer"; },
                g => { f += "online:" + g + ";"; return g == Digger ? "diggerPlayer" : null; },
                out recipient);
            fired = f;
            return result;
        }

        [TestMethod]
        public void ResolveDigger_ThirdPartyTopDamager_LooksUpTheDiggerOnline_NotTheKiller()
        {
            var r = Resolve(Digger, ThirdParty, out var recipient, out var fired);

            Assert.AreEqual("diggerPlayer", r);
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaInventory, recipient);
            Assert.AreEqual("online:" + Digger + ";", fired);
        }

        [TestMethod]
        public void ResolveDigger_DiggerIsKiller_UsesTheKillerResolverOnly()
        {
            var r = Resolve(Digger, Digger, out var recipient, out var fired);

            Assert.AreEqual("killerPlayer", r);
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaCorpse, recipient);
            Assert.AreEqual("killer;", fired);
        }

        [TestMethod]
        public void ResolveDigger_NoDigger_ResolvesNobodyAndTouchesNothing()
        {
            Assert.IsNull(Resolve(null, ThirdParty, out var r1, out var f1));
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.None, r1);
            Assert.AreEqual("", f1);

            Assert.IsNull(Resolve(0u, ThirdParty, out var r2, out var f2));
            Assert.AreEqual("", f2);
        }

        [TestMethod]
        public void ResolveDigger_OfflineDigger_ResolvesNull_ButRouteIsStillInventory()
        {
            var result = MlRelariaTrophy.ResolveDigger<string>(ThirdParty + 1, ThirdParty, () => "killerPlayer", g => null, out var recipient);

            Assert.IsNull(result, "an offline digger must not fall back to the killer");
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaInventory, recipient);
        }

        [TestMethod]
        public void RouteRewards_CorpseRouteRunsInline_InventoryRouteIsQueuedOnTheDigger()
        {
            var applied = new System.Collections.Generic.List<bool>();
            var queued = new System.Collections.Generic.List<System.Action>();

            MlRelariaTrophy.RouteRewards(MlRelariaTrophy.RewardRecipient.DiggerViaCorpse, applied.Add, queued.Add);
            CollectionAssert.AreEqual(new[] { true }, applied);
            Assert.AreEqual(0, queued.Count);

            applied.Clear();
            MlRelariaTrophy.RouteRewards(MlRelariaTrophy.RewardRecipient.DiggerViaInventory, applied.Add, queued.Add);
            Assert.AreEqual(0, applied.Count, "the inventory route must not apply until the digger's queue runs it");
            Assert.AreEqual(1, queued.Count);
            queued[0]();
            CollectionAssert.AreEqual(new[] { false }, applied);

            applied.Clear();
            queued.Clear();
            MlRelariaTrophy.RouteRewards(MlRelariaTrophy.RewardRecipient.None, applied.Add, queued.Add);
            Assert.AreEqual(0, applied.Count + queued.Count);
        }

        [TestMethod]
        public void DecideRewardPath_FirstKillTrophy_RepeatKillRewards_BankedSpareNothing()
        {
            Assert.AreEqual(MlRelariaTrophy.RewardPath.Trophy, MlRelariaTrophy.DecideRewardPath(1004120, false, false));
            Assert.AreEqual(MlRelariaTrophy.RewardPath.RepeatKill, MlRelariaTrophy.DecideRewardPath(1004120, true, false));
            Assert.AreEqual(MlRelariaTrophy.RewardPath.RepeatKill, MlRelariaTrophy.DecideRewardPath(1004120, true, true));
            Assert.AreEqual(MlRelariaTrophy.RewardPath.None, MlRelariaTrophy.DecideRewardPath(1004120, false, true), "banking a spare is not a repeat kill");
            Assert.AreEqual(MlRelariaTrophy.RewardPath.None, MlRelariaTrophy.DecideRewardPath(0, true, false));
        }

        [TestMethod]
        public void DeliverVia_CorpseRoute_UsesOnlyTheCorpse()
        {
            var fired = "";
            Assert.AreEqual(MlRelariaTrophy.DeliveryResult.Corpse, MlRelariaTrophy.DeliverVia(true,
                () => { fired += "c"; return true; }, () => { fired += "i"; return true; }, () => { fired += "g"; return true; }));
            Assert.AreEqual("c", fired);

            Assert.AreEqual(MlRelariaTrophy.DeliveryResult.Failed, MlRelariaTrophy.DeliverVia(true, () => false, () => true, () => true));
        }

        [TestMethod]
        public void DeliverVia_InventoryRoute_PackThenGroundThenFail()
        {
            var fired = "";
            Assert.AreEqual(MlRelariaTrophy.DeliveryResult.Inventory, MlRelariaTrophy.DeliverVia(false,
                () => { fired += "c"; return true; }, () => { fired += "i"; return true; }, () => { fired += "g"; return true; }));
            Assert.AreEqual("i", fired, "a pack with room must not touch the corpse or the ground");

            fired = "";
            Assert.AreEqual(MlRelariaTrophy.DeliveryResult.Ground, MlRelariaTrophy.DeliverVia(false,
                () => { fired += "c"; return true; }, () => { fired += "i"; return false; }, () => { fired += "g"; return true; }));
            Assert.AreEqual("ig", fired);

            Assert.AreEqual(MlRelariaTrophy.DeliveryResult.Failed, MlRelariaTrophy.DeliverVia(false, () => true, () => false, () => false));
        }

        // ---- owner decision "Pay digger anyway": WHO killed the boss never gates payment -----------------

        private const uint Monster = 0x80000001;
        private const uint OlthoiPlayer = 0x50000030;

        /// <summary>A monster (or an ownerless pet) took the top-damager slot: the digger is still paid, by inventory.</summary>
        [TestMethod]
        public void DecideRecipient_NonPlayerTopDamager_PaysDiggerViaInventory()
        {
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaInventory, MlRelariaTrophy.DecideRecipient(Digger, Monster));
        }

        /// <summary>An Olthoi player is a different identity from the digger: inventory route, never a corpse keyed to them.</summary>
        [TestMethod]
        public void DecideRecipient_OlthoiPlayerTopDamager_PaysDiggerViaInventory()
        {
            Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaInventory, MlRelariaTrophy.DecideRecipient(Digger, OlthoiPlayer));
        }

        [TestMethod]
        public void ResolveDigger_NonPlayerOrOlthoiTopDamager_LooksUpTheDiggerOnline_NeverTouchesTheKillerResolver()
        {
            foreach (var killer in new[] { Monster, OlthoiPlayer })
            {
                var r = Resolve(Digger, killer, out var recipient, out var fired);
                Assert.AreEqual("diggerPlayer", r);
                Assert.AreEqual(MlRelariaTrophy.RewardRecipient.DiggerViaInventory, recipient);
                Assert.AreEqual("online:" + Digger + ";", fired);
            }
        }

        /// <summary>Paying the digger does not depend on the killer: every path decision is a function of the claim state only.</summary>
        [TestMethod]
        public void DecideRewardPath_HasNoKillerInput_FirstKillStillPaysTrophy()
        {
            var m = typeof(MlRelariaTrophy).GetMethod("DecideRewardPath");
            CollectionAssert.AreEqual(new[] { typeof(int), typeof(bool), typeof(bool) }, System.Array.ConvertAll(m.GetParameters(), x => x.ParameterType));
            Assert.AreEqual(MlRelariaTrophy.RewardPath.Trophy, MlRelariaTrophy.DecideRewardPath(1004120, false, false));
        }

        /// <summary>
        /// Structural pin, not a blacklist: a gate on who killed the boss can be spelled many ways
        /// (killer?.IsPlayer != true, killer is null, a TryGetAttacker type test), so instead the body may hold
        /// exactly the three known bails and nothing between the marker bail and the digger read.
        /// </summary>
        [TestMethod]
        public void TryDropTrophy_has_no_killer_identity_gate()
        {
            var code = Code("Source/ACE.Server/MlTreasure/MlRelariaTrophy.cs");
            var body = PooledLootSourceText.MethodBody(code, "public static void TryDropTrophy(");

            Assert.AreEqual(3, Regex.Matches(body, @"\breturn\s*;").Count,
                "exactly three bails: not a dug Relaria (bossMarker), no recorded digger (recipient None), digger offline (null)");
            AssertOnce(body, @"if\s*\(bossMarker\s*<=\s*0\)\s*return;\s*var\s+diggerGuid\s*=", "nothing may sit between the marker bail and the digger read");
            Assert.IsFalse(Regex.IsMatch(Code("Source/ACE.Server/MlTreasure/MlRelariaTrophy.cs"), @"killedByPlayer"), "the killedByPlayer input is gone from the whole file");
        }

        // ---- thin source pins for what cannot be lifted (comments stripped, statements whole) ----------

        private static string Code(string repoRelative)
        {
            var src = PooledLootSourceText.Read(repoRelative);
            src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(src, @"//[^\n]*", "");
        }

        private static void AssertOnce(string code, string pattern, string why)
        {
            Assert.AreEqual(1, Regex.Matches(code, pattern, RegexOptions.Singleline).Count, why);
        }

        [TestMethod]
        public void TryDropTrophy_wiring_is_pinned_as_live_statements()
        {
            var code = Code("Source/ACE.Server/MlTreasure/MlRelariaTrophy.cs");
            var body = PooledLootSourceText.MethodBody(code, "public static void TryDropTrophy(");

            AssertOnce(body, @"var\s+diggerGuid\s*=\s*dying\.GetProperty\(MlRelariaSpawner\.DiggerGuidProperty\);", "digger guid read");
            AssertOnce(body, @"ResolveDigger\(diggerGuid,\s*killer\?\.Guid\.Full,\s*\(\)\s*=>\s*killer\?\.TryGetPetOwnerOrAttacker\(\)\s*as\s+Player,\s*guid\s*=>\s*PlayerManager\.GetOnlinePlayer\(guid\),\s*out\s+var\s+recipient\)", "digger resolution");
            AssertOnce(body, @"if\s*\(recipient\s*==\s*RewardRecipient\.None\)\s*\{\s*log\.Info\(.*?\);\s*return;\s*\}", "no-digger bail");
            AssertOnce(body, @"if\s*\(digger\s*==\s*null\)\s*\{\s*log\.Warn\(.*?\);\s*return;\s*\}", "offline-digger bail");
            AssertOnce(body, @"RouteRewards\(recipient,\s*toCorpse\s*=>\s*ApplyRewards\(dying,\s*corpse,\s*digger,\s*bossMarker,\s*toCorpse\),\s*run\s*=>\s*\{\s*var\s+chain\s*=\s*new\s+ActionChain\(\);\s*chain\.AddAction\(digger,\s*run\);\s*chain\.EnqueueChain\(\);\s*\}\);", "route + enqueue on the digger");
        }

        [TestMethod]
        public void ApplyRewards_and_Deliver_wiring_is_pinned_as_live_statements()
        {
            var code = Code("Source/ACE.Server/MlTreasure/MlRelariaTrophy.cs");
            var apply = PooledLootSourceText.MethodBody(code, "private static void ApplyRewards(");

            AssertOnce(apply, @"var\s+alreadyClaimed\s*=\s*digger\.QuestManager\.HasQuest\(ClaimQuestName\);", "claimed read keyed to digger");
            AssertOnce(apply, @"var\s+killerHoldsTrophy\s*=\s*digger\.GetInventoryItemsOfWCID\(TrophyWcid\)\.Count\s*>\s*0;", "holds read keyed to digger");
            AssertOnce(apply, @"var\s+path\s*=\s*DecideRewardPath\(bossMarker,\s*alreadyClaimed,\s*killerHoldsTrophy\);", "path decision");
            AssertOnce(apply, @"if\s*\(path\s*!=\s*RewardPath\.Trophy\)\s*\{.*?if\s*\(path\s*==\s*RewardPath\.RepeatKill\)\s*TryAwardRepeatKillRewards\(dying,\s*corpse,\s*digger,\s*toCorpse\);\s*return;\s*\}", "repeat-kill branch");
            AssertOnce(apply, @"if\s*\(Deliver\(trophy,\s*corpse,\s*digger,\s*toCorpse,\s*dying\)\)", "trophy delivery");

            var repeat = PooledLootSourceText.MethodBody(code, "private static void TryAwardRepeatKillRewards(");
            AssertOnce(repeat, @"var\s+alreadyHasAura\s*=\s*digger\.QuestManager\.HasQuest\(RepeatAuraQuestName\);", "aura read keyed to digger");
            AssertOnce(repeat, @"digger\.QuestManager\.Stamp\(RepeatAuraQuestName\);", "aura stamp keyed to digger");
            Assert.AreEqual(2, Regex.Matches(repeat, @"if\s*\(doubloonCount\s*>\s*0\)\s*AddDoubloons\(corpse,\s*doubloonCount,\s*digger,\s*dying,\s*toCorpse\);").Count, "both doubloon payouts must go through AddDoubloons with toCorpse");

            var doubloons = PooledLootSourceText.MethodBody(code, "private static void AddDoubloons(");
            AssertOnce(doubloons, @"Deliver\(extra,\s*corpse,\s*digger,\s*toCorpse,\s*dying\)", "extra doubloon delivery");
            Assert.AreEqual(2, Regex.Matches(doubloons, @"Deliver\(first,\s*corpse,\s*digger,\s*toCorpse,\s*dying\);").Count, "stackable and non-stackable branches each deliver first");

            var deliver = PooledLootSourceText.MethodBody(code, "private static bool Deliver(");
            AssertOnce(deliver, @"DeliverVia\(toCorpse,\s*\(\)\s*=>\s*corpse\.TryAddToInventory\(item\),\s*\(\)\s*=>\s*digger\.TryCreateInInventoryWithNetworking\(item,\s*out\s+_,\s*burdenCheck:\s*false\),\s*\(\)\s*=>\s*digger\.Location\s*!=\s*null\s*&&\s*Creature\.DropLootInWorld\(new\[\]\s*\{\s*item\s*\},\s*digger\.Location,\s*digger\.Guid\)\);", "delegates wired to the real calls");
        }

        [TestMethod]
        public void TrySpawn_records_the_digger_on_a_live_line_before_the_boss_enters_the_world()
        {
            var code = Code("Source/ACE.Server/MlTreasure/MlRelariaSpawner.cs");
            var body = PooledLootSourceText.MethodBody(code, "public static bool TrySpawn(");

            var m = Regex.Match(body, @"boss\.SetProperty\(DiggerGuidProperty,\s*player\.Guid\.Full\);");
            Assert.IsTrue(m.Success, "TrySpawn must record the digger guid on the boss on a live line");
            Assert.AreEqual(1, Regex.Matches(body, @"boss\.SetProperty\(DiggerGuidProperty,").Count);

            var enter = body.IndexOf("boss.EnterWorld()", System.StringComparison.Ordinal);
            Assert.IsTrue(enter > m.Index, "the digger must be recorded before EnterWorld");

            var prepare = PooledLootSourceText.MethodBody(code, "public static void PrepareBoss(");
            Assert.IsFalse(prepare.Contains("DiggerGuidProperty"), "PrepareBoss is shared with the admin spawn, which has no digger");
        }
    }
}
