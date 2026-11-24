using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.SpellReroll;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The behaviour-preserving manager extraction for the Salvage Roller (Docs/SalvageRoller/DESIGN.md 5.8, plan
    /// step 4), against the section 9 invariants:
    ///
    ///   1. refusal text is byte-identical to the bag path's;
    ///   2. the bag paths' order of operations is unchanged;
    ///   3. the mutation functions touch no Player, network, save or fuel.
    ///
    /// The refusal lines are pinned word for word here because the bag path now SENDS what these helpers return:
    /// a pin on the helper is a pin on what a player using a bag sees. Order is pinned against the source text,
    /// because VerifyUseRequirements needs a live Player and cannot run under this harness.
    /// </summary>
    [TestClass]
    public class SalvageRollerExtractionTests
    {
        private static uint nextGuid = 0x7D400000;   // static guid range, clear of GuidManager and the other test classes
        private static uint nextWcid = 993000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static WorldObject Named(string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A database-free armor piece, built and stamped the way EquipmentModApplicationTests.MakeItem does.</summary>
        private static WorldObject Armor(Dictionary<PropertyInt, int> ints = null, bool workmanship = true, EquipMask slot = EquipMask.ChestArmor)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)slot },
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Hauberk" } },
            };

            if (workmanship)
                weenie.PropertiesInt[PropertyInt.ItemWorkmanship] = 10;

            if (ints != null)
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;

            var wo = new Clothing(weenie, new ObjectGuid(nextGuid++));

            EquipmentModManager.StampOriginalGearRatings(wo);

            return wo;
        }

        // ---------------- invariant 1: refusal text ----------------

        [TestMethod]
        public void Weapon_RefusalLines_AreTheBagPathsWords()
        {
            var target = Named("Test Blade");

            Assert.IsNull(WeaponModManager.RefusalMessage(WeaponModManager.WeaponModRefusal.None, "Bag", target));

            Assert.AreEqual("The Test Blade is not a weapon. Only melee weapons, missile weapons and casters can carry weapon mods.",
                WeaponModManager.RefusalMessage(WeaponModManager.WeaponModRefusal.NotAWeapon, "Bag", target));
            Assert.AreEqual("The Test Blade has no workmanship, so it cannot hold a special modifier.",
                WeaponModManager.RefusalMessage(WeaponModManager.WeaponModRefusal.NoWorkmanship, "Bag", target));
            Assert.AreEqual("Every one of the Test Blade's slots is taken by work this salvage cannot undo. There is nothing left to work with.",
                WeaponModManager.RefusalMessage(WeaponModManager.WeaponModRefusal.NoAvailableSlots, "Bag", target));
            Assert.AreEqual("The Test Blade was finished by a Prismatic Drift Stone. Its tinkering is sealed and no salvage will reopen it.",
                WeaponModManager.RefusalMessage(WeaponModManager.WeaponModRefusal.DriftStoneLocked, "Bag", target));
            Assert.AreEqual("The Test Blade's tinker log does not account for everything applied to it, so its current tinkers cannot be safely removed.",
                WeaponModManager.RefusalMessage(WeaponModManager.WeaponModRefusal.TinkerLogMismatch, "Bag", target));

            // the catch-all names the SOURCE, so the roller must hand in the Hammer's name
            Assert.AreEqual("The Hammer of Tourmaline cannot be used on the Test Blade.",
                WeaponModManager.RefusalMessage(WeaponModManager.WeaponModRefusal.NotAModMaterial, "Hammer of Tourmaline", target));
        }

        [TestMethod]
        public void Equipment_TargetStateRefusal_IsTheBagPathsWords_InTheBagPathsOrder()
        {
            // not an eligible slot, no mods: the plain "cannot carry" line
            // a shield is ItemType.Armor yet refused outright by IsEligibleSlot
            var shield = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.CombatUse, (int)CombatUse.Shield } }, slot: EquipMask.Shield);
            Assert.IsTrue(shield.IsShield, "precondition: the fixture is a shield");
            Assert.AreEqual("The Test Hauberk cannot carry equipment mods. Only armor and clothing, excluding underclothes, can.",
                EquipmentModManager.TargetStateRefusal(shield, false, out var action, out var count));
            Assert.AreEqual(EquipmentModManager.ModAction.None, action);

            // eligible, but no workmanship
            Assert.AreEqual("The Test Hauberk has no workmanship, so it cannot hold an equipment mod.",
                EquipmentModManager.TargetStateRefusal(Armor(workmanship: false), false, out _, out _));

            // eligible, workmanship, nothing to do
            Assert.AreEqual("The Test Hauberk has no gear ratings to convert and no mods to reroll.",
                EquipmentModManager.TargetStateRefusal(Armor(), false, out _, out _));

            // the alpha maximizer on an unmodded item
            Assert.AreEqual("The Test Hauberk has no mods to maximize. Use obsidian salvage on it first to mint mods.",
                EquipmentModManager.TargetStateRefusal(Armor(), true, out _, out _));

            // eligible and rated: no refusal, and the action resolved exactly as ResolveAction would
            var rated = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 2 } });
            Assert.IsNull(EquipmentModManager.TargetStateRefusal(rated, false, out action, out count));
            Assert.AreEqual(EquipmentModManager.ModAction.Convert, action);
            Assert.AreEqual(2, count);
        }

        [TestMethod]
        public void Spell_RefusalLines_StillComeFromGetRefusalMessage()
        {
            // TargetStateRefusal returns GetRefusalMessage's text unchanged; pin the one every unrollable
            // looted-or-not item can reach without a treasure profile.
            var target = Named("Test Robe");

            Assert.AreEqual("The Test Robe has no item enchantments that can be reforged.",
                SpellRerollManager.GetRefusalMessage(RerollRefusal.NothingToReroll, target));
            Assert.AreEqual("The Test Robe was not found as treasure, so the salvage has no pattern to draw from. Only looted equipment can be reforged.",
                SpellRerollManager.GetRefusalMessage(RerollRefusal.Unclassifiable, target));
        }

        // ---------------- invariant 3: pure mutation functions ----------------

        [TestMethod]
        public void Equipment_Convert_RoutesEveryRatingWriteThroughTheSink_AndMintsTheRatingPoints()
        {
            var item = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 1 }, { PropertyInt.GearCrit, 1 } });
            var twin = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 1 }, { PropertyInt.GearCrit, 1 } });

            // what the bag path used to push through player.UpdateProperty, computed independently on a twin
            var expectedWrites = EquipmentModManager.SpendNaturalRatings(twin);

            var writes = new List<KeyValuePair<PropertyInt, int?>>();

            var lines = EquipmentModManager.ApplyConvertOrReroll(item, EquipmentModManager.ModAction.Convert, 2, (property, value) =>
            {
                writes.Add(new KeyValuePair<PropertyInt, int?>(property, value));
                EquipmentModManager.WriteRatingToItem(item, property, value);
            });

            Assert.IsNotNull(lines);
            Assert.AreEqual(2, lines.Count, "Convert mints one mod per rating point");
            CollectionAssert.AreEquivalent(expectedWrites.ToList(), writes, "every rating write goes through the sink, and only those");
            Assert.AreEqual(2, EquipmentModManager.GetModCount(item));
            Assert.AreEqual(2, EquipmentModManager.GetModCapacity(item));
            Assert.AreEqual(0, EquipmentModManager.SumGearRatings(item), "the natural ratings were the price");
        }

        [TestMethod]
        public void Equipment_Reroll_KeepsCapacityAndMintsItAfresh()
        {
            var item = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 2 } });

            EquipmentModManager.ApplyConvertOrReroll(item, EquipmentModManager.ModAction.Convert, 2, (p, v) => EquipmentModManager.WriteRatingToItem(item, p, v));

            var sinkCalls = 0;
            var lines = EquipmentModManager.ApplyConvertOrReroll(item, EquipmentModManager.ModAction.Reroll, EquipmentModManager.GetModCapacity(item), (p, v) => sinkCalls++);

            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual(0, sinkCalls, "a reroll spends no ratings");
            Assert.AreEqual(2, EquipmentModManager.GetModCount(item));
            Assert.AreEqual(2, EquipmentModManager.GetModCapacity(item));
        }

        [TestMethod]
        public void Equipment_AnyOtherAction_ReturnsNullAndChangesNothing()
        {
            var item = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 2 } });
            var sinkCalls = 0;

            Assert.IsNull(EquipmentModManager.ApplyConvertOrReroll(item, EquipmentModManager.ModAction.None, 2, (p, v) => sinkCalls++));
            Assert.IsNull(EquipmentModManager.ApplyConvertOrReroll(item, EquipmentModManager.ModAction.Maximize, 2, (p, v) => sinkCalls++));

            Assert.AreEqual(0, sinkCalls);
            Assert.AreEqual(2, EquipmentModManager.SumGearRatings(item));
            Assert.AreEqual(0, EquipmentModManager.GetModCount(item));
        }

        [TestMethod]
        public void Equipment_WriteRatingToItem_SetsOrRemoves_LikeUpdatePropertyDoes()
        {
            var item = Armor();

            EquipmentModManager.WriteRatingToItem(item, PropertyInt.GearDamage, 3);
            Assert.AreEqual(3, item.GetProperty(PropertyInt.GearDamage));

            EquipmentModManager.WriteRatingToItem(item, PropertyInt.GearDamage, null);
            Assert.IsNull(item.GetProperty(PropertyInt.GearDamage));
        }

        [TestMethod]
        public void Spell_ApplyPlan_SwapsKeepingTheCastProbability_AndSkipsASwapItCannotMake()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Robe" } },
                PropertiesSpellBook = new Dictionary<int, float> { { 1000, 2.5f }, { 5000, 2.0f } },
            };

            var item = new Clothing(weenie, new ObjectGuid(nextGuid++));

            var plan = new SpellRerollPlan();
            plan.Swaps.Add(new SpellRerollSwap(1000, 2000, 7));
            plan.Swaps.Add(new SpellRerollSwap(3000, 4000, 7));   // 3000 is not on the item: logged and skipped

            var applied = SpellRerollManager.ApplyPlan(item, plan, "SalvageRollerExtractionTests");

            Assert.AreEqual(1, applied.Count, "only the swap that landed is reported");
            Assert.AreEqual(1000, applied[0].OldSpell);

            var known = item.Biota.GetKnownSpellsIds(item.BiotaDatabaseLock);

            CollectionAssert.AreEquivalent(new List<int> { 2000, 5000 }, known);

            var probability = item.Biota.GetMatchingSpells(new HashSet<int> { 2000 }, item.BiotaDatabaseLock);
            Assert.AreEqual(2.5f, probability[2000], "the replacement keeps the old spell's cast probability");
        }

        [TestMethod]
        public void MutationFunctions_TouchNoPlayerNetworkSaveOrFuel()
        {
            var forbidden = new[] { "Player", "Session", "EnqueueSend", "SaveBiotaToDatabase", "TryConsume", "SendUseDoneEvent", "UpdateProperty(" };

            AssertPure("Source/ACE.Server/EquipmentMods/EquipmentModManager.cs",
                "public static List<string> ApplyConvertOrReroll(WorldObject target, ModAction action, int count, Action<PropertyInt, int?> ratingWrite)", forbidden);

            AssertPure("Source/ACE.Server/SpellReroll/SpellRerollManager.cs",
                "internal static List<SpellRerollSwap> ApplyPlan(WorldObject target, SpellRerollPlan plan, string logContext)", forbidden);

            AssertPure("Source/ACE.Server/WeaponMods/WeaponModManager.cs",
                "public static List<string> ApplyReroll(WorldObject target, WeaponClass weaponClass, double workmanship)", forbidden);

            AssertPure("Source/ACE.Server/WeaponMods/WeaponModManager.cs",
                "public static List<string> ApplySwap(WorldObject target, WeaponClass weaponClass, double workmanship)", forbidden);
        }

        // ---------------- invariant 2: the bag paths' order ----------------

        [TestMethod]
        public void BagPaths_StillCheckTargetInventoryThenFuelThenTargetState()
        {
            AssertVerifyOrder("Source/ACE.Server/WeaponMods/WeaponModManager.cs",
                "public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target, out WeaponModAction action)",
                "TargetStateRefusal(source.Name, target, action)");

            AssertVerifyOrder("Source/ACE.Server/EquipmentMods/EquipmentModManager.cs",
                "public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target, out ModAction action, out int count)",
                "TargetStateRefusal(target, isMaximizer, out action, out count)");

            AssertVerifyOrder("Source/ACE.Server/SpellReroll/SpellRerollManager.cs",
                "public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)",
                "TargetStateRefusal(target)");
        }

        [TestMethod]
        public void EquipmentHandleApply_StillMutatesThenSavesThenConsumes()
        {
            var file = PooledLootSourceText.Read("Source/ACE.Server/EquipmentMods/EquipmentModManager.cs");
            var body = PooledLootSourceText.MethodBody(file, "private static void HandleApply(Player player, WorldObject source, WorldObject target, ModAction action, int count)");

            var mutate = body.IndexOf("ApplyConvertOrReroll(target, action, count, (property, value) => player.UpdateProperty(target, property, value))", System.StringComparison.Ordinal);
            var save = body.IndexOf("target.SaveBiotaToDatabase()", System.StringComparison.Ordinal);
            var consume = body.IndexOf("SalvageTool.TryConsume(", System.StringComparison.Ordinal);

            Assert.IsTrue(mutate >= 0 && mutate < save && save < consume, "Equipment HandleApply order: mutate (pushing ratings), save, then consume");
        }

        [TestMethod]
        public void SpellHandleApply_StillPlansThenSwapsThenSavesThenConsumes()
        {
            var file = PooledLootSourceText.Read("Source/ACE.Server/SpellReroll/SpellRerollManager.cs");
            var body = PooledLootSourceText.MethodBody(file, "private static void HandleApply(Player player, WorldObject source, WorldObject target)");

            var plan = body.IndexOf("BuildPlan(target, n => ThreadSafeRandom.Next(0, n - 1), out var refusal)", System.StringComparison.Ordinal);
            var apply = body.IndexOf("ApplyPlan(target, plan, $\"SpellRerollManager.HandleApply({player.Name}, {target.Name})\")", System.StringComparison.Ordinal);
            var save = body.IndexOf("target.SaveBiotaToDatabase()", System.StringComparison.Ordinal);
            var consume = body.IndexOf("SalvageTool.TryConsume(", System.StringComparison.Ordinal);

            Assert.IsTrue(plan >= 0 && plan < apply && apply < save && save < consume, "Spell HandleApply order: plan, swap, save, then consume");
        }

        [TestMethod]
        public void HandleTierTransition_IsReachableFromTheRoller()
        {
            var method = typeof(WeaponModManager).GetMethod("HandleTierTransition", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.IsNotNull(method);
            Assert.IsTrue(method.IsAssembly, "HandleTierTransition must be internal so the take can run it on the real item");
        }

        // ---------------- helpers ----------------

        private static void AssertPure(string path, string signature, string[] forbidden)
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), signature);

            foreach (var token in forbidden)
                Assert.IsFalse(body.Contains(token, System.StringComparison.Ordinal), $"{signature} mentions {token}");
        }

        private static void AssertVerifyOrder(string path, string signature, string stateCall)
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), signature);

            var inventory = body.IndexOf("TargetInventoryRefusal(player, target)", System.StringComparison.Ordinal);
            var fuel = body.IndexOf("IsFullBag(source)", System.StringComparison.Ordinal);
            var state = body.IndexOf(stateCall, System.StringComparison.Ordinal);

            Assert.IsTrue(inventory >= 0, $"{path}: target inventory check missing");
            Assert.IsTrue(fuel > inventory, $"{path}: the fuel check must still follow the target inventory check");
            Assert.IsTrue(state > fuel, $"{path}: the target state check must still follow the fuel check");

            // the source inventory check still comes first of all, exactly as before
            var sourceInventory = body.IndexOf("player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory)", System.StringComparison.Ordinal);
            Assert.IsTrue(sourceInventory >= 0 && sourceInventory < inventory, $"{path}: the source inventory check must still precede the target's");
        }
    }
}
