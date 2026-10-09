using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the pure half of the NPC turn-in reward pre-flight
    /// (Source/ACE.Server/Entity/EmoteGivePreflight.cs). The gate itself lives on Player
    /// (Player_Inventory.PreflightEmoteGive) and needs a live Player plus a world database, so it is
    /// verified in-game rather than here; everything that can stand alone was factored into this class.
    /// </summary>
    [TestClass]
    public class EmoteGivePreflightTests
    {
        private static PreflightPathCost Cost(int inventorySlots, int containerSlots, int burden, bool outOfInventory = false, bool outOfContainer = false, bool overBurden = false)
        {
            return new PreflightPathCost(inventorySlots, containerSlots, burden, outOfInventory, outOfContainer, overBurden);
        }

        private static PropertiesEmote Set(EmoteCategory category, string quest, params PropertiesEmoteAction[] actions)
        {
            var set = new PropertiesEmote { Category = category, Quest = quest, Probability = 1 };

            foreach (var action in actions)
                set.PropertiesEmoteAction.Add(action);

            return set;
        }

        private static PropertiesEmoteAction GiveRow(uint wcid, int stackSize = 1) =>
            new PropertiesEmoteAction { Type = (uint)EmoteType.Give, WeenieClassId = wcid, StackSize = stackSize };

        private static PropertiesEmoteAction BranchRow(EmoteType type, string quest) =>
            new PropertiesEmoteAction { Type = (uint)type, Message = quest };

        // ---------------- flag seam ----------------

        /// <summary>
        /// The production flag read must degrade to OFF rather than throw out of a give. See the control
        /// run below for what the underlying PropertyManager read actually does under the test host.
        /// </summary>
        [TestMethod]
        public void ReadEnabledFromProperties_WithNoShardConfig_ReadsAsOff()
        {
            Assert.IsFalse(EmoteGivePreflight.ReadEnabledFromProperties());
        }

        /// <summary>
        /// Control run for the test above, in the only form that is order-independent: the guarded read
        /// returns the same OFF answer whichever way the underlying PropertyManager read goes.
        ///
        /// The unguarded read is NOT asserted here, deliberately. Run alone, PropertyManager.GetBool throws
        /// NullReferenceException - its cache misses and it falls through to DatabaseManager.ShardConfig,
        /// which is null under the test host (Source/ACE.Server/Managers/PropertyManager.cs:104), which is
        /// what the try/catch in ReadEnabledFromProperties exists for. But another test in a full run can
        /// warm that cache first, and the same call then returns a cached default instead of throwing, so
        /// asserting the throw makes this file order-dependent (measured: it passes under
        /// --filter EmoteGivePreflightTests and fails in a full ACE.Server.Tests run).
        /// </summary>
        [TestMethod]
        public void ReadEnabledFromProperties_IsOffWhicheverWayThePropertyReadGoes()
        {
            var first = EmoteGivePreflight.ReadEnabledFromProperties();
            var second = EmoteGivePreflight.ReadEnabledFromProperties();

            Assert.IsFalse(first);
            Assert.IsFalse(second);
        }

        [TestMethod]
        public void Enabled_DefaultsToTheProductionRead_AndIsOffInTests()
        {
            Assert.IsFalse(EmoteGivePreflight.Enabled);
        }

        [TestMethod]
        public void Enabled_HonorsTheSeam()
        {
            var restore = EmoteGivePreflight.EnabledSource;

            try
            {
                EmoteGivePreflight.EnabledSource = () => true;
                Assert.IsTrue(EmoteGivePreflight.Enabled);

                EmoteGivePreflight.EnabledSource = () => false;
                Assert.IsFalse(EmoteGivePreflight.Enabled);
            }
            finally
            {
                EmoteGivePreflight.EnabledSource = restore;
            }
        }

        [TestMethod]
        public void Enabled_WithANullSeam_FallsBackToTheProductionRead()
        {
            var restore = EmoteGivePreflight.EnabledSource;

            try
            {
                EmoteGivePreflight.EnabledSource = null;
                Assert.IsFalse(EmoteGivePreflight.Enabled);
            }
            finally
            {
                EmoteGivePreflight.EnabledSource = restore;
            }
        }

        // ---------------- reward counting (the "nothing to gate on" escape) ----------------

        [TestMethod]
        public void TotalRewardCount_OfANullForecast_IsZero()
        {
            Assert.AreEqual(0, EmoteGivePreflight.TotalRewardCount(null));
        }

        [TestMethod]
        public void TotalRewardCount_OfASetWithNoRewards_IsZero()
        {
            var root = new PropertiesEmote { Category = EmoteCategory.Give, Probability = 1 };
            root.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = (uint)EmoteType.Tell, Message = "thanks" });

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(0, EmoteGivePreflight.TotalRewardCount(forecast));
        }

        [TestMethod]
        public void TotalRewardCount_SumsEveryRewardOnEveryPath()
        {
            var success = new PropertiesEmote { Category = EmoteCategory.QuestSuccess, Quest = "testquest", Probability = 1 };
            success.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = (uint)EmoteType.Give, WeenieClassId = 1000001, StackSize = 1 });

            var failure = new PropertiesEmote { Category = EmoteCategory.QuestFailure, Quest = "testquest", Probability = 1 };
            failure.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = (uint)EmoteType.CreateTreasure });

            var root = new PropertiesEmote { Category = EmoteCategory.Give, Probability = 1 };
            root.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = (uint)EmoteType.InqQuest, Message = "testquest" });

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root, success, failure });

            // two paths, one reward each
            Assert.AreEqual(2, forecast.Paths.Count);
            Assert.AreEqual(2, EmoteGivePreflight.TotalRewardCount(forecast));
        }

        // ---------------- worst-path selection ----------------

        [TestMethod]
        public void SelectWorst_OfNothing_IsNull()
        {
            Assert.IsNull(EmoteGivePreflight.SelectWorst(null));
            Assert.IsNull(EmoteGivePreflight.SelectWorst(new List<PreflightPathCost>()));
        }

        [TestMethod]
        public void SelectWorst_PrefersAFailingPathOverALargerPassingOne()
        {
            var passingButLarge = Cost(9, 0, 5000);
            var failingButSmall = Cost(1, 0, 10, outOfInventory: true);

            var worst = EmoteGivePreflight.SelectWorst(new List<PreflightPathCost> { passingButLarge, failingButSmall });

            Assert.AreSame(failingButSmall, worst);
            Assert.IsTrue(worst.ExceedsLimits);
        }

        [TestMethod]
        public void SelectWorst_AmongFailingPaths_TakesTheOneDemandingTheMostSlots()
        {
            var oneSlot = Cost(1, 0, 900, outOfInventory: true);
            var threeSlots = Cost(2, 1, 10, outOfInventory: true);

            var worst = EmoteGivePreflight.SelectWorst(new List<PreflightPathCost> { oneSlot, threeSlots });

            Assert.AreSame(threeSlots, worst);
            Assert.AreEqual(3, worst.RequiredSlots);
        }

        [TestMethod]
        public void SelectWorst_BreaksASlotTieOnBurden()
        {
            var lightweight = Cost(2, 0, 50, overBurden: true);
            var heavy = Cost(2, 0, 5000, overBurden: true);

            var worst = EmoteGivePreflight.SelectWorst(new List<PreflightPathCost> { lightweight, heavy });

            Assert.AreSame(heavy, worst);
        }

        [TestMethod]
        public void SelectWorst_WhenEveryPathFits_StillReturnsOneThatDoesNotExceedLimits()
        {
            var worst = EmoteGivePreflight.SelectWorst(new List<PreflightPathCost> { Cost(1, 0, 100), Cost(2, 0, 200) });

            Assert.IsNotNull(worst);
            Assert.IsFalse(worst.ExceedsLimits);
        }

        [TestMethod]
        public void SelectWorst_SkipsNullEntries()
        {
            var real = Cost(1, 0, 10, outOfInventory: true);

            var worst = EmoteGivePreflight.SelectWorst(new List<PreflightPathCost> { null, real, null });

            Assert.AreSame(real, worst);
        }

        // ---------------- cost shape ----------------

        [TestMethod]
        public void PathCost_RequiredSlots_CanGoNegativeWhenTheTurnInFreesMoreThanTheRewardNeeds()
        {
            // one reward slot needed, the consumed turn-in freed a container slot and its burden
            var cost = Cost(1, -1, -350);

            Assert.AreEqual(0, cost.RequiredSlots);
            Assert.IsFalse(cost.ExceedsLimits);
        }

        [TestMethod]
        public void PathCost_ExceedsLimits_IsTrueForAnyOneLimit()
        {
            Assert.IsTrue(Cost(1, 0, 0, outOfInventory: true).ExceedsLimits);
            Assert.IsTrue(Cost(0, 1, 0, outOfContainer: true).ExceedsLimits);
            Assert.IsTrue(Cost(0, 0, 1, overBurden: true).ExceedsLimits);
            Assert.IsFalse(Cost(1, 1, 1).ExceedsLimits);
        }

        [TestMethod]
        public void PathCost_From_ANullTally_IsNull()
        {
            Assert.IsNull(PreflightPathCost.From(null));
        }

        // ---------------- refusal wording ----------------

        [TestMethod]
        public void RefusalMessage_ForAPathThatFits_IsNull()
        {
            Assert.IsNull(EmoteGivePreflight.RefusalMessage(null, "Nuhmudira"));
            Assert.IsNull(EmoteGivePreflight.RefusalMessage(Cost(1, 0, 100), "Nuhmudira"));
        }

        [TestMethod]
        public void RefusalMessage_PicksBurdenFirst()
        {
            var message = EmoteGivePreflight.RefusalMessage(Cost(1, 1, 9000, outOfInventory: true, outOfContainer: true, overBurden: true), "Nuhmudira");

            Assert.AreEqual("You are too encumbered to accept what Nuhmudira would give you in return!", message);
        }

        [TestMethod]
        public void RefusalMessage_ThenPackSpace()
        {
            var message = EmoteGivePreflight.RefusalMessage(Cost(1, 1, 10, outOfInventory: true, outOfContainer: true), "Nuhmudira");

            Assert.AreEqual("You do not have enough pack space to accept what Nuhmudira would give you in return!", message);
        }

        [TestMethod]
        public void RefusalMessage_ThenContainerSlots()
        {
            var message = EmoteGivePreflight.RefusalMessage(Cost(0, 1, 10, outOfContainer: true), "Nuhmudira");

            Assert.AreEqual("You do not have enough container slots to accept what Nuhmudira would give you in return!", message);
        }

        // ---------------- Decide: the gate decision and its exception boundary ----------------

        /// <summary>
        /// The gate runs inside a CreateMoveToChain callback with no catch anywhere above it, and the path
        /// costing it drives touches live inventory and the world database. A throw there must not escape:
        /// it fails open, so a broken pre-flight degrades to the pre-gate behaviour rather than aborting
        /// every turn-in on the server.
        /// </summary>
        [TestMethod]
        public void Decide_WhenPathCostingThrows_FailsOpenAndReportsNoWorstPath()
        {
            var root = Set(EmoteCategory.Give, null, GiveRow(1000001));

            var allowed = EmoteGivePreflight.Decide(
                root,
                new List<PropertiesEmote> { root },
                path => throw new InvalidOperationException("costing blew up"),
                "unit test",
                out var worst);

            Assert.IsTrue(allowed);
            Assert.IsNull(worst);
        }

        /// <summary>
        /// The costing delegate above is thrown from deliberately rather than the forecast build, and the
        /// assertion is discriminating in the same way: the delegate returns a cost that WOULD refuse, so a
        /// missing catch cannot be mistaken for a pass. Both callees sit inside the one try block, so one
        /// throwing input proves the boundary; driving a throw out of EmoteRewardForecast.Build instead
        /// would couple this test to that class's null handling, which is not this gate's contract.
        /// </summary>
        [TestMethod]
        public void Decide_WhenPathCostingThrows_DoesNotRefuseEvenThoughTheCostWouldHave()
        {
            var root = Set(EmoteCategory.Give, null, GiveRow(1000001));

            var calls = 0;

            var allowed = EmoteGivePreflight.Decide(
                root,
                new List<PropertiesEmote> { root },
                path =>
                {
                    calls++;
                    throw new InvalidOperationException("costing blew up after being reached");
                },
                "unit test",
                out var worst);

            Assert.AreEqual(1, calls, "the costing delegate must actually have been reached");
            Assert.IsTrue(allowed);
            Assert.IsNull(worst);
        }

        [TestMethod]
        public void Decide_WithANullEmoteSetOrNullCosting_Allows()
        {
            var root = Set(EmoteCategory.Give, null, GiveRow(1000001));

            Assert.IsTrue(EmoteGivePreflight.Decide(null, new List<PropertiesEmote>(), path => Cost(1, 0, 0, outOfInventory: true), "unit test", out var worstA));
            Assert.IsNull(worstA);

            Assert.IsTrue(EmoteGivePreflight.Decide(root, new List<PropertiesEmote> { root }, null, "unit test", out var worstB));
            Assert.IsNull(worstB);
        }

        [TestMethod]
        public void Decide_WhenEveryPathFits_Allows()
        {
            var root = Set(EmoteCategory.Give, null, GiveRow(1000001));

            var allowed = EmoteGivePreflight.Decide(
                root,
                new List<PropertiesEmote> { root },
                path => Cost(1, 0, 250),
                "unit test",
                out var worst);

            Assert.IsTrue(allowed);
            Assert.IsNull(worst);
        }

        [TestMethod]
        public void Decide_WhenAPathExceedsLimits_RefusesAndHandsBackThatPath()
        {
            var root = Set(EmoteCategory.Give, null, GiveRow(1000001));

            var allowed = EmoteGivePreflight.Decide(
                root,
                new List<PropertiesEmote> { root },
                path => Cost(3, 0, 9000, overBurden: true),
                "unit test",
                out var worst);

            Assert.IsFalse(allowed);
            Assert.IsNotNull(worst);
            Assert.IsTrue(worst.ExceedsLimits);
            Assert.AreEqual(9000, worst.RequiredBurden);
        }

        /// <summary>
        /// One failing path out of several is enough to refuse - the player might take any of them.
        /// </summary>
        [TestMethod]
        public void Decide_RefusesWhenOnlyOneBranchOfSeveralWouldNotFit()
        {
            var success = Set(EmoteCategory.QuestSuccess, "testquest", GiveRow(1000001));
            var failure = Set(EmoteCategory.QuestFailure, "testquest", GiveRow(1000002));
            var root = Set(EmoteCategory.Give, null, BranchRow(EmoteType.InqQuest, "testquest"));

            var priced = 0;

            var allowed = EmoteGivePreflight.Decide(
                root,
                new List<PropertiesEmote> { root, success, failure },
                path =>
                {
                    priced++;
                    return priced == 2 ? Cost(1, 0, 10, outOfInventory: true) : Cost(1, 0, 10);
                },
                "unit test",
                out var worst);

            Assert.AreEqual(2, priced);
            Assert.IsFalse(allowed);
            Assert.IsNotNull(worst);
        }

        [TestMethod]
        public void Decide_WithNoRewardsToForecast_NeverPricesAPath()
        {
            var root = Set(EmoteCategory.Give, null, new PropertiesEmoteAction { Type = (uint)EmoteType.Tell, Message = "thanks" });

            var priced = 0;

            var allowed = EmoteGivePreflight.Decide(
                root,
                new List<PropertiesEmote> { root },
                path => { priced++; return Cost(1, 0, 0, outOfInventory: true); },
                "unit test",
                out var worst);

            Assert.IsTrue(allowed);
            Assert.IsNull(worst);
            Assert.AreEqual(0, priced);
        }

        /// <summary>
        /// A truncated walk may hide worse paths than it reported, so the gate must not price or refuse on
        /// it. The control below is the same shape with the cycle removed: it is not truncated and IS priced,
        /// which is what makes the assertion above about truncation specifically rather than about the set
        /// simply producing no rewards.
        /// </summary>
        [TestMethod]
        public void Decide_WithATruncatedForecast_NeverPricesAPath()
        {
            // a QuestSuccess set that branches back into itself: the second visit truncates the walk
            var cyclic = Set(EmoteCategory.QuestSuccess, "loop", BranchRow(EmoteType.InqQuest, "loop"));
            var empty = Set(EmoteCategory.QuestFailure, "loop");
            var root = Set(EmoteCategory.Give, null, GiveRow(1000001), BranchRow(EmoteType.InqQuest, "loop"));

            var sets = new List<PropertiesEmote> { root, cyclic, empty };

            var forecast = EmoteRewardForecast.Build(root, sets);

            Assert.IsTrue(forecast.Truncated, "expected the self-referential branch to truncate the walk");
            Assert.IsTrue(EmoteGivePreflight.TotalRewardCount(forecast) > 0, "expected rewards present, so truncation is the only reason to allow");

            var priced = 0;

            var allowed = EmoteGivePreflight.Decide(
                root,
                sets,
                path => { priced++; return Cost(1, 0, 0, outOfInventory: true); },
                "unit test",
                out var worst);

            Assert.IsTrue(allowed);
            Assert.IsNull(worst);
            Assert.AreEqual(0, priced);
        }

        [TestMethod]
        public void Decide_ControlForTruncation_TheSameShapeWithoutTheCycleIsPricedAndRefused()
        {
            var acyclic = Set(EmoteCategory.QuestSuccess, "loop");
            var empty = Set(EmoteCategory.QuestFailure, "loop");
            var root = Set(EmoteCategory.Give, null, GiveRow(1000001), BranchRow(EmoteType.InqQuest, "loop"));

            var sets = new List<PropertiesEmote> { root, acyclic, empty };

            var forecast = EmoteRewardForecast.Build(root, sets);

            Assert.IsFalse(forecast.Truncated);
            Assert.IsTrue(EmoteGivePreflight.TotalRewardCount(forecast) > 0);

            var priced = 0;

            var allowed = EmoteGivePreflight.Decide(
                root,
                sets,
                path => { priced++; return Cost(1, 0, 0, outOfInventory: true); },
                "unit test",
                out var worst);

            Assert.IsTrue(priced > 0, "the control must actually reach the costing delegate");
            Assert.IsFalse(allowed);
            Assert.IsNotNull(worst);
        }

        // ---------------- treasure accounting ----------------

        /// <summary>
        /// A CreateTreasure row carries no wcid, so the gate must reserve a slot for it without any world
        /// database lookup. This is the accounting the gate performs per path, minus the live player.
        /// </summary>
        [TestMethod]
        public void TreasureRows_AreCountedAsOneSlotEachAndNoBurden()
        {
            var root = new PropertiesEmote { Category = EmoteCategory.Give, Probability = 1 };
            root.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = (uint)EmoteType.CreateTreasure });
            root.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = (uint)EmoteType.CreateTreasure });

            var forecast = EmoteRewardForecast.Build(root, new List<PropertiesEmote> { root });

            Assert.AreEqual(1, forecast.Paths.Count);

            var treasureRows = 0;
            var wcidRewards = 0;

            foreach (var reward in forecast.Paths[0])
            {
                if (reward.IsRandomTreasure)
                    treasureRows++;
                else
                    wcidRewards++;
            }

            Assert.AreEqual(2, treasureRows);
            Assert.AreEqual(0, wcidRewards);

            var cost = Cost(treasureRows * EmoteGivePreflight.TreasureInventorySlots, 0, 0);

            Assert.AreEqual(2, cost.RequiredInventorySlots);
            Assert.AreEqual(0, cost.RequiredBurden);
        }
    }
}
