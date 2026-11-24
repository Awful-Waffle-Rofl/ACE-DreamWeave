using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Paragon gem overflow confirmation: the shared WouldOverflow predicate, its agreement with the appraisal
    /// ", capped" tail, and the pure prompt-vs-apply decision. Weapons are in-memory weenies (nothing persisted).
    /// The two tunables the predicate reads are seeded in TestInitialize and restored in TestCleanup.
    /// </summary>
    [TestClass]
    public class ParagonModOverflowTests
    {
        private bool priorEnabled;
        private double priorScale;

        private static uint nextGuid = 0x7D7C0000;
        private static uint nextWcid = 994990;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            priorEnabled = PropertyManager.GetBool("weapon_mods_enabled").Item;
            priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0));
        }

        [TestCleanup]
        public void Restore()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", priorEnabled);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
        }

        private static WeaponModDefinition Row(WeaponModId id)
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id} is not a live row");
            return definition;
        }

        private static WeaponModDefinition FirstOfTier(WeaponModTier tier)
        {
            var row = WeaponModRegistry.AllMods.FirstOrDefault(m => m.Tier == tier);
            Assert.IsNotNull(row, $"fixture: no tier {tier} row");
            return row;
        }

        /// <summary>A melee weapon holding <paramref name="gems"/> copies of <paramref name="id"/> in the Paragon slots.</summary>
        private static WorldObject Weapon(WeaponModId id, int gems)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Paragon Overflow Test Sword" } },
            };

            var item = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            Assert.IsNotNull(item, "fixture: no WorldObject");

            for (var i = 0; i < gems; i++)
                item.SetProperty(ParagonModRegistry.Slots[i], (int)id);

            return item;
        }

        /// <summary>Writes a Tourmaline record that reads back as <paramref name="fraction"/> of the row's perfect roll.</summary>
        private static void SetRecordFraction(WorldObject weapon, WeaponModDefinition definition, double fraction)
        {
            var value = definition.Tier == WeaponModTier.B
                ? fraction
                : fraction * WeaponModValue.MaxMagnitude(definition, WeaponModValue.MagnitudeScale(), weapon);

            weapon.SetProperty(definition.Record, value);
        }

        // ================= WouldOverflow truth table =================

        [TestMethod]
        public void NoRecord_FourGemsPlusOne_IsExactlyPerfect_NotOverflow()
        {
            foreach (var definition in new[] { FirstOfTier(WeaponModTier.A), FirstOfTier(WeaponModTier.B) })
            {
                var weapon = Weapon(definition.Id, 4);

                Assert.IsFalse(WeaponModParagon.WouldOverflow(weapon, definition.Id), $"{definition.Id}: 0 + 5 gems is exactly 1.0");
                Assert.IsTrue(WeaponModParagon.WouldOverflow(weapon, definition.Id, 2), $"{definition.Id}: a sixth gem would pass it");
            }
        }

        [TestMethod]
        public void TierB_Record081_PlusOneGem_Overflows_Record080_DoesNot()
        {
            var definition = FirstOfTier(WeaponModTier.B);
            var weapon = Weapon(definition.Id, 0);

            SetRecordFraction(weapon, definition, 0.81);
            Assert.IsTrue(WeaponModParagon.WouldOverflow(weapon, definition.Id), "0.81 + 0.2 passes 1.0");

            SetRecordFraction(weapon, definition, 0.80);
            Assert.IsFalse(WeaponModParagon.WouldOverflow(weapon, definition.Id), "0.80 + 0.2 is exactly perfect");
        }

        [TestMethod]
        public void TierA_IntegerRow_RecordNearCeiling_Overflows()
        {
            var definition = WeaponModRegistry.AllMods.FirstOrDefault(m => m.Tier == WeaponModTier.A
                && WeaponModValue.MaxMagnitude(m, 1.0) >= 5.0);
            Assert.IsNotNull(definition, "fixture: a Tier A row with a ceiling of at least 5");

            var weapon = Weapon(definition.Id, 0);

            SetRecordFraction(weapon, definition, 0.81);
            Assert.IsTrue(WeaponModParagon.WouldOverflow(weapon, definition.Id), "Tier A 0.81 of ceiling + 0.2 passes 1.0");

            SetRecordFraction(weapon, definition, 0.5);
            Assert.IsFalse(WeaponModParagon.WouldOverflow(weapon, definition.Id), "Tier A 0.5 + 0.2 stays under");
        }

        [TestMethod]
        public void RecordAboveCeiling_Overflows_EvenWithNoExtraGem()
        {
            foreach (var definition in new[] { FirstOfTier(WeaponModTier.A), FirstOfTier(WeaponModTier.B) })
            {
                var weapon = Weapon(definition.Id, 0);

                SetRecordFraction(weapon, definition, 1.5);

                Assert.IsTrue(WeaponModParagon.WouldOverflow(weapon, definition.Id), $"{definition.Id}: above ceiling + 1 gem");
                Assert.IsTrue(WeaponModParagon.WouldOverflow(weapon, definition.Id, 0), $"{definition.Id}: above ceiling on its own");
            }
        }

        [TestMethod]
        public void NullWeapon_NonLiveRow_AreNotOverflow()
        {
            Assert.IsFalse(WeaponModParagon.WouldOverflow(null, FirstOfTier(WeaponModTier.B), 1.0));
            Assert.IsFalse(WeaponModParagon.WouldOverflow(Weapon(WeaponModId.Ambush, 1), (WeaponModId)(-12345)));
        }

        // ================= display and predicate agree =================

        [TestMethod]
        public void DisplayCapped_AgreesWithWouldOverflow_ForEveryRegistryRow()
        {
            var fractions = new[] { 0.1, 0.3, 0.5, 0.6, 0.8, 0.81, 1.0, 1.4 };
            var compared = 0;
            var cappedSeen = 0;
            var uncappedSeen = 0;

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                foreach (var fraction in fractions)
                {
                    for (var gems = 1; gems <= ParagonModRegistry.MaxGems; gems++)
                    {
                        var weapon = Weapon(definition.Id, gems);

                        SetRecordFraction(weapon, definition, fraction);

                        // a record the row cannot show (rounded away on an integer row, muted) is not listed: nothing to compare
                        if (WeaponModTinkerSet.ReadSpecials(weapon).All(s => s.Definition.Id != definition.Id))
                            continue;

                        var line = WeaponModParagonDisplay.ModLines(weapon, WeaponModValue.MagnitudeScale()).FirstOrDefault(l => l.Contains("(rolled"));
                        Assert.IsNotNull(line, $"{definition.Id} f={fraction} g={gems}: no combined line");

                        var overflow = WeaponModParagon.WouldOverflow(weapon, definition.Id, 0);

                        Assert.AreEqual(overflow, line.Contains(", capped"), $"{definition.Id} f={fraction} g={gems}: {line}");

                        compared++;

                        if (overflow)
                            cappedSeen++;
                        else
                            uncappedSeen++;
                    }
                }
            }

            Assert.IsTrue(compared > 100, $"the grid compared {compared} cases");
            Assert.IsTrue(cappedSeen > 0 && uncappedSeen > 0, "the grid covers both outcomes");
        }

        // ================= the prompt decision (pure) =================

        [TestMethod]
        public void Decision_OverflowPrompts_NoOverflowApplies_ConfirmedApplies()
        {
            Assert.AreEqual(ParagonModManager.GemFlow.Prompt, ParagonModManager.ResolveGemFlow(overflows: true, confirmed: false));
            Assert.AreEqual(ParagonModManager.GemFlow.Apply, ParagonModManager.ResolveGemFlow(overflows: false, confirmed: false));
            Assert.AreEqual(ParagonModManager.GemFlow.Apply, ParagonModManager.ResolveGemFlow(overflows: true, confirmed: true), "the confirmed path never prompts again");
            Assert.AreEqual(ParagonModManager.GemFlow.Apply, ParagonModManager.ResolveGemFlow(overflows: false, confirmed: true));
        }

        [TestMethod]
        public void GemWouldOverflow_ReadsTheGemsModAndTheWeapon()
        {
            var definition = FirstOfTier(WeaponModTier.B);
            var weapon = Weapon(definition.Id, 0);

            var gem = Weapon(definition.Id, 0);
            gem.SetProperty(PropertyInt.ParagonModGemModifier, (int)definition.Id);

            SetRecordFraction(weapon, definition, 0.81);
            Assert.IsTrue(ParagonModManager.GemWouldOverflow(gem, weapon));

            SetRecordFraction(weapon, definition, 0.2);
            Assert.IsFalse(ParagonModManager.GemWouldOverflow(gem, weapon));

            Assert.IsFalse(ParagonModManager.GemWouldOverflow(Weapon(definition.Id, 0), weapon), "an item with no gem modifier never prompts");
        }

        [TestMethod]
        public void PromptText_IsVerbatim()
        {
            Assert.AreEqual("This Paragon Amber would push Ambush past a perfect roll, so part of it would be wasted. Apply it anyway?",
                ParagonModManager.OverflowConfirmationText("Ambush"));
        }

        // ================= wiring =================

        [TestMethod]
        public void Wiring_PromptRunsAfterVerifyAndBeforeAnimationAndConsume()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/WeaponMods/ParagonModManager.cs");
            var use = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(source,
                "public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)"));

            var verify = use.IndexOf("VerifyUseRequirements(player, source, target, out var firstAction)", System.StringComparison.Ordinal);
            var decide = use.IndexOf("ResolveGemFlow(firstAction == ParagonModAction.ApplyGem && GemWouldOverflow(source, target), confirmed)", System.StringComparison.Ordinal);
            var enqueue = use.IndexOf("new Confirmation_ParagonOverflow(player.Guid, source.Guid, target.Guid)", System.StringComparison.Ordinal);
            var busy = use.IndexOf("player.IsBusy = true;", System.StringComparison.Ordinal);
            var clap = use.IndexOf("MotionCommand.ClapHands", System.StringComparison.Ordinal);

            Assert.IsTrue(verify >= 0 && decide > verify && enqueue > decide, "refusals, then the decision, then the prompt");
            Assert.IsTrue(busy > enqueue && clap > enqueue, "no busy flag and no animation before the prompt is raised");
            Assert.AreEqual(1, JewelryAspectTestKit.Count(use, "ConfirmationManager.EnqueueSend"), "exactly one prompt site");
        }

        [TestMethod]
        public void Wiring_ConfirmationYes_ReentersTheManagerConfirmed()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/WeaponMods/ParagonModManager.cs");
            var confirmed = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(source,
                "internal static void OnOverflowConfirmed(Player player, ObjectGuid sourceGuid, ObjectGuid targetGuid)"));

            StringAssert.Contains(confirmed, "UseObjectOnTarget(player, source, target, true);");
        }

        [TestMethod]
        public void Confirmation_StoresGuids_AndAnOfflinePlayerDoesNothing()
        {
            // The Yes path needs a live Player and session (FindObject, chat, the manager), which this harness cannot
            // build; what is testable is the construction and that an unresolvable player returns before any work.
            var player = new ObjectGuid(0x50000001);
            var source = new ObjectGuid(0x80000001);
            var target = new ObjectGuid(0x80000002);

            var confirmation = new Confirmation_ParagonOverflow(player, source, target);

            Assert.AreEqual(source, confirmation.SourceGuid);
            Assert.AreEqual(target, confirmation.TargetGuid);

            confirmation.ProcessConfirmation(false);
            confirmation.ProcessConfirmation(true);
            confirmation.ProcessConfirmation(false, timeout: true);
        }

        [TestMethod]
        public void IsOverCap_HardCodedValues()
        {
            Assert.IsFalse(WeaponModParagon.IsOverCap(0.0, 5));
            Assert.IsFalse(WeaponModParagon.IsOverCap(0.8, 1));
            Assert.IsFalse(WeaponModParagon.IsOverCap(0.6, 2));
            Assert.IsTrue(WeaponModParagon.IsOverCap(0.81, 1));
            Assert.IsTrue(WeaponModParagon.IsOverCap(0.0, 6));
            Assert.IsFalse(WeaponModParagon.IsOverCap(1.0, 0));
            Assert.IsFalse(WeaponModParagon.IsOverCap(1.0000000005, 0), "inside the 1e-9 tolerance");
            Assert.IsTrue(WeaponModParagon.IsOverCap(1.000001, 0));
        }
    }
}