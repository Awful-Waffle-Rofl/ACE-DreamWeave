using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.MlTreasure;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// Tally of the Unburied (MlRelariaChargeTrophy, wcid 1006170) - the SECOND, chargeable Aun Relaria
    /// trophy that USED TO drop alongside the existing one-time CAP relic (MlRelariaTrophy, wcid
    /// 1004121). Owner ruling 2026-09-24 removed the drop entirely; the weenie, the charge side and the
    /// quest names stay so a Tally a player already holds keeps working.
    ///
    /// Covers: the map-charge decision, the wcid/content/quest-name constant pins, the delivery-side
    /// search (FindChargeable) over real in-memory objects - the same GenericObject/Weenie construction
    /// KillFillVesselTests uses, so no live Player or world database is needed - and source-text guards
    /// over the things no pure-function test can reach: the FinishDig consume-then-charge guard in both
    /// branches, that MlRelariaTrophy.TryDropTrophy no longer calls into this class' drop side, and the
    /// mule re-check that must run before the glow is stamped and the trophy consumed.
    ///
    /// WHAT IS NOT COVERED, and cannot be from a unit test (same limits as MlRelariaTrophyTests and
    /// KillFillVesselTests): MlRelariaChargeTrophy.TryAddMapCharge itself needs a live Player
    /// (UpdateProperty enqueues on Session, QuestManager.Stamp touches the quest registry,
    /// ArmRelariaTallyGlowPulseIfEligible queues an ActionChain). It needs the live test queued in
    /// Docs/VERIFY-QUEUE.md.
    /// </summary>
    [TestClass]
    public class MlRelariaChargeTrophyTests
    {
        private static uint nextGuid = 0x7D210000;   // clear of KillFillVesselTests' own static range
        private static uint nextWcid = 993500;

        // ---- the map-charge decision -------------------------------------------------------------------

        /// <summary>
        /// THE FIX: a player who already completed (holds GlowQuestName) but still holds a Tally - reached
        /// a completed character only by a route ShouldDropTrophy's killer-only gate does not cover
        /// (looting a Relaria corpse after HalfLife, or fellowship loot sharing - Corpse.cs) - must
        /// destroy every stray Tally rather than charge one. DestroyStray wins even though a chargeable
        /// Tally genuinely exists, which is the whole point of evaluating "completed" first and
        /// unconditionally.
        /// </summary>
        [TestMethod]
        public void DecideMapChargeAction_AlreadyCompletedWithATrophy_DestroysStray()
        {
            Assert.AreEqual(MlRelariaChargeTrophy.MapChargeAction.DestroyStray,
                MlRelariaChargeTrophy.DecideMapChargeAction(alreadyCompleted: true, hasChargeableTrophy: true));
        }

        /// <summary>Same outcome even with no Tally in hand - "completed" is checked before, and
        /// independently of, whether a chargeable Tally exists at all.</summary>
        [TestMethod]
        public void DecideMapChargeAction_AlreadyCompletedWithNoTrophy_StillDestroysStray()
        {
            Assert.AreEqual(MlRelariaChargeTrophy.MapChargeAction.DestroyStray,
                MlRelariaChargeTrophy.DecideMapChargeAction(alreadyCompleted: true, hasChargeableTrophy: false));
        }

        /// <summary>The ordinary case this feature exists for: not yet completed, carrying a chargeable
        /// Tally - charge it.</summary>
        [TestMethod]
        public void DecideMapChargeAction_NotCompletedWithATrophy_Charges()
        {
            Assert.AreEqual(MlRelariaChargeTrophy.MapChargeAction.Charge,
                MlRelariaChargeTrophy.DecideMapChargeAction(alreadyCompleted: false, hasChargeableTrophy: true));
        }

        /// <summary>Not completed, no Tally in hand at all - nothing to do.</summary>
        [TestMethod]
        public void DecideMapChargeAction_NotCompletedWithNoTrophy_DoesNothing()
        {
            Assert.AreEqual(MlRelariaChargeTrophy.MapChargeAction.None,
                MlRelariaChargeTrophy.DecideMapChargeAction(alreadyCompleted: false, hasChargeableTrophy: false));
        }

        // ---- constant pins ----------------------------------------------------------------------------

        [TestMethod]
        public void TrophyWcid_IsTheAuthoredWeenie()
        {
            Assert.AreEqual(1006170u, MlRelariaChargeTrophy.TrophyWcid,
                "TrophyWcid must name Content/sql/weenies/1006170 Tally of the Unburied.sql");
        }

        [TestMethod]
        public void QuestNames_DifferFromTheRelicsOwnQuestNames()
        {
            Assert.AreNotEqual(MlRelariaTrophy.ClaimQuestName, MlRelariaChargeTrophy.CompleteQuestName);
            Assert.AreNotEqual(MlRelariaTrophy.ClaimQuestName, MlRelariaChargeTrophy.GlowQuestName);
            Assert.AreNotEqual(MlRelariaTrophy.RepeatAuraQuestName, MlRelariaChargeTrophy.GlowQuestName);
            Assert.AreNotEqual(MlRelariaChargeTrophy.CompleteQuestName, MlRelariaChargeTrophy.GlowQuestName);
        }

        [TestMethod]
        public void CompleteQuestName_IsPinned()
        {
            Assert.AreEqual("MlRelariaTallyComplete", MlRelariaChargeTrophy.CompleteQuestName);
        }

        [TestMethod]
        public void GlowQuestName_IsPinned()
        {
            Assert.AreEqual("MlRelariaTallyGlow", MlRelariaChargeTrophy.GlowQuestName);
        }

        // ---- the weenie SQL, pinned against the constants above ---------------------------------------

        private static string SqlPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies", "1006170 Tally of the Unburied.sql")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/sql/weenies/1006170 Tally of the Unburied.sql by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Content", "sql", "weenies", "1006170 Tally of the Unburied.sql");
        }

        [TestMethod]
        public void Sql_ClassIdMatchesTheWcidConstant()
        {
            var sql = File.ReadAllText(SqlPath());

            StringAssert.Contains(sql, $"VALUES ({MlRelariaChargeTrophy.TrophyWcid}, 'ace1006170-tallyoftheunburied'",
                "the weenie row's class_Id must be the same id MlRelariaChargeTrophy.TrophyWcid names");
        }

        [TestMethod]
        public void Sql_CarriesTheHundredChargeCapacity()
        {
            var sql = File.ReadAllText(SqlPath());

            StringAssert.Contains(sql, "(1006170,   91,        100)",
                "PropertyInt 91 MaxStructure must be 100 - the 100-map-completion capacity the spec fixes");
        }

        /// <summary>The Tally is recognized by its fixed wcid (MlRelariaChargeTrophy.FindChargeable), NOT
        /// by KillFillVessel's marker property - so it must never carry PropertyInt 9038
        /// KillFillCreatureType, which would make it look like an (unwanted) kill-fill vessel too.</summary>
        [TestMethod]
        public void Sql_CarriesNoKillFillMarkerProperty()
        {
            var sql = File.ReadAllText(SqlPath());

            Assert.IsFalse(sql.Contains(", 9038,"), "the Tally must not carry PropertyInt 9038 KillFillCreatureType");
            Assert.IsFalse(sql.Contains(", 9039,"), "the Tally must not carry PropertyInt 9039 KillFillLandblock");
            Assert.IsFalse(sql.Contains(", 9040,"), "the Tally must not carry PropertyInt 9040 KillFillRealm");
        }

        /// <summary>Unlike the relic (1004121), the Tally never grants a class ability point and must
        /// carry neither the CAP funnel property nor the relic's own one-time claim quest name.</summary>
        [TestMethod]
        public void Sql_CarriesNoClassAbilityPointGrant()
        {
            var sql = File.ReadAllText(SqlPath());

            Assert.IsFalse(sql.Contains(", 9020,"), "the Tally must not carry PropertyInt 9020 ClassAbilityPointValue");
            Assert.IsFalse(sql.Contains(", 9017,"), "the Tally must not carry PropertyString 9017 ClassAbilityGrantQuest");
        }

        // ---- FindChargeable, over real in-memory objects (KillFillVesselTests' construction pattern) --

        /// <summary>A vessel built from an in-memory weenie. WeenieType.Generic's SetEphemeralValues
        /// touches neither the database nor the dat files, matching KillFillVesselTests.MakeVessel.</summary>
        private static WorldObject MakeTrophy(int structure, int capacity = 100)
        {
            var weenie = new Weenie
            {
                WeenieClassId = MlRelariaChargeTrophy.TrophyWcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.Structure, structure },
                    { PropertyInt.MaxStructure, capacity },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Tally of the Unburied" } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject MakeOtherItem(string name = "Bread")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Misc } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static Container MakeContainer(string name = "Pack")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Container },
                    { PropertyInt.ItemsCapacity, 24 },
                    { PropertyInt.ContainersCapacity, 7 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        private static void Put(Container container, WorldObject item) => container.Inventory[item.Guid] = item;

        [TestMethod]
        public void FindChargeable_ReturnsNullForANullContainer()
        {
            Assert.IsNull(MlRelariaChargeTrophy.FindChargeable(null));
        }

        [TestMethod]
        public void FindChargeable_ReturnsNullWhenNoneQualify()
        {
            var pack = MakeContainer();
            Put(pack, MakeOtherItem());

            Assert.IsNull(MlRelariaChargeTrophy.FindChargeable(pack));
        }

        [TestMethod]
        public void FindChargeable_FindsATrophyInTheMainPackAndIgnoresOtherItems()
        {
            var pack = MakeContainer();
            Put(pack, MakeOtherItem());
            var trophy = MakeTrophy(structure: 3);
            Put(pack, trophy);
            Put(pack, MakeOtherItem("Cheese"));

            Assert.AreSame(trophy, MlRelariaChargeTrophy.FindChargeable(pack));
        }

        /// <summary>Side packs are searched too, for the identical reason KillFillVessel.FindFillable
        /// does: Container.Inventory holds only what is directly in that container, so a trophy stowed in
        /// a side pouch must not be invisible.</summary>
        [TestMethod]
        public void FindChargeable_SearchesSidePacks()
        {
            var pack = MakeContainer();
            var side = MakeContainer("Side Pouch");
            var trophy = MakeTrophy(structure: 0);

            Put(side, trophy);
            Put(pack, MakeOtherItem());
            Put(pack, side);

            Assert.AreSame(trophy, MlRelariaChargeTrophy.FindChargeable(pack));
        }

        /// <summary>A Tally already at capacity (Structure == MaxStructure) is skipped - it is not
        /// "chargeable" any more, it is awaiting the charge-side completion stamp instead.</summary>
        [TestMethod]
        public void FindChargeable_SkipsAFullTrophy()
        {
            var pack = MakeContainer();
            var full = MakeTrophy(structure: 100, capacity: 100);

            Put(pack, full);

            Assert.IsNull(MlRelariaChargeTrophy.FindChargeable(pack));
        }

        [TestMethod]
        public void FindChargeable_SkipsAFullOneAndFindsAnAvailableOne()
        {
            var pack = MakeContainer();
            var full = MakeTrophy(structure: 100, capacity: 100);
            var available = MakeTrophy(structure: 40);

            Put(pack, full);
            Put(pack, available);

            Assert.AreSame(available, MlRelariaChargeTrophy.FindChargeable(pack));
        }

        // ---- source-text guards ------------------------------------------------------------------------

        private static string SourcePath(params string[] relativeSegments)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            var target = Path.Combine(relativeSegments);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, target)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {target} by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, target);
        }

        private static string StripComments(string source)
        {
            var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(noBlockComments, @"//[^\n]*", "");
        }

        /// <summary>
        /// BOTH branches of TreasureMapHandler.FinishDig must consume the map and add a charge ONLY when
        /// the consume actually succeeded, wrapped so a charge-side failure never costs the player their
        /// already-consumed map. Matched with a whitespace-tolerant regex (\s* rather than a fixed
        /// indentation/newline literal) so it does not break on a harmless reformat, but still pins the
        /// "only on success" shape - an unconditional pair of calls would fail this, but pass a test that
        /// only checked both calls exist somewhere in the file.
        /// </summary>
        [TestMethod]
        public void FinishDig_BothBranches_ChargeOnlyWhenConsumeSucceeds()
        {
            var src = StripComments(File.ReadAllText(SourcePath("Source", "ACE.Server", "MlTreasure", "TreasureMapHandler.cs")));

            var pattern = new Regex(@"if\s*\(player\.TryConsumeFromInventoryWithNetworking\(wo,\s*1\)\)\s*MlRelariaChargeTrophy\.TryAddMapCharge\(player\);");

            Assert.AreEqual(2, pattern.Matches(src).Count,
                "expected the guarded 'consume then charge' shape exactly twice - once in the boss-variant branch, once in the ordinary branch");
        }

        /// <summary>Both branches wrap their consume-and-charge pair in a try/catch that LOGS rather than
        /// lets an exception propagate - a charge-side bug must never turn into a lost map or a broken
        /// dig. Counts rather than searches for one literal span, since the two branches are two separate
        /// try blocks.</summary>
        [TestMethod]
        public void FinishDig_ConsumeAndChargeCallsAreEachInsideATryCatchThatLogs()
        {
            var src = StripComments(File.ReadAllText(SourcePath("Source", "ACE.Server", "MlTreasure", "TreasureMapHandler.cs")));

            Assert.AreEqual(2, Regex.Matches(src, Regex.Escape("MlRelariaChargeTrophy.TryAddMapCharge(player);")).Count,
                "expected exactly two call sites - the boss-variant branch and the ordinary branch");

            // Every TryAddMapCharge call site sits between a "try" and the log.Error catch that follows it
            // in that same branch; searching for the pattern in each of the two branch windows below
            // pins this without depending on whitespace/indentation exactly.
            var firstCharge = src.IndexOf("MlRelariaChargeTrophy.TryAddMapCharge(player);", StringComparison.Ordinal);
            var secondCharge = src.IndexOf("MlRelariaChargeTrophy.TryAddMapCharge(player);", firstCharge + 1, StringComparison.Ordinal);

            var precedingTry1 = src.LastIndexOf("try", firstCharge, StringComparison.Ordinal);
            var followingCatch1 = src.IndexOf("catch (Exception ex)", firstCharge, StringComparison.Ordinal);
            Assert.IsTrue(precedingTry1 >= 0 && precedingTry1 < firstCharge, "the first TryAddMapCharge call must be preceded by a try block");
            Assert.IsTrue(followingCatch1 >= 0 && followingCatch1 > firstCharge, "the first TryAddMapCharge call must be followed by a catch block");

            var precedingTry2 = src.LastIndexOf("try", secondCharge, StringComparison.Ordinal);
            var followingCatch2 = src.IndexOf("catch (Exception ex)", secondCharge, StringComparison.Ordinal);
            Assert.IsTrue(precedingTry2 >= 0 && precedingTry2 < secondCharge, "the second TryAddMapCharge call must be preceded by a try block");
            Assert.IsTrue(followingCatch2 >= 0 && followingCatch2 > secondCharge, "the second TryAddMapCharge call must be followed by a catch block");
        }

        /// <summary>
        /// OWNER RULING 2026-09-24: MlRelariaTrophy.TryDropTrophy must no longer call
        /// MlRelariaChargeTrophy.TryDropOnCorpse (or ShouldDropTrophy) at all - the Tally no longer drops
        /// from a Relaria kill. The relic's own alreadyClaimed branch must still exist, unmodified, and
        /// neither MlRelariaChargeTrophy.TryDropOnCorpse nor MlRelariaChargeTrophy.ShouldDropTrophy exist
        /// anywhere in the codebase any more - both were dead code once their only caller was removed.
        /// </summary>
        [TestMethod]
        public void MlRelariaTrophy_NoLongerCallsTheChargeTrophyDrop()
        {
            var src = StripComments(File.ReadAllText(SourcePath("Source", "ACE.Server", "MlTreasure", "MlRelariaTrophy.cs")));

            var killerResolved = src.IndexOf("killer.TryGetPetOwnerOrAttacker() is Player killerPlayer", StringComparison.Ordinal);
            var relicBranch = src.IndexOf("var alreadyClaimed = killerPlayer.QuestManager.HasQuest(ClaimQuestName);", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, killerResolved, "the killer Player must be resolved via TryGetPetOwnerOrAttacker");
            Assert.AreNotEqual(-1, relicBranch, "the relic's own alreadyClaimed read must still exist, unmodified");
            Assert.IsFalse(src.Contains("MlRelariaChargeTrophy.TryDropOnCorpse("),
                "MlRelariaTrophy.TryDropTrophy must no longer call MlRelariaChargeTrophy.TryDropOnCorpse");

            var chargeTrophySrc = File.ReadAllText(SourcePath("Source", "ACE.Server", "MlTreasure", "MlRelariaChargeTrophy.cs"));
            Assert.IsFalse(chargeTrophySrc.Contains("public static void TryDropOnCorpse("),
                "MlRelariaChargeTrophy.TryDropOnCorpse must be removed as dead code now its only caller is gone");
            Assert.IsFalse(chargeTrophySrc.Contains("public static bool ShouldDropTrophy("),
                "MlRelariaChargeTrophy.ShouldDropTrophy must be removed as dead code now its only caller is gone");
        }

        /// <summary>
        /// TryAddMapCharge must re-read the completion quest stamp AFTER stamping it and BEFORE arming the
        /// glow or consuming the trophy - the mule guard on QuestManager.Update is a SILENT no-op, so
        /// without this re-check a mule's Tally would be consumed (and the player told the glow is theirs)
        /// for a completion that was never actually recorded.
        /// </summary>
        [TestMethod]
        public void TryAddMapCharge_ReChecksTheStampBeforeArmingTheGlowOrConsuming()
        {
            var src = StripComments(File.ReadAllText(SourcePath("Source", "ACE.Server", "MlTreasure", "MlRelariaChargeTrophy.cs")));

            var stampComplete = src.IndexOf("player.QuestManager.Stamp(CompleteQuestName);", StringComparison.Ordinal);
            var recheck = src.IndexOf("!player.QuestManager.HasQuest(CompleteQuestName)", StringComparison.Ordinal);
            var stampGlow = src.IndexOf("player.QuestManager.Stamp(GlowQuestName);", StringComparison.Ordinal);
            var consume = src.IndexOf("player.TryConsumeFromInventoryWithNetworking(trophy, 1);", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, stampComplete, "TryAddMapCharge must stamp CompleteQuestName on reaching capacity");
            Assert.AreNotEqual(-1, recheck, "TryAddMapCharge must re-read HasQuest(CompleteQuestName) after stamping it");
            Assert.AreNotEqual(-1, stampGlow, "TryAddMapCharge must stamp GlowQuestName");
            Assert.AreNotEqual(-1, consume, "TryAddMapCharge must consume the trophy");

            Assert.IsTrue(stampComplete < recheck, "the re-check must happen AFTER the stamp it is verifying");
            Assert.IsTrue(recheck < stampGlow, "the glow must be stamped only AFTER the re-check confirms the completion stamp took");
            Assert.IsTrue(recheck < consume, "the trophy must be consumed only AFTER the re-check confirms the completion stamp took");
        }

        /// <summary>
        /// THE FIX'S OWN SOURCE-ORDER GUARD: TryAddMapCharge must read GlowQuestName - the "already
        /// completed" check - BEFORE it ever writes a charge (player.UpdateProperty(...Structure...)),
        /// and the DestroyStray branch it feeds must return before that write is reached. A version that
        /// checked completion only after charging (or only for the FindChargeable result rather than
        /// unconditionally) would still pass every DecideMapChargeAction unit test above, since those
        /// test the pure function directly - this is what pins that TryAddMapCharge actually calls it
        /// this way, and returns out of the DestroyStray branch rather than falling through.
        /// </summary>
        [TestMethod]
        public void TryAddMapCharge_ChecksCompletionBeforeWritingAnyCharge()
        {
            var src = StripComments(File.ReadAllText(SourcePath("Source", "ACE.Server", "MlTreasure", "MlRelariaChargeTrophy.cs")));

            var methodStart = src.IndexOf("public static void TryAddMapCharge(Player player)", StringComparison.Ordinal);
            Assert.AreNotEqual(-1, methodStart, "TryAddMapCharge must exist");

            var completedRead = src.IndexOf("player.QuestManager.HasQuest(GlowQuestName)", methodStart, StringComparison.Ordinal);
            var destroyStrayCall = src.IndexOf("DestroyStrayTrophies(player);", methodStart, StringComparison.Ordinal);
            var chargeWrite = src.IndexOf("player.UpdateProperty(trophy, PropertyInt.Structure, charges);", methodStart, StringComparison.Ordinal);

            Assert.AreNotEqual(-1, completedRead, "TryAddMapCharge must read GlowQuestName to decide the completed case");
            Assert.AreNotEqual(-1, destroyStrayCall, "TryAddMapCharge must call DestroyStrayTrophies for the completed case");
            Assert.AreNotEqual(-1, chargeWrite, "TryAddMapCharge must still write the Structure charge for the ordinary case");

            Assert.IsTrue(completedRead < destroyStrayCall, "the completed read must precede the call it gates");
            Assert.IsTrue(destroyStrayCall < chargeWrite, "DestroyStrayTrophies must be called (and returned from) BEFORE the charge write - a completed player must never reach it");

            // The DestroyStray branch must actually return rather than fall through into the charge path -
            // pinned by requiring a "return;" between the DestroyStrayTrophies call and the charge write,
            // immediately after it (the same brace-scoped shape UseGem's own order tests assume, per
            // OneTimeCapGrantOrderTests).
            var returnAfterDestroy = src.IndexOf("return;", destroyStrayCall, StringComparison.Ordinal);
            Assert.AreNotEqual(-1, returnAfterDestroy, "the DestroyStrayTrophies call must be followed by a return");
            Assert.IsTrue(returnAfterDestroy < chargeWrite, "the return after DestroyStrayTrophies must precede the charge write");
        }
    }
}
