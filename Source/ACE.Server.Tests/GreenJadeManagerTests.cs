using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Green Jade (Task D): the material test, the target refusals the Salvage Roller reuses, the pure swap, the price-first
    /// pay-and-swap with a stand-in for SalvageTool.TryConsume (which needs a live Player and cannot run here), the exact
    /// texts, the SalvageForge row, and source pins on the recipe intercept. PropertyManager keys are seeded per test and
    /// restored in TestCleanup.
    /// </summary>
    [TestClass]
    public class GreenJadeManagerTests
    {
        private static uint nextGuid = 0x7ED20000;
        private static uint nextWcid = 1008300;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedTunables()
        {
            JewelryAspectTestKit.Seed();
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            JewelryAspectTestKit.Restore();
        }

        private static WorldObject Salvage(string name, MaterialType material, ItemType type = ItemType.TinkeringMaterial, int? structure = null, int? maxStructure = null, int? charges = null)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)type },
                { PropertyInt.MaterialType, (int)material },
            };

            if (structure.HasValue) ints[PropertyInt.Structure] = structure.Value;
            if (maxStructure.HasValue) ints[PropertyInt.MaxStructure] = maxStructure.Value;
            if (charges.HasValue) ints[PropertyInt.SalvageToolCharges] = charges.Value;

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = ints,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject FullBag() => Salvage("Green Jade Salvage", MaterialType.GreenJade, structure: 100, maxStructure: 100);

        private static WorldObject Hammer(int remaining) => Salvage("Green Jade Hammer", MaterialType.GreenJade, structure: remaining, maxStructure: 10, charges: 10);

        private static WorldObject Piece(int attack, int defense, int utility) => JewelryAspectApplicatorTests.Jewelry("Test Ring", EquipMask.FingerWearLeft | EquipMask.FingerWearRight, attack, defense, utility);

        private static Func<int, int> Script(params int[] draws)
        {
            var i = 0;
            return n => draws[i++ % draws.Length];
        }

        // ---------------- material test ----------------

        [TestMethod]
        public void IsGreenJadeMaterial_NeedsTheTinkeringMaterialTypeAndTheMaterial()
        {
            Assert.IsTrue(GreenJadeManager.IsGreenJadeMaterial(FullBag()));
            Assert.IsTrue(GreenJadeManager.IsGreenJadeMaterial(Hammer(10)));

            Assert.IsFalse(GreenJadeManager.IsGreenJadeMaterial(Salvage("Serpentine", MaterialType.Serpentine)), "another material");
            Assert.IsFalse(GreenJadeManager.IsGreenJadeMaterial(Salvage("Raw Green Jade", MaterialType.GreenJade, type: ItemType.Gem)), "a raw gem carries the material but is not salvage");
            Assert.IsFalse(GreenJadeManager.IsGreenJadeMaterial(null));
        }

        // ---------------- target refusals (shared with the Salvage Roller) ----------------

        [TestMethod]
        public void TargetStateRefusal_AspectedPiece_Passes()
        {
            Assert.IsNull(GreenJadeManager.TargetStateRefusal(Piece(3, 3, 4)));
        }

        [TestMethod]
        public void TargetStateRefusal_UnaspectedPiece_IsRefusedWithTheExactLine()
        {
            Assert.AreEqual("The Test Ring has no Aspect yet. Use a Jewelers Aspect Gem on it first.", GreenJadeManager.TargetStateRefusal(Piece(0, 0, 0)));
        }

        [TestMethod]
        public void TargetStateRefusal_NonAspectablePieces_AreRefusedBeforeTheAspectCheck()
        {
            const string line = "Aspects can only be given to rings, bracelets and necklaces.";

            foreach (var (name, locations) in new[] { ("Trinket", EquipMask.TrinketOne), ("Crown", EquipMask.HeadWear), ("Cloak", EquipMask.Cloak), ("Sigil", EquipMask.SigilOne) })
            {
                // even a trinket that somehow carries ten aspects is refused as the wrong kind of item
                Assert.AreEqual(line, GreenJadeManager.TargetStateRefusal(JewelryAspectApplicatorTests.Jewelry(name, locations, 5, 3, 2)), name);
                Assert.AreEqual(line, GreenJadeManager.TargetStateRefusal(JewelryAspectApplicatorTests.Jewelry(name, locations)), name);
            }
        }

        [TestMethod]
        public void TargetInventoryRefusal_WornPieceIsRefused_PackedPiecePasses()
        {
            var player = JewelryAspectTestKit.BarePlayer(out var equipped);
            var ring = Piece(3, 3, 4);
            var seam = GreenJadeManager.InMyInventory;

            try
            {
                GreenJadeManager.InMyInventory = (p, t) => true;

                Assert.IsNull(GreenJadeManager.TargetInventoryRefusal(player, ring), "control: in the pack and not worn");

                ring.CurrentWieldedLocation = EquipMask.FingerWearRight;
                JewelryAspectTestKit.Wear(equipped, ring);

                Assert.AreEqual("Take the Test Ring off first.", GreenJadeManager.TargetInventoryRefusal(player, ring));
                Assert.IsFalse(GreenJadeManager.VerifyTarget(player, ring, out var message));
                Assert.AreEqual("Take the Test Ring off first.", message);
            }
            finally
            {
                GreenJadeManager.InMyInventory = seam;
            }
        }

        [TestMethod]
        public void VerifyTarget_ReportsInventoryBeforeState()
        {
            var player = JewelryAspectTestKit.BarePlayer(out _);
            var seam = GreenJadeManager.InMyInventory;

            try
            {
                GreenJadeManager.InMyInventory = (p, t) => false;

                // out of the pack AND unaspected: the inventory line wins, as the bag path orders it
                Assert.IsFalse(GreenJadeManager.VerifyTarget(player, Piece(0, 0, 0), out var message));
                Assert.AreEqual("You must remove the Test Ring and place it in your inventory first.", message);

                GreenJadeManager.InMyInventory = (p, t) => true;

                Assert.IsFalse(GreenJadeManager.VerifyTarget(player, Piece(0, 0, 0), out message));
                Assert.AreEqual("The Test Ring has no Aspect yet. Use a Jewelers Aspect Gem on it first.", message);

                Assert.IsTrue(GreenJadeManager.VerifyTarget(player, Piece(3, 3, 4), out message));
                Assert.IsNull(message);
            }
            finally
            {
                GreenJadeManager.InMyInventory = seam;
            }
        }

        // ---------------- fuel ----------------

        [TestMethod]
        public void FuelRefusal_FullBagOrHammerWithACharge_Passes()
        {
            Assert.IsNull(GreenJadeManager.FuelRefusal(FullBag()));
            Assert.IsNull(GreenJadeManager.FuelRefusal(Hammer(10)));
            Assert.IsNull(GreenJadeManager.FuelRefusal(Hammer(7)), "7 of 10 charges still pays: a Hammer counts whole applications");
            Assert.IsNull(GreenJadeManager.FuelRefusal(Hammer(1)));
        }

        [TestMethod]
        public void FuelRefusal_PartialBagAndEmptyHammer_AreRefusedWithTheManagersWords()
        {
            var partial = Salvage("Green Jade Salvage", MaterialType.GreenJade, structure: 40, maxStructure: 100);
            Assert.AreEqual("The Green Jade Salvage is not full. A complete unit of salvage is required.", GreenJadeManager.FuelRefusal(partial));

            Assert.AreEqual("The Green Jade Hammer has no uses remaining.", GreenJadeManager.FuelRefusal(Hammer(0)));
        }

        // ---------------- the pure swap ----------------

        [TestMethod]
        public void ApplyStep_Swap_IsDeterministicUnderAScriptedDraw()
        {
            // counts (3,3,4); draw 0 picks slot 0 = Attack; second draw 1 picks the second of {Defense, Utility} = Utility
            var piece = Piece(3, 3, 4);

            var counts = GreenJadeManager.ApplyStep(piece, Script(0, 1), out var added, out var from, out var to);
            Assert.IsFalse(added, "at ten aspects the step is a swap");

            Assert.AreEqual(AspectType.Attack, from);
            Assert.AreEqual(AspectType.Utility, to);
            Assert.AreEqual(new AspectCounts(2, 3, 5).Attack, counts.Attack);
            Assert.AreEqual(3, counts.Defense);
            Assert.AreEqual(5, counts.Utility);

            // and the piece itself was rewritten
            Assert.AreEqual(2, piece.GetProperty(PropertyInt.JewelryAspectAttack));
            Assert.AreEqual(3, piece.GetProperty(PropertyInt.JewelryAspectDefense));
            Assert.AreEqual(5, piece.GetProperty(PropertyInt.JewelryAspectUtility));
        }

        [TestMethod]
        public void ApplyStep_Swap_ChangesExactlyOneSlot_AndKeepsTheSumAtTen()
        {
            var random = new Random(20261109);
            var piece = Piece(3, 3, 4);

            for (var i = 0; i < 2000; i++)
            {
                var before = JewelryAspectMath.ReadCounts(piece);

                var after = GreenJadeManager.ApplyStep(piece, n => random.Next(n), out var added, out var from, out var to);
                Assert.IsFalse(added);

                Assert.AreEqual(10, after.Total, "sum stays 10");
                Assert.AreNotEqual(from, to, "never swaps a type for itself");
                Assert.AreEqual(before[from] - 1, after[from]);
                Assert.AreEqual(before[to] + 1, after[to]);

                var changed = Enum.GetValues(typeof(AspectType)).Cast<AspectType>().Count(t => before[t] != after[t]);
                Assert.AreEqual(2, changed, "exactly one slot moved: two counts differ, by one each");

                Assert.IsTrue(JewelryAspectMath.IsAspected(piece), "the piece stays aspected");
            }
        }

        [TestMethod]
        public void ApplyStep_Swap_NeverTouchesTheTinkerCounterOrLog()
        {
            var piece = Piece(3, 3, 4);

            GreenJadeManager.ApplyStep(piece, new Random(7).Next, out _, out _, out _);

            Assert.IsNull(piece.GetProperty(PropertyInt.NumTimesTinkered));
            Assert.IsNull(piece.GetProperty(PropertyString.TinkerLog));
        }

        // ---------------- price first, then mutate ----------------

        [TestMethod]
        public void PayAndStep_Hammer_ConsumesTheChargeNotTheHammer_AndSwapsAfterThePrice()
        {
            var hammer = Hammer(10);
            var piece = Piece(3, 3, 4);
            WorldObject consumed = null;
            var calls = 0;
            AspectCounts seenAtPrice = default;

            // the stand-in for SalvageTool.TryConsume: a Hammer with charges to spare stays and reports charges left
            var result = GreenJadeManager.PayAndStep(hammer, piece, Script(0, 1), source =>
            {
                calls++;
                consumed = source;
                seenAtPrice = JewelryAspectMath.ReadCounts(piece);
                return SalvageTool.GetRemaining(source) - 1;
            });

            Assert.IsNotNull(result);
            Assert.AreEqual(1, calls, "the price is taken exactly once");
            Assert.AreSame(hammer, consumed, "the Hammer is what pays");
            Assert.AreEqual(9, result.Remaining);
            Assert.IsTrue(result.WasSalvageTool);
            Assert.AreEqual("Green Jade Hammer", result.SourceName);
            Assert.AreEqual(10, hammer.Structure ?? 0, "the stand-in left the Hammer's own Structure alone: only SalvageTool.TryConsume decrements it");
            Assert.AreEqual(new AspectCounts(3, 3, 4).Attack, seenAtPrice.Attack, "the piece was still untouched when the price was taken");
            Assert.AreEqual(3, seenAtPrice.Defense);
            Assert.AreEqual(4, seenAtPrice.Utility);
            Assert.AreEqual(AspectType.Attack, result.From);
            Assert.AreEqual(AspectType.Utility, result.To);
            Assert.AreEqual(2, piece.GetProperty(PropertyInt.JewelryAspectAttack));
        }

        [TestMethod]
        public void PayAndStep_Bag_IsConsumedWhole_AndCarriesNoChargeMessage()
        {
            var bag = FullBag();
            var piece = Piece(3, 3, 4);

            var result = GreenJadeManager.PayAndStep(bag, piece, Script(0, 0), source => 0);

            Assert.IsNotNull(result);
            Assert.IsFalse(result.WasSalvageTool);
            Assert.AreEqual(0, result.Remaining);
            Assert.IsNull(SalvageTool.GetChargeMessage(result.SourceName, result.WasSalvageTool, result.Remaining), "a bag says nothing about charges");
            Assert.AreEqual(10, JewelryAspectMath.ReadCounts(piece).Total);
        }

        [TestMethod]
        public void PayAndStep_LastHammerCharge_ReportsTheToolCrumbling()
        {
            var result = GreenJadeManager.PayAndStep(Hammer(1), Piece(3, 3, 4), Script(0, 0), source => 0);

            Assert.AreEqual("The Green Jade Hammer crumbles to dust.", SalvageTool.GetChargeMessage(result.SourceName, result.WasSalvageTool, result.Remaining));
        }

        [TestMethod]
        public void PayAndStep_FailedPrice_LeavesThePieceUntouched()
        {
            var piece = Piece(3, 3, 4);

            var result = GreenJadeManager.PayAndStep(FullBag(), piece, Script(0, 1), source => null);

            Assert.IsNull(result);
            Assert.AreEqual(3, piece.GetProperty(PropertyInt.JewelryAspectAttack));
            Assert.AreEqual(3, piece.GetProperty(PropertyInt.JewelryAspectDefense));
            Assert.AreEqual(4, piece.GetProperty(PropertyInt.JewelryAspectUtility));
        }

        [TestMethod]
        public void PayAndStep_AlwaysSucceeds_NoFailureBranchOtherThanThePrice()
        {
            var random = new Random(99);
            var piece = Piece(10, 0, 0);

            for (var i = 0; i < 500; i++)
            {
                var result = GreenJadeManager.PayAndStep(Hammer(10), piece, n => random.Next(n), source => 9);
                Assert.IsNotNull(result, "a swap never fails once the price is paid");
                Assert.AreEqual(10, JewelryAspectMath.ReadCounts(piece).Total);
            }
        }

        // ---------------- exact texts ----------------

        [TestMethod]
        public void Text_SuccessAndDisabled_AreByteForByte()
        {
            Assert.AreEqual(
                "The Green Jade shifts an aspect on the Test Ring: Attack becomes Defense. Now Attack 2, Defense 4, Utility 4.",
                GreenJadeManager.GetSuccessText("Test Ring", false, AspectType.Attack, AspectType.Defense, new AspectCounts(2, 4, 4)));

            Assert.AreEqual(
                "The Green Jade adds an aspect to the Test Ring: Utility. Now Attack 1, Defense 0, Utility 2.",
                GreenJadeManager.GetSuccessText("Test Ring", true, AspectType.Utility, AspectType.Utility, new AspectCounts(1, 0, 2)));

            Assert.AreEqual("Jewelry Aspects are disabled right now.", GreenJadeManager.RefusalDisabled);
            Assert.AreEqual("Aspects can only be given to rings, bracelets and necklaces.", GreenJadeManager.RefusalNotJewelry);
        }

        // ---------------- the add phase ----------------

        [TestMethod]
        public void ApplyStep_BelowTen_Adds_AndDoesNotSwap()
        {
            var piece = Piece(0, 1, 0);

            var counts = GreenJadeManager.ApplyStep(piece, Script(2), out var added, out var from, out var to);

            Assert.IsTrue(added);
            Assert.AreEqual(AspectType.Utility, to);
            Assert.AreEqual(AspectType.Utility, from, "an add reports the type added on both sides");
            Assert.AreEqual(0, counts.Attack);
            Assert.AreEqual(1, counts.Defense, "the existing aspect is untouched");
            Assert.AreEqual(1, counts.Utility);
            Assert.AreEqual(1, piece.GetProperty(PropertyInt.JewelryAspectUtility));
        }

        [TestMethod]
        public void ApplyStep_FromOneToTen_NineAdds_NeverASwap_ThenEveryStepSwaps()
        {
            var random = new Random(31);
            var piece = Piece(0, 1, 0);

            for (var step = 2; step <= 10; step++)
            {
                var before = JewelryAspectMath.ReadCounts(piece);
                var after = GreenJadeManager.ApplyStep(piece, n => random.Next(n), out var added, out var from, out var to);

                Assert.IsTrue(added, $"step to {step} is an add");
                Assert.AreEqual(step, after.Total);
                Assert.AreEqual(before[to] + 1, after[to]);
            }

            Assert.AreEqual(10, JewelryAspectMath.ReadCounts(piece).Total);

            for (var i = 0; i < 300; i++)
            {
                var before = JewelryAspectMath.ReadCounts(piece);
                var after = GreenJadeManager.ApplyStep(piece, n => random.Next(n), out var added, out var from, out var to);

                Assert.IsFalse(added, "at ten it swaps");
                Assert.AreEqual(10, after.Total, "a swap keeps the total at ten");
                Assert.AreNotEqual(from, to);
                Assert.AreEqual(before[from] - 1, after[from]);
                Assert.AreEqual(before[to] + 1, after[to]);
            }
        }

        [TestMethod]
        public void PayAndStep_BelowTen_ReportsAnAdd_AndPaysTheSameWay()
        {
            var piece = Piece(1, 0, 0);

            var result = GreenJadeManager.PayAndStep(Hammer(10), piece, Script(1), source => 9);

            Assert.IsTrue(result.Added);
            Assert.AreEqual(AspectType.Defense, result.To);
            Assert.AreEqual(2, result.Counts.Total);
            Assert.AreEqual(9, result.Remaining);
        }

        [TestMethod]
        public void TargetStateRefusal_PartiallyFilledPiece_Passes()
        {
            Assert.IsNull(GreenJadeManager.TargetStateRefusal(Piece(0, 1, 0)), "one aspect is aspected");
            Assert.IsNull(GreenJadeManager.TargetStateRefusal(Piece(2, 3, 4)), "nine aspects is aspected");
        }

        [TestMethod]
        public void Wiring_UseRequirements_RefuseTradedItems_AndPostClapChecksTheMuleGate()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/Entity/GreenJadeManager.cs");
            var use = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(text, "public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target)"));
            var verify = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(text, "public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)"));

            var clap = use.IndexOf("SendMotionAsCommands", StringComparison.Ordinal);
            var mule = use.IndexOf("if (player.MuleBlocked(MuleAction.Craft)) { player.SendUseDoneEvent(); return; }", clap, StringComparison.Ordinal);
            var reverify = use.IndexOf("VerifyUseRequirements(player, source, target);", clap, StringComparison.Ordinal);

            Assert.IsTrue(clap > 0 && mule > clap && reverify > mule, "the post-clap action checks the mule gate first, then re-verifies");
            Assert.AreEqual(1, JewelryAspectTestKit.Count(use, "player.MuleBlocked(MuleAction.Craft)"));

            StringAssert.Contains(verify, "if (InTradeWindow(player, source, target)) return WeenieError.TradeItemBeingTraded;");

            var trade = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(text, "internal static bool InTradeWindow(Player player, WorldObject source, WorldObject target)"));
            StringAssert.Contains(trade, "player.IsTrading");
            StringAssert.Contains(trade, "source.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow)");
            StringAssert.Contains(trade, "target.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow)");
        }

        // ---------------- trade window ----------------

        // IsTrading has a private setter, and FieldInfo.SetValue on a Player runs its static constructor (which needs a database in
        // this harness and poisons the process); an UnsafeAccessor reaches the auto-property's backing field without that.
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<IsTrading>k__BackingField")]
        private static extern ref bool IsTradingField(Player player);

        private static Player TradingPlayer(bool trading, params WorldObject[] inWindow)
        {
            var player = JewelryAspectTestKit.BarePlayer(out _);

            IsTradingField(player) = trading;
            player.ItemsInTradeWindow = new HashSet<ObjectGuid>(inWindow.Select(w => w.Guid));

            return player;
        }

        private static WorldObject BagHolding(WorldObject inner)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Container,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Pouch" } },
            };

            var bag = ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            Assert.IsInstanceOfType<Container>(bag, "harness: a Container weenie builds a Container");

            ((Container)bag).Inventory[inner.Guid] = inner;

            return bag;
        }

        [TestMethod]
        public void InTradeWindow_TheTargetInTheWindow_IsTrue()
        {
            var bag = FullBag();
            var piece = Piece(3, 3, 4);

            Assert.IsTrue(GreenJadeManager.InTradeWindow(TradingPlayer(true, piece), bag, piece));
        }

        [TestMethod]
        public void InTradeWindow_TheSourceInTheWindow_IsTrue()
        {
            var bag = FullBag();
            var piece = Piece(3, 3, 4);

            Assert.IsTrue(GreenJadeManager.InTradeWindow(TradingPlayer(true, bag), bag, piece));
        }

        [TestMethod]
        public void InTradeWindow_AContainerHoldingATradedItem_IsTrue()
        {
            var inner = Piece(3, 3, 4);
            var pouch = BagHolding(inner);

            // the traded thing is inside the pouch used as the source, or inside a pouch used as the target
            Assert.IsTrue(GreenJadeManager.InTradeWindow(TradingPlayer(true, inner), pouch, Piece(3, 3, 4)), "container as the source");
            Assert.IsTrue(GreenJadeManager.InTradeWindow(TradingPlayer(true, inner), FullBag(), pouch), "container as the target");
        }

        [TestMethod]
        public void InTradeWindow_NotTrading_IsFalseEvenWithTheGuidPresent()
        {
            var bag = FullBag();
            var piece = Piece(3, 3, 4);

            Assert.IsFalse(GreenJadeManager.InTradeWindow(TradingPlayer(false, piece, bag), bag, piece));
        }

        [TestMethod]
        public void InTradeWindow_TradingButNeitherItemInTheWindow_IsFalse()
        {
            var other = Piece(1, 1, 1);

            Assert.IsFalse(GreenJadeManager.InTradeWindow(TradingPlayer(true, other), FullBag(), Piece(3, 3, 4)));
        }

        // ---------------- SalvageForge ----------------
        [TestMethod]
        public void SalvageForge_MaterialTable_CarriesTheGreenJadeRow()
        {
            Assert.IsTrue(SalvageForge.MaterialTable.TryGetValue(MaterialType.GreenJade, out var info), "Green Jade is a forge material");
            Assert.AreEqual(Skill.MagicItemTinkering, info.Skill);
            Assert.AreEqual(1008001u, (uint)info.HammerWcid);
            Assert.AreEqual("Green Jade", info.DisplayName);
            Assert.AreEqual(6, SalvageForge.MaterialTable.Count);
        }

        [TestMethod]
        public void SalvageForge_RefusalTexts_NameGreenJade()
        {
            const string list = "tiger eye, obsidian, tourmaline, amethyst, serpentine or green jade salvage.";

            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/Entity/SalvageForge.cs"), "must be used on a full bag of " + list);
            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/RefireStations/SalvageForgeStation.cs"), "only takes a full bag of " + list);
        }

        // ---------------- wiring pins (source text) ----------------

        [TestMethod]
        public void Wiring_RecipeIntercept_SitsAfterSerpentine_AndBeforeTheCookbook()
        {
            var recipe = PooledLootSourceText.Read("Source/ACE.Server/Managers/RecipeManager.cs");
            var body = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(recipe, "public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)"));

            var mule = body.IndexOf("player.MuleBlocked(MuleAction.Craft)", StringComparison.Ordinal);
            var tinkerLock = body.IndexOf("TinkerLock.IsRefused(source, target)", StringComparison.Ordinal);
            var serpentine = body.IndexOf("SpellRerollManager.IsRerollMaterial(source)", StringComparison.Ordinal);
            var jade = body.IndexOf("if (GreenJadeManager.IsGreenJadeMaterial(source))", StringComparison.Ordinal);
            var cookbook = body.IndexOf("var recipe = GetRecipe(player, source, target);", StringComparison.Ordinal);

            Assert.IsTrue(mule >= 0 && tinkerLock > mule && serpentine > tinkerLock && jade > serpentine && cookbook > jade, "mule gate, TinkerLock, Serpentine, Green Jade, then the cookbook");
            StringAssert.Contains(body, "GreenJadeManager.UseObjectOnTarget(player, source, target);");

            // the intercept does not read the switch: the manager's own check sends the disabled text
            var interceptEnd = body.IndexOf("GreenJadeManager.UseObjectOnTarget(player, source, target);", StringComparison.Ordinal);
            Assert.IsFalse(body.Substring(jade, interceptEnd - jade).Contains("jewelry_aspects_enabled"), "the Green Jade intercept must not gate on the switch");
            StringAssert.Contains(JewelryAspectTestKit.Flat(PooledLootSourceText.Read("Source/ACE.Server/Entity/GreenJadeManager.cs")), "SendCraftMessage(player, RefusalDisabled);");
        }

        [TestMethod]
        public void Wiring_Manager_RevalidatesAfterTheClap_NeverBuildsAnUpdateMessage_AndNeverTouchesTheTinkerLog()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/Entity/GreenJadeManager.cs");
            var flat = JewelryAspectTestKit.Flat(text);

            var use = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(text, "public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target)"));
            Assert.IsTrue(use.IndexOf("VerifyUseRequirements(player, source, target);", use.IndexOf("SendMotionAsCommands", StringComparison.Ordinal), StringComparison.Ordinal) > 0, "re-verify after the clap");

            Assert.AreEqual(0, JewelryAspectTestKit.Count(flat, "new GameMessageUpdateObject"));
            StringAssert.Contains(flat, "PrismaticDriftStone.UpdateObj(player, target);");
            Assert.AreEqual(0, JewelryAspectTestKit.Count(flat, "NumTimesTinkered"));
            Assert.AreEqual(0, JewelryAspectTestKit.Count(flat, "TinkerLog"));
            StringAssert.Contains(flat, "SalvageTool.TryConsume(player, s, out var remaining)");
        }
    }
}
