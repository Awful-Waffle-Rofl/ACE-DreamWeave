using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.KnotLoot;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CLOAK MANTLE gems: the Mantle and Amplifier applicator (Verify / ApplyTo) and the appraisal lines.
    /// Items are in-memory weenies (nothing persisted). The two display settings are seeded per test and restored.
    /// </summary>
    [TestClass]
    public class CloakMantleApplicatorTests
    {
        private static uint nextGuid = 0x7EB00000;
        private static uint nextWcid = 997700;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedTunables()
        {
            PropertyManager.ModifyBool("cloak_rear_guard_enabled", true);
            PropertyManager.ModifyDouble("cloak_rear_guard_armor_per_level", 20.0);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, 100);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, 200);
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            PropertyManager.ModifyBool("cloak_rear_guard_enabled", DefaultPropertyManager.DefaultBooleanProperties["cloak_rear_guard_enabled"].Item);
            PropertyManager.ModifyDouble("cloak_rear_guard_armor_per_level", DefaultPropertyManager.DefaultDoubleProperties["cloak_rear_guard_armor_per_level"].Item);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, DefaultPropertyManager.DefaultLongProperties[CloakMantleApplicator.RollMinKey].Item);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, DefaultPropertyManager.DefaultLongProperties[CloakMantleApplicator.RollMaxKey].Item);
        }

        private static Gem MantleGem()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.CloakRearGuard, true } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Mantle" } },
            };

            return new Gem(weenie, new ObjectGuid(nextGuid++));
        }

        private static Gem AmplifierGem(double amplifier)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.CloakProcAmplifier, amplifier } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Amplifier" } },
            };

            return new Gem(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject PlainCloak()
        {
            return UpgradedCloakTestItems.Item(cloak: true);
        }

        private static WorldObject ProcSpellCloak()
        {
            var cloak = PlainCloak();
            cloak.ProcSpell = (uint)SpellId.CloakAllSkill;
            return cloak;
        }

        private static WorldObject DamageProcCloak()
        {
            var cloak = PlainCloak();
            cloak.SetProperty(PropertyInt.CloakWeaveProc, 2);
            return cloak;
        }

        private static WorldObject LeveledCloak(int itemLevel)
        {
            var cloak = PlainCloak();
            cloak.SetProperty(PropertyInt64.ItemBaseXp, 100);
            cloak.SetProperty(PropertyInt.ItemMaxLevel, 10);
            cloak.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.Fixed);
            cloak.SetProperty(PropertyInt64.ItemTotalXp, itemLevel * 100L);

            Assert.AreEqual(itemLevel, cloak.ItemLevel ?? 0, "control: the fixture must really carry that item level");

            return cloak;
        }

        [TestMethod]
        public void IsMantle_IsAmplifier_KeyOnGemCarryingTheProperty()
        {
            Assert.IsTrue(CloakMantleApplicator.IsMantle(MantleGem()));
            Assert.IsFalse(CloakMantleApplicator.IsAmplifier(MantleGem()));
            Assert.IsTrue(CloakMantleApplicator.IsAmplifier(AmplifierGem(0.25)));
            Assert.IsFalse(CloakMantleApplicator.IsMantle(AmplifierGem(0.25)));
            Assert.IsFalse(CloakMantleApplicator.IsAmplifier(AmplifierGem(0.0)), "a zero amplifier is not an applicator");
        }

        [TestMethod]
        public void IsMantle_IsAmplifier_FalseForCloakCarryingTheProperty()
        {
            var cloak = PlainCloak();
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.25);

            Assert.IsFalse(CloakMantleApplicator.IsMantle(cloak));
            Assert.IsFalse(CloakMantleApplicator.IsAmplifier(cloak));
        }

        [TestMethod]
        public void Verify_NotACloak_Refused()
        {
            var amulet = UpgradedCloakTestItems.Item(cloak: false);

            Assert.AreEqual("Only a cloak can take that.", CloakMantleApplicator.Verify(MantleGem(), amulet));
            Assert.AreEqual("Only a cloak can take that.", CloakMantleApplicator.Verify(AmplifierGem(0.25), amulet));
        }

        [TestMethod]
        public void Verify_MantleAlreadyPresent_Refused()
        {
            var cloak = PlainCloak();
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);

            Assert.AreEqual("That cloak already carries a Mantle.", CloakMantleApplicator.Verify(MantleGem(), cloak));
        }

        [TestMethod]
        public void Verify_MantleOnNonLevellingCloak_Refused()
        {
            var plain = PlainCloak();
            Assert.IsFalse(plain.HasItemLevel, "harness: the plain cloak cannot level");

            Assert.AreEqual("That cloak cannot level, so a Mantle would never guard it.", CloakMantleApplicator.Verify(MantleGem(), plain));

            // control: the same gem is allowed on a levelling cloak
            Assert.IsNull(CloakMantleApplicator.Verify(MantleGem(), LeveledCloak(1)));

            // an already-Mantled non-levelling cloak reports the Mantle first (refusal order)
            plain.SetProperty(PropertyBool.CloakRearGuard, true);
            Assert.AreEqual(CloakMantleApplicator.RefusalHasMantle, CloakMantleApplicator.Verify(MantleGem(), plain));

            // the Amplifier does not need a levelling cloak
            Assert.IsNull(CloakMantleApplicator.Verify(AmplifierGem(0.25), ProcSpellCloak()));
        }

        [TestMethod]
        public void Verify_AmplifierOnCloakWithNoProc_Refused()
        {
            Assert.AreEqual("That cloak has no proc to amplify.", CloakMantleApplicator.Verify(AmplifierGem(0.25), PlainCloak()));
        }

        [TestMethod]
        public void Verify_AmplifierOnCloakWithAnyAmplifier_Refused()
        {
            var cloak = ProcSpellCloak();
            cloak.SetProperty(PropertyString.Name, "Test Cloak");
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.25);

            const string text = "The Test Cloak already has a Weave Amplifier.";
            Assert.AreEqual(text, CloakMantleApplicator.Verify(AmplifierGem(0.25), cloak), "equal");
            Assert.AreEqual(text, CloakMantleApplicator.Verify(AmplifierGem(0.10), cloak), "weaker");
            Assert.AreEqual(text, CloakMantleApplicator.Verify(AmplifierGem(0.90), cloak), "stronger");
        }

        [TestMethod]
        public void Verify_SecondMantle_Refused_ButMantleAndAmplifierCoexist()
        {
            var cloak = LeveledCloak(1);
            cloak.SetProperty(PropertyString.Name, "Test Cloak");
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);
            Assert.AreEqual(CloakMantleApplicator.RefusalHasMantle, CloakMantleApplicator.Verify(MantleGem(), cloak));

            // a Mantled cloak with a proc accepts an Amplifier
            cloak.ProcSpell = (uint)SpellId.CloakAllSkill;
            Assert.IsNull(CloakMantleApplicator.Verify(AmplifierGem(0.25), cloak));

            // and an amplified, unmantled cloak accepts a Mantle
            var other = LeveledCloak(1);
            other.ProcSpell = (uint)SpellId.CloakAllSkill;
            other.SetProperty(PropertyFloat.CloakProcAmplifier, 0.25);
            Assert.IsNull(CloakMantleApplicator.Verify(MantleGem(), other));
        }

        [TestMethod]
        public void Verify_NoProcWinsOverWeakerAmplifier()
        {
            // order of refusals: no proc is checked before the equal-or-stronger check
            var cloak = PlainCloak();
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.50);

            Assert.AreEqual("That cloak has no proc to amplify.", CloakMantleApplicator.Verify(AmplifierGem(0.25), cloak));
        }

        [TestMethod]
        public void Mantle_OnLevellingCloak_Allowed_SetsBool()
        {
            var cloak = LeveledCloak(1);
            var gem = MantleGem();

            Assert.IsNull(CloakMantleApplicator.Verify(gem, cloak));

            CloakMantleApplicator.ApplyTo(gem, cloak, 0);

            Assert.AreEqual(true, cloak.GetProperty(PropertyBool.CloakRearGuard));
            Assert.IsNull(gem.GetProperty(PropertyFloat.CloakProcAmplifier), "ApplyTo stamps the target only");
        }

        [TestMethod]
        public void Amplifier_OnProcCloak_Allowed_AnyProcKind()
        {
            Assert.IsNull(CloakMantleApplicator.Verify(AmplifierGem(0.25), ProcSpellCloak()), "spell proc");
            Assert.IsNull(CloakMantleApplicator.Verify(AmplifierGem(0.25), DamageProcCloak()), "damage proc");
        }

        [TestMethod]
        public void Amplifier_Higher_DoesNotReplace()
        {
            var cloak = ProcSpellCloak();
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.10);

            Assert.IsNotNull(CloakMantleApplicator.Verify(AmplifierGem(0.25), cloak));
        }

        [TestMethod]
        public void Amplifier_Equal_Refused()
        {
            var cloak = ProcSpellCloak();
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.25);

            Assert.IsNotNull(CloakMantleApplicator.Verify(AmplifierGem(0.25), cloak));
        }

        [TestMethod]
        public void Text_ConfirmationAndSuccess_Verbatim()
        {
            var cloak = PlainCloak();
            cloak.SetProperty(PropertyString.Name, "Test Cloak");

            Assert.AreEqual("Apply the Mantle to Test Cloak? This cannot be undone.", CloakMantleApplicator.GetConfirmationText(MantleGem(), cloak));
            Assert.AreEqual("Amplify the proc of Test Cloak by 100% to 200%? This cannot be undone.", CloakMantleApplicator.GetConfirmationText(AmplifierGem(1.0), cloak));
            Assert.AreEqual("The Mantle settles into your Test Cloak. It now guards your back, growing stronger as the cloak levels.", CloakMantleApplicator.GetSuccessText(MantleGem(), cloak, 0));
            Assert.AreEqual("Your Test Cloak's proc is now 137% stronger.", CloakMantleApplicator.GetSuccessText(AmplifierGem(1.0), cloak, 137));
        }

        [TestMethod]
        public void Display_MantleLevel10_Shows200()
        {
            var cloak = LeveledCloak(10);
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);

            var lines = CloakMantleDisplay.GetAppraisalLines(cloak).ToList();

            CollectionAssert.AreEqual(new[] { "Rear Guard: 200 armor vs attacks from behind (cloak level 10)" }, lines);
        }

        [TestMethod]
        public void Display_Amplifier_ShowsWholePercent()
        {
            var cloak = ProcSpellCloak();
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.25);

            CollectionAssert.AreEqual(new[] { "Proc Amplifier: +25%" }, CloakMantleDisplay.GetAppraisalLines(cloak).ToList());
        }

        [TestMethod]
        public void Display_Both_MantleThenAmplifier()
        {
            var cloak = LeveledCloak(3);
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.5);

            CollectionAssert.AreEqual(
                new[] { "Rear Guard: 60 armor vs attacks from behind (cloak level 3)", "Proc Amplifier: +50%" },
                CloakMantleDisplay.GetAppraisalLines(cloak).ToList());
        }

        [TestMethod]
        public void Display_ReadsSettingLive()
        {
            var cloak = LeveledCloak(10);
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);

            PropertyManager.ModifyDouble("cloak_rear_guard_armor_per_level", 25.0);

            Assert.AreEqual("Rear Guard: 250 armor vs attacks from behind (cloak level 10)", CloakMantleDisplay.GetAppraisalLines(cloak).Single());
        }

        [TestMethod]
        public void Display_NoProperties_NoLines()
        {
            Assert.AreEqual(0, CloakMantleDisplay.GetAppraisalLines(PlainCloak()).Count());
        }

        private const string ApplyHead = "private static void Apply(Player player, WorldObject source, WorldObject target)";
        private const string UseHead = "public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)";
        private const string RollGate = "if (ImbueGemRisk.Resolve(true, ImbueGemRisk.Roll(ImbueGemRisk.ChanceFor(player))) == ImbueGemOutcome.Destroy)";

        private static string CloakSource() => PooledLootSourceText.Read("Source/ACE.Server/Entity/CloakMantleApplicator.cs");

        [TestMethod]
        public void Wiring_Apply_PaysBeforeRoll_RollGatesDestroy_AndAppliesFromCapturedValues()
        {
            var apply = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(CloakSource(), ApplyHead));

            var captureKind = apply.IndexOf("var isMantle = IsMantle(source);", System.StringComparison.Ordinal);
            var consume = apply.IndexOf("if (!player.TryConsumeFromInventoryWithNetworking(source, 1))", System.StringComparison.Ordinal);
            var gate = apply.IndexOf(RollGate, System.StringComparison.Ordinal);
            var destroy = apply.IndexOf("ImbueGemRisk.DestroyTarget(player, target, failText);", System.StringComparison.Ordinal);
            var mantleText = apply.IndexOf("var mantleText = MantleSuccessText(target);", System.StringComparison.Ordinal);
            var destroyReturn = apply.IndexOf("return;", destroy, System.StringComparison.Ordinal);
            var amplifierRoll = apply.IndexOf("var amplifierPct = isAmplifier ? RollAmplifierPct() : 0;", System.StringComparison.Ordinal);
            var write = apply.IndexOf("ApplyValues(isMantle, amplifierPct / 100.0, target);", System.StringComparison.Ordinal);

            Assert.IsTrue(captureKind >= 0 && consume > captureKind, "the gem's kind is captured before it is consumed");
            Assert.IsTrue(gate > consume, "the gem is spent before the roll, so a failure still costs it");
            Assert.IsTrue(destroy > gate && destroyReturn > destroy && amplifierRoll > destroyReturn && write > amplifierRoll, "a failed roll destroys the cloak and returns before the strength is rolled or any write");
            Assert.IsTrue(mantleText >= 0 && mantleText < consume, "the Mantle text is captured before the gem is consumed");
            Assert.AreEqual(1, JewelryAspectTestKit.Count(apply, "RollAmplifierPct()"), "the strength is rolled exactly once");
            Assert.AreEqual(1, JewelryAspectTestKit.Count(apply, RollGate), "exactly one roll gate");
            Assert.AreEqual(0, JewelryAspectTestKit.Count(apply, "ApplyTo(source"), "never reads the consumed gem after the consume");
            Assert.AreEqual(3, JewelryAspectTestKit.Count(apply, "SendUseDoneEvent"), "consume failure, destroyed target and success each end the use");
        }

        [TestMethod]
        public void Wiring_Use_SkillGateRunsBeforeTheConfirmation()
        {
            var use = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(CloakSource(), UseHead));

            var skill = use.IndexOf("ImbueGemRisk.SkillRefusal(ImbueGemRisk.CloakGemSkill, player.GetCreatureSkill(ImbueGemRisk.CloakGemSkill).AdvancementClass, source.Name);", System.StringComparison.Ordinal);
            var refuse = use.IndexOf("if (skillRefusal != null)", System.StringComparison.Ordinal);
            var confirm = use.IndexOf("if (!confirmed)", System.StringComparison.Ordinal);

            Assert.IsTrue(skill >= 0 && refuse > skill && confirm > refuse, "untrained is refused before the confirmation");
            StringAssert.Contains(use, "if (skillRefusal != null) { player.SendTransientError(skillRefusal); player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements); return; }");
        }

        [TestMethod]
        public void Wiring_VerifyUseRequirements_TradeWindowAndWornCloakRefused()
        {
            var verify = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(CloakSource(), "private static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)"));

            var self = verify.IndexOf("if (source == target)", System.StringComparison.Ordinal);
            var trade = verify.IndexOf("if (GreenJadeManager.InTradeWindow(player, source, target)) return WeenieError.TradeItemBeingTraded;", System.StringComparison.Ordinal);
            var worn = verify.IndexOf("var inventoryRefusal = GreenJadeManager.TargetInventoryRefusal(player, target); if (inventoryRefusal != null)", System.StringComparison.Ordinal);

            Assert.IsTrue(self >= 0 && trade > self, "the trade-window re-check follows the null and self checks");
            Assert.IsTrue(worn > trade, "a worn or out-of-pack cloak is refused, so a failure never destroys equipped gear");
            Assert.AreEqual(0, JewelryAspectTestKit.Count(verify, "FindObject(target.Guid.Full"), "the old worn-allowed target reach is gone");
        }

        [TestMethod]
        public void Display_GemItself_NoLines()
        {
            Assert.AreEqual(0, CloakMantleDisplay.GetAppraisalLines(MantleGem()).Count(), "the gem carries the property but is not a cloak");
            Assert.AreEqual(0, CloakMantleDisplay.GetAppraisalLines(AmplifierGem(0.25)).Count());
        }
    }
}
