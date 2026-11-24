using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.SalvageRoller;
using ACE.Server.SpellReroll;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SalvageRollerEligibility.VerifyBasket (Docs/SalvageRoller/DESIGN.md 5.3, plan step 5): the whole-basket rule,
    /// the check order, refusal text that matches the manual path byte for byte, and Retained/Attuned targets
    /// accepted. A Player cannot be built under this harness, so the rules run through the Actor view; the
    /// production Actor wiring is pinned by a source scan.
    ///
    /// Every PropertyManager key the verify reads is seeded in TestInitialize and restored to its default in
    /// TestCleanup, and every static seam is restored there too.
    /// </summary>
    [TestClass]
    public class SalvageRollerEligibilityTests
    {
        private static uint nextGuid = 0x7D480000;   // static guid range, clear of GuidManager and the other test classes
        private static uint nextWcid = 993400;

        private static readonly string[] BoolKeys =
        {
            "salvage_roller_enabled", "weapon_mods_enabled", "equipment_mods_enabled", "serpentine_reroll_enabled", "allow_combat_mode_crafting", "jewelry_aspects_enabled",
        };

        private Func<uint, (bool Ok, bool Exists)> savedBatchProbe;
        private Func<uint, (bool Ready, string Reason)> savedEscrowProbe;
        private Func<WorldObject, WorldObject, string> savedTinkerLock;
        private Func<WorldObject, string> savedSerpentine;

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Setup()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("salvage_roller_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("equipment_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("serpentine_reroll_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("allow_combat_mode_crafting", false));
            Assert.IsTrue(PropertyManager.ModifyBool("jewelry_aspects_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyLong("salvage_roller_max_candidates", 100));

            savedBatchProbe = SalvageRollerEligibility.BatchProbe;
            savedEscrowProbe = SalvageRollerEligibility.EscrowProbe;
            savedTinkerLock = SalvageRollerEligibility.TinkerLockRefusal;
            savedSerpentine = SalvageRollerEligibility.SerpentineTargetStateRefusal;

            SalvageRollerEligibility.BatchProbe = _ => (true, false);
            SalvageRollerEligibility.EscrowProbe = _ => (true, null);
            SalvageRollerEligibility.TinkerLockRefusal = (_, __) => null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            SalvageRollerEligibility.BatchProbe = savedBatchProbe;
            SalvageRollerEligibility.EscrowProbe = savedEscrowProbe;
            SalvageRollerEligibility.TinkerLockRefusal = savedTinkerLock;
            SalvageRollerEligibility.SerpentineTargetStateRefusal = savedSerpentine;

            foreach (var key in BoolKeys)
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);

            PropertyManager.ModifyLong("salvage_roller_max_candidates", DefaultPropertyManager.DefaultLongProperties["salvage_roller_max_candidates"].Item);
        }

        // ---------------- fixtures ----------------

        private static WorldObject Hammer(MaterialType? material, int structure = 10, int max = 10, bool tool = true, string name = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                    { PropertyInt.Structure, structure },
                    { PropertyInt.MaxStructure, max },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name ?? $"Hammer of {material}" } },
            };

            if (material != null)
                weenie.PropertiesInt[PropertyInt.MaterialType] = (int)material.Value;

            if (tool)
                weenie.PropertiesInt[PropertyInt.SalvageToolCharges] = max;

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Bag(MaterialType material) => Hammer(material, structure: 100, max: 100, tool: false, name: $"Salvage ({material})");

        private static WorldObject Weapon(string name = "Test Sword", Dictionary<PropertyInt, int> ints = null, Dictionary<PropertyBool, bool> bools = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            if (ints != null)
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;

            if (bools != null)
                weenie.PropertiesBool = new Dictionary<PropertyBool, bool>(bools);

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Armor(Dictionary<PropertyInt, int> ints = null, string name = "Test Hauberk")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)EquipMask.ChestArmor },
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            if (ints != null)
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;

            var wo = new Clothing(weenie, new ObjectGuid(nextGuid++));

            EquipmentModManager.StampOriginalGearRatings(wo);

            return wo;
        }

        private static WorldObject Named(string name, ItemType itemType = ItemType.Misc)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)itemType } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A player view holding <paramref name="pack"/> in its inventory and nothing else.</summary>
        private static SalvageRollerEligibility.Actor Actor(IEnumerable<WorldObject> pack, Action<SalvageRollerEligibility.Actor> tweak = null)
        {
            var held = new HashSet<WorldObject>(pack);

            var actor = new SalvageRollerEligibility.Actor
            {
                CharacterId = 0x50000001,
                AccountId = 4242,
                InInventory = held.Contains,
                PvpTemplateRefusal = (_, __) => null,
            };

            tweak?.Invoke(actor);
            return actor;
        }

        private static string Refusal(IReadOnlyList<WorldObject> offered, Action<SalvageRollerEligibility.Actor> tweak = null, int? requested = null)
        {
            var ok = SalvageRollerEligibility.VerifyBasket(Actor(offered, tweak), offered, requested ?? offered.Count,
                out var action, out var target, out var hammers, out var refusal);

            if (!ok)
            {
                // whole-basket rule: a refusal hands back nothing resolved
                Assert.AreEqual(SalvageRollerAction.None, action);
                Assert.IsNull(target);
                Assert.AreEqual(0, hammers.Count);
                Assert.IsNotNull(refusal);
            }
            else
            {
                Assert.IsNull(refusal);
            }

            return refusal;
        }

        private static List<WorldObject> Basket(WorldObject target, params WorldObject[] hammers)
        {
            var list = new List<WorldObject>();
            if (target != null) list.Add(target);
            list.AddRange(hammers);
            return list;
        }

        // ---------------- accepted baskets ----------------

        [TestMethod]
        public void Tourmaline_AcceptsOneWeaponAndFullHammers_AndHandsBackTheParts()
        {
            var sword = Weapon();
            var h1 = Hammer(MaterialType.Tourmaline);
            var h2 = Hammer(MaterialType.Tourmaline);
            var offered = Basket(h1, sword, h2);   // order in the sale does not matter

            Assert.IsTrue(SalvageRollerEligibility.VerifyBasket(Actor(offered), offered, 3, out var action, out var target, out var hammers, out var refusal), refusal);

            Assert.AreEqual(SalvageRollerAction.TourmalineReroll, action);
            Assert.AreSame(sword, target);
            CollectionAssert.AreEquivalent(new[] { h1, h2 }, hammers);
            Assert.AreEqual(20, SalvageRollerEligibility.RollCount(hammers), "one roll per charge");
        }

        // ---------------- the post-detach backstop (DESIGN 5.4 step 4.1) ----------------

        [TestMethod]
        public void DetachedBasket_IsAccepted_WithNothingInThePackAndThePlayerBusy()
        {
            var sword = Weapon();
            var h1 = Hammer(MaterialType.Tourmaline);
            var h2 = Hammer(MaterialType.Tourmaline);
            var offered = Basket(sword, h1, h2);

            // the pre-detach gate refuses this actor twice over: busy, and nothing in the pack
            Assert.AreEqual(SalvageRollerEligibility.BusyMessage, Refusal(offered, a => { a.IsBusy = true; a.InInventory = _ => false; }));
            Assert.AreEqual(SalvageRollerEligibility.FuelNotInInventoryMessage(h1.Name), Refusal(offered, a => a.InInventory = _ => false));

            // the backstop accepts the very same basket and resolves it the same way
            var detachedActor = Actor(offered, a => { a.IsBusy = true; a.InInventory = _ => false; });

            Assert.IsTrue(SalvageRollerEligibility.VerifyDetachedBasket(detachedActor, offered, out var action, out var target, out var hammers, out var refusal), refusal);
            Assert.AreEqual(SalvageRollerAction.TourmalineReroll, action);
            Assert.AreSame(sword, target);
            CollectionAssert.AreEquivalent(new[] { h1, h2 }, hammers);
        }

        [TestMethod]
        public void DetachedBasket_StillRunsEveryOtherRule()
        {
            var sword = Weapon();
            var hammer = Hammer(MaterialType.Tourmaline);
            var offered = Basket(sword, hammer);

            string Detached(Action<SalvageRollerEligibility.Actor> tweak)
            {
                var actor = Actor(offered, a => { a.InInventory = _ => false; tweak?.Invoke(a); });
                SalvageRollerEligibility.VerifyDetachedBasket(actor, offered, out _, out _, out _, out var why);
                return why;
            }

            Assert.AreEqual(Player.GetMuleRefusal(MuleAction.Craft), Detached(a => a.IsMule = true));
            Assert.AreEqual(SalvageRollerEligibility.PeaceModeMessage, Detached(a => a.InCombatMode = true));

            SalvageRollerEligibility.BatchProbe = _ => (true, true);
            Assert.AreEqual(SalvageRollerEligibility.BatchExistsMessage, Detached(null), "the one-batch probe still runs");

            var mixed = Basket(sword, Hammer(MaterialType.Tourmaline), Hammer(MaterialType.Amethyst));
            SalvageRollerEligibility.VerifyDetachedBasket(Actor(mixed, a => a.InInventory = _ => false), mixed, out _, out _, out _, out var mixedRefusal);
            Assert.AreEqual(SalvageRollerEligibility.MixedMaterialsMessage, mixedRefusal);
        }

        [TestMethod]
        public void Amethyst_RoutesToTheSwap()
        {
            var offered = Basket(Weapon(), Hammer(MaterialType.Amethyst));

            Assert.IsTrue(SalvageRollerEligibility.VerifyBasket(Actor(offered), offered, 2, out var action, out _, out _, out var refusal), refusal);
            Assert.AreEqual(SalvageRollerAction.AmethystSwap, action);
        }

        [TestMethod]
        public void Obsidian_DecidesConvertOrRerollOffTheTarget_AsTheBagPathDoes()
        {
            var rated = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 2 } });
            var offered = Basket(rated, Hammer(MaterialType.Obsidian));

            Assert.IsTrue(SalvageRollerEligibility.VerifyBasket(Actor(offered), offered, 2, out var action, out _, out _, out var refusal), refusal);
            Assert.AreEqual(SalvageRollerAction.ObsidianConvert, action);

            // convert it for real, and the same item now routes to the reroll
            Assert.IsNotNull(EquipmentModManager.ApplyConvertOrReroll(rated, EquipmentModManager.ModAction.Convert, 2,
                (p, v) => EquipmentModManager.WriteRatingToItem(rated, p, v)));

            Assert.IsTrue(SalvageRollerEligibility.VerifyBasket(Actor(offered), offered, 2, out action, out _, out _, out refusal), refusal);
            Assert.AreEqual(SalvageRollerAction.ObsidianReroll, action);
        }

        [TestMethod]
        public void Serpentine_RoutesThroughTheManagersTargetRule()
        {
            WorldObject seen = null;
            SalvageRollerEligibility.SerpentineTargetStateRefusal = t => { seen = t; return null; };

            var robe = Named("Test Robe", ItemType.Clothing);
            var offered = Basket(robe, Hammer(MaterialType.Serpentine));

            Assert.IsTrue(SalvageRollerEligibility.VerifyBasket(Actor(offered), offered, 2, out var action, out _, out _, out var refusal), refusal);
            Assert.AreEqual(SalvageRollerAction.SerpentineReroll, action);
            Assert.AreSame(robe, seen);

            // and the manager's own line comes through untouched
            var line = SpellRerollManager.GetRefusalMessage(RerollRefusal.Unclassifiable, robe);
            SalvageRollerEligibility.SerpentineTargetStateRefusal = t => line;
            Assert.AreEqual(line, Refusal(offered));
        }

        private static WorldObject Ring(int attack, int defense, int utility, string name = "Test Ring") =>
            JewelryAspectApplicatorTests.Jewelry(name, EquipMask.FingerWearLeft | EquipMask.FingerWearRight, attack, defense, utility);

        [TestMethod]
        public void GreenJade_RoutesToTheSwap_ForAnAspectedPiece_MidFillIncluded()
        {
            foreach (var ring in new[] { Ring(1, 0, 0), Ring(3, 3, 3), Ring(5, 3, 2) })
            {
                var offered = Basket(ring, Hammer(MaterialType.GreenJade));

                Assert.IsTrue(SalvageRollerEligibility.VerifyBasket(Actor(offered), offered, 2, out var action, out var target, out _, out var refusal), refusal);
                Assert.AreEqual(SalvageRollerAction.GreenJadeSwap, action);
                Assert.AreSame(ring, target);
            }
        }

        [TestMethod]
        public void GreenJade_TargetRefusals_AreTheHandApplyLines()
        {
            var hammer = Hammer(MaterialType.GreenJade);

            // not aspected yet: GreenJadeManager.TargetStateRefusal verbatim
            var bare = Ring(0, 0, 0);
            Assert.AreEqual("The Test Ring has no Aspect yet. Use a Jewelers Aspect Gem on it first.", Refusal(Basket(bare, hammer)));
            Assert.AreEqual(GreenJadeManager.TargetStateRefusal(bare), Refusal(Basket(bare, hammer)));

            // not a ring, bracelet or necklace
            var sword = Weapon();
            Assert.AreEqual(GreenJadeManager.RefusalNotJewelry, Refusal(Basket(sword, hammer)));

            // worn: the hand path's worn line
            var worn = Ring(2, 2, 2);
            worn.CurrentWieldedLocation = EquipMask.FingerWearLeft;
            // production InInventory (FindObject in MyInventory) excludes equipped items, so model that: the worn line
            // must win over the pack line
            Assert.AreEqual("Take the Test Ring off first.", Refusal(Basket(worn, hammer), a => a.InInventory = wo => wo.CurrentWieldedLocation == null));

            // out of the pack: the roller's own inventory line, as for every other material
            var loose = Ring(2, 2, 2);
            Assert.AreEqual($"You must remove the Test Ring and place it in your inventory first.",
                Refusal(Basket(loose, hammer), a => a.InInventory = wo => wo != loose));
        }

        [TestMethod]
        public void GreenJade_IsGatedOnJewelryAspectsEnabled_AndNeedsAFullHammer()
        {
            var ring = Ring(2, 2, 2);

            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);
            Assert.AreEqual("The Salvage Roller cannot work green jade right now.", Refusal(Basket(ring, Hammer(MaterialType.GreenJade))));
            PropertyManager.ModifyBool("jewelry_aspects_enabled", true);

            // the manual path takes a part-used Hammer or a bag; the roller takes full Hammers only
            Assert.AreEqual(SalvageRollerEligibility.NotFullMessage("Hammer of GreenJade"), Refusal(Basket(ring, Hammer(MaterialType.GreenJade, structure: 4))));
            Assert.IsNull(Refusal(Basket(ring, Hammer(MaterialType.GreenJade))));

            // the other jewelry material is not Green Jade
            Assert.AreEqual(SalvageRollerEligibility.UnsupportedMaterialMessage, Refusal(Basket(ring, Hammer(MaterialType.Granite))));
        }

        [TestMethod]
        public void GreenJade_NamesItselfInTheUnsupportedLine_AndTheActionName()
        {
            StringAssert.Contains(SalvageRollerEligibility.UnsupportedMaterialMessage, "green jade");
            Assert.AreEqual("green jade", SalvageRollerEligibility.MaterialName(MaterialType.GreenJade));
            Assert.AreEqual("green jade swaps", SalvageRollerManager.ActionName(SalvageRollerAction.GreenJadeSwap));
        }

        [TestMethod]
        public void ActionEnum_GreenJadeSwapIsSixAndMatchesTheSchemaTwin()
        {
            Assert.AreEqual(6, (int)SalvageRollerAction.GreenJadeSwap);
            Assert.AreEqual((int)SalvageRollerAction.GreenJadeSwap, (int)ACE.Database.Models.Shard.SalvageBatchAction.GreenJadeSwap);
        }

        [TestMethod]
        public void SerpentineSeam_DefaultsToTheManagersOwnRule()
        {
            var method = savedSerpentine.Method;

            Assert.AreEqual(typeof(SpellRerollManager), method.DeclaringType);
            Assert.AreEqual(nameof(SpellRerollManager.TargetStateRefusal), method.Name);
        }

        [TestMethod]
        public void RetainedAndAttunedTargets_AreAccepted_AsOnTheManualPath()
        {
            var sword = Weapon(ints: new Dictionary<PropertyInt, int> { { PropertyInt.Attuned, (int)AttunedStatus.Attuned } },
                bools: new Dictionary<PropertyBool, bool> { { PropertyBool.Retained, true } });

            Assert.IsTrue(sword.Retained);
            Assert.AreEqual(AttunedStatus.Attuned, sword.Attuned);

            Assert.IsNull(Refusal(Basket(sword, Hammer(MaterialType.Tourmaline))));

            var plate = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 1 }, { PropertyInt.Attuned, (int)AttunedStatus.Attuned } });
            plate.SetProperty(PropertyBool.Retained, true);

            Assert.IsNull(Refusal(Basket(plate, Hammer(MaterialType.Obsidian))));
        }

        // ---------------- refusals, in DESIGN.md 5.3's order ----------------

        [TestMethod]
        public void Step1_TheSwitchOff_RefusesFirst()
        {
            PropertyManager.ModifyBool("salvage_roller_enabled", false);

            // even a basket that is wrong in every other way gets the switch's line
            Assert.AreEqual(SalvageRollerEligibility.DisabledMessage, Refusal(Basket(Named("Rock"), Named("Stick"))));
        }

        [TestMethod]
        public void Step2_Shape_EveryWholeBasketRefusal()
        {
            var sword = Weapon();
            var t1 = Hammer(MaterialType.Tourmaline);

            Assert.AreEqual(SalvageRollerEligibility.LinesDroppedMessage, Refusal(Basket(sword, t1), requested: 3), "a line the sell path dropped refuses all of it");
            Assert.AreEqual(SalvageRollerEligibility.NoTargetMessage, Refusal(Basket(null, t1, Hammer(MaterialType.Tourmaline))));
            Assert.AreEqual(SalvageRollerEligibility.TooManyTargetsMessage, Refusal(Basket(sword, Weapon("Other Sword"), t1)));
            Assert.AreEqual(SalvageRollerEligibility.NoHammersMessage("Test Sword"), Refusal(Basket(sword)));
            Assert.AreEqual("Offer full Hammers with the Test Sword. Each charge is one roll.", Refusal(Basket(sword)));

            var bag = Bag(MaterialType.Tourmaline);
            Assert.AreEqual($"The {bag.Name} is not a Hammer. The Salvage Roller takes full Hammers only, not salvage bags.", Refusal(Basket(sword, t1, bag)));

            Assert.AreEqual(SalvageRollerEligibility.MixedMaterialsMessage, Refusal(Basket(sword, t1, Hammer(MaterialType.Amethyst))));
        }

        [TestMethod]
        public void Step2_OverCap_IsRefusedNotTrimmed_InTheDesignsWords()
        {
            var hammers = Enumerable.Range(0, 11).Select(_ => Hammer(MaterialType.Tourmaline)).ToArray();

            Assert.AreEqual("The Salvage Roller takes at most 100 rolls (10 Hammers); you offered 110.", Refusal(Basket(Weapon(), hammers)));

            // exactly the cap is fine
            Assert.IsNull(Refusal(Basket(Weapon(), hammers.Take(10).ToArray())));
        }

        [TestMethod]
        public void Step2_TheCapTunable_IsClampedTo1Through100()
        {
            var six = Enumerable.Range(0, 6).Select(_ => Hammer(MaterialType.Tourmaline)).ToArray();

            PropertyManager.ModifyLong("salvage_roller_max_candidates", 50);
            Assert.AreEqual(50, SalvageRollerEligibility.MaxRolls());
            Assert.AreEqual("The Salvage Roller takes at most 50 rolls (5 Hammers); you offered 60.", Refusal(Basket(Weapon(), six)));

            PropertyManager.ModifyLong("salvage_roller_max_candidates", 1000);
            Assert.AreEqual(100, SalvageRollerEligibility.MaxRolls());

            PropertyManager.ModifyLong("salvage_roller_max_candidates", 0);
            Assert.AreEqual(1, SalvageRollerEligibility.MaxRolls());
            Assert.AreEqual("The Salvage Roller takes at most 1 rolls; you offered 10.", Refusal(Basket(Weapon(), Hammer(MaterialType.Tourmaline))));
        }

        [TestMethod]
        public void Step3_MaterialRouting()
        {
            var sword = Weapon();

            Assert.AreEqual("The Salvage Roller does not work tiger eye.", Refusal(Basket(sword, Hammer(MaterialType.TigerEye))));
            Assert.AreEqual(SalvageRollerEligibility.UnsupportedMaterialMessage, Refusal(Basket(sword, Hammer(MaterialType.Granite))));

            // the alpha maximizer carries no MaterialType, so it fails every material match
            Assert.AreEqual(SalvageRollerEligibility.UnsupportedMaterialMessage, Refusal(Basket(sword, Hammer(null, structure: 100, max: 100))));

            PropertyManager.ModifyBool("weapon_mods_enabled", false);
            Assert.AreEqual("The Salvage Roller cannot work tourmaline right now.", Refusal(Basket(sword, Hammer(MaterialType.Tourmaline))));
            Assert.AreEqual("The Salvage Roller cannot work amethyst right now.", Refusal(Basket(sword, Hammer(MaterialType.Amethyst))));

            PropertyManager.ModifyBool("equipment_mods_enabled", false);
            Assert.AreEqual("The Salvage Roller cannot work obsidian right now.", Refusal(Basket(Armor(), Hammer(MaterialType.Obsidian))));

            PropertyManager.ModifyBool("serpentine_reroll_enabled", false);
            Assert.AreEqual("The Salvage Roller cannot work serpentine right now.", Refusal(Basket(sword, Hammer(MaterialType.Serpentine))));
        }

        [TestMethod]
        public void Step4_TheManualEntryChecks_InRecipeManagersOrder()
        {
            var sword = Weapon();
            var hammer = Hammer(MaterialType.Tourmaline);
            var offered = Basket(sword, hammer);

            // mule first, with the mule system's own line
            Assert.AreEqual("A mule cannot craft or tinker.", Refusal(offered, a => { a.IsMule = true; a.IsBusy = true; }));
            Assert.AreEqual(Player.GetMuleRefusal(MuleAction.Craft), Refusal(offered, a => a.IsMule = true));

            // then the PvP template, with the source and target it was asked about
            WorldObject askedSource = null, askedTarget = null;
            Assert.AreEqual("templated", Refusal(offered, a =>
            {
                a.IsBusy = true;
                a.PvpTemplateRefusal = (s, t) => { askedSource = s; askedTarget = t; return "templated"; };
            }));
            Assert.AreSame(hammer, askedSource);
            Assert.AreSame(sword, askedTarget);

            // busy, then combat mode (only while allow_combat_mode_crafting is off)
            Assert.AreEqual(SalvageRollerEligibility.BusyMessage, Refusal(offered, a => { a.IsBusy = true; a.InCombatMode = true; }));
            Assert.AreEqual(SalvageRollerEligibility.PeaceModeMessage, Refusal(offered, a => a.InCombatMode = true));

            PropertyManager.ModifyBool("allow_combat_mode_crafting", true);
            Assert.IsNull(Refusal(offered, a => a.InCombatMode = true));

            // then the tinker lock, with each Hammer as the source
            SalvageRollerEligibility.TinkerLockRefusal = (s, t) => s == hammer && t == sword ? "locked" : null;
            Assert.AreEqual("locked", Refusal(offered));
        }

        [TestMethod]
        public void TinkerLockSeam_DefaultsToTinkerLock()
        {
            var body = PooledLootSourceText.Read("Source/ACE.Server/SalvageRoller/SalvageRollerEligibility.cs");

            StringAssert.Contains(body, "TinkerLock.IsRefused(source, target) ? TinkerLock.RefusalMessage(source, target) : null");
        }

        [TestMethod]
        public void Step5_Fuel_InThePack_AndFull()
        {
            var sword = Weapon();
            var hammer = Hammer(MaterialType.Tourmaline, name: "Hammer of Tourmaline");
            var offered = Basket(sword, hammer);

            // not in the pack: the manual path's own line
            Assert.AreEqual("The Hammer of Tourmaline must be in your inventory.",
                Refusal(offered, a => { var pack = new HashSet<WorldObject> { sword }; a.InInventory = pack.Contains; }));

            // part-used: refused, stricter than manual by owner ruling
            var partUsed = Hammer(MaterialType.Tourmaline, structure: 7, name: "Worn Hammer");
            Assert.AreEqual("The Worn Hammer is not full. The Salvage Roller takes full Hammers only.", Refusal(Basket(sword, hammer, partUsed)));
        }

        [TestMethod]
        public void Step6_Target_NotInThePack_IsTheManualPathsLine()
        {
            var sword = Weapon();
            var hammer = Hammer(MaterialType.Tourmaline);

            Assert.AreEqual("You must remove the Test Sword and place it in your inventory first.",
                Refusal(Basket(sword, hammer), a => { var pack = new HashSet<WorldObject> { hammer }; a.InInventory = pack.Contains; }));
        }

        [TestMethod]
        public void Step6_TargetInventoryLine_IsByteIdenticalToAllFourManagers()
        {
            const string literal = "$\"You must remove the {target.Name} and place it in your inventory first.\"";

            foreach (var path in new[]
            {
                "Source/ACE.Server/WeaponMods/WeaponModManager.cs",
                "Source/ACE.Server/EquipmentMods/EquipmentModManager.cs",
                "Source/ACE.Server/SpellReroll/SpellRerollManager.cs",
                "Source/ACE.Server/Entity/GreenJadeManager.cs",
            })
            {
                var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(path), "internal static string TargetInventoryRefusal(Player player, WorldObject target)");
                StringAssert.Contains(body, literal, path);
            }

            Assert.AreEqual("You must remove the Thing and place it in your inventory first.", SalvageRollerEligibility.TargetNotInInventoryMessage("Thing"));
        }

        [TestMethod]
        public void Step6_TargetState_IsTheManagersLine_ByteForByte()
        {
            var hammer = Hammer(MaterialType.Tourmaline, name: "Hammer of Tourmaline");

            // a weapon salvage on something that is not a weapon
            var rock = Named("Rock");
            Assert.AreEqual(WeaponModManager.RefusalMessage(WeaponModManager.WeaponModRefusal.NotAWeapon, hammer.Name, rock), Refusal(Basket(rock, hammer)));
            Assert.AreEqual("The Rock is not a weapon. Only melee weapons, missile weapons and casters can carry weapon mods.", Refusal(Basket(rock, hammer)));

            // a weapon with no workmanship
            var plain = Weapon("Plain Sword");
            plain.RemoveProperty(PropertyInt.ItemWorkmanship);
            Assert.AreEqual("The Plain Sword has no workmanship, so it cannot hold a special modifier.", Refusal(Basket(plain, hammer)));

            // Obsidian on a shield, and on armor with nothing to do
            var obsidian = Hammer(MaterialType.Obsidian);
            var shield = Armor(new Dictionary<PropertyInt, int> { { PropertyInt.CombatUse, (int)CombatUse.Shield } }, "Test Shield");
            Assert.AreEqual("The Test Shield cannot carry equipment mods. Only armor and clothing, excluding underclothes, can.", Refusal(Basket(shield, obsidian)));
            Assert.AreEqual("The Test Hauberk has no gear ratings to convert and no mods to reroll.", Refusal(Basket(Armor(), obsidian)));

            var expected = EquipmentModManager.TargetStateRefusal(Armor(), false, out _, out _);
            Assert.AreEqual(expected, Refusal(Basket(Armor(), obsidian)));
        }

        [TestMethod]
        public void Step7_TheAccount_ReadFailure_OpenBatch_AndEscrow()
        {
            var offered = Basket(Weapon(), Hammer(MaterialType.Tourmaline));

            uint askedCharacter = 0, askedAccount = 0;

            SalvageRollerEligibility.BatchProbe = id => { askedCharacter = id; return (false, false); };
            Assert.AreEqual(SalvageRollerEligibility.BatchReadFailedMessage, Refusal(offered), "a failed read never passes for no batch");
            Assert.AreEqual(0x50000001u, askedCharacter);

            SalvageRollerEligibility.BatchProbe = _ => (true, true);
            Assert.AreEqual(SalvageRollerEligibility.BatchExistsMessage, Refusal(offered));

            SalvageRollerEligibility.BatchProbe = _ => (true, false);
            SalvageRollerEligibility.EscrowProbe = id => { askedAccount = id; return (false, AccountVaultStore.EscrowUnavailableMessage); };
            Assert.AreEqual(AccountVaultStore.EscrowUnavailableMessage, Refusal(offered));
            Assert.AreEqual(4242u, askedAccount);

            SalvageRollerEligibility.EscrowProbe = _ => (false, null);
            Assert.AreEqual(AccountVaultStore.EscrowUnavailableMessage, Refusal(offered), "a not-ready escrow with no reason still refuses");
        }

        [TestMethod]
        public void Step7_RunsLast_SoAnEarlierRefusalNeverTouchesTheDatabase()
        {
            var probed = false;
            SalvageRollerEligibility.BatchProbe = _ => { probed = true; return (true, false); };
            SalvageRollerEligibility.EscrowProbe = _ => { probed = true; return (true, null); };

            Assert.IsNotNull(Refusal(Basket(Named("Rock"), Hammer(MaterialType.Tourmaline))));
            Assert.IsFalse(probed);
        }

        // ---------------- wiring ----------------

        [TestMethod]
        public void ProductionActor_ReadsThePlayerTheWayTheManualPathDoes()
        {
            var body = PooledLootSourceText.Read("Source/ACE.Server/SalvageRoller/SalvageRollerEligibility.cs");

            StringAssert.Contains(body, "IsMule = player.MuleBlocked(MuleAction.Craft, notify: false)");
            StringAssert.Contains(body, "InInventory = wo => player.FindObject(wo.Guid.Full, Player.SearchLocations.MyInventory) != null");
            StringAssert.Contains(body, "PvpTemplateRefusal = (source, target) => player.PvpTemplateBlocked(PvpTemplateAction.UseWithTarget, source, target)");
            StringAssert.Contains(body, "InCombatMode = player.CombatMode != CombatMode.NonCombat");
            StringAssert.Contains(body, "DatabaseManager.Shard.BaseDatabase.TryGetSalvageBatchForCharacter(characterId, out var batch)");
            StringAssert.Contains(body, "store.IsEscrowReady(out var reason)");
        }

        [TestMethod]
        public void ActionEnum_MatchesTheSchemaEnum_MemberForMember()
        {
            var roller = Enum.GetValues(typeof(SalvageRollerAction)).Cast<SalvageRollerAction>().ToList();
            var schema = Enum.GetValues(typeof(SalvageBatchAction)).Cast<SalvageBatchAction>().ToList();

            Assert.AreEqual(schema.Count, roller.Count);

            foreach (var member in roller)
            {
                Assert.IsTrue(Enum.TryParse<SalvageBatchAction>(member.ToString(), out var twin), $"{member} has no schema twin");
                Assert.AreEqual((byte)member, (byte)twin, $"{member} differs in value from the schema");
            }

            foreach (var member in schema)
                Assert.IsTrue(Enum.IsDefined(typeof(SalvageRollerAction), member.ToString()), $"schema {member} has no roller twin");
        }
    }
}
