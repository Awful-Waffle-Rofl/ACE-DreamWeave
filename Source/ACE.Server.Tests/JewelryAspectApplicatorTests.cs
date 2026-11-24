using System;
using System.Collections.Generic;
using System.Linq;

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
    /// The Aspect Gem (Task D): the gem test, the target-state refusals, the roll and its write, the exact texts, the worn
    /// and out-of-pack refusals, the appraisal lines, and source pins on the wiring. Items are in-memory weenies; the
    /// PropertyManager keys this class reads are seeded per test and restored in TestCleanup.
    /// </summary>
    [TestClass]
    public class JewelryAspectApplicatorTests
    {
        private static uint nextGuid = 0x7ED10000;
        private static uint nextWcid = 1008200;

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

        internal static WorldObject Jewelry(string name, EquipMask locations, int attack = 0, int defense = 0, int utility = 0, ItemType type = ItemType.Jewelry)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)type },
                    { PropertyInt.ValidLocations, (int)locations },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            if (attack != 0 || defense != 0 || utility != 0)
            {
                weenie.PropertiesInt[PropertyInt.JewelryAspectAttack] = attack;
                weenie.PropertiesInt[PropertyInt.JewelryAspectDefense] = defense;
                weenie.PropertiesInt[PropertyInt.JewelryAspectUtility] = utility;
            }

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Ring(int attack = 0, int defense = 0, int utility = 0) => Jewelry("Test Ring", EquipMask.FingerWearLeft | EquipMask.FingerWearRight, attack, defense, utility);

        private static Gem AspectGem(bool withProperty = true)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Aspect Gem" } },
            };

            if (withProperty)
                weenie.PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.JewelryAspectGem, true } };

            return new Gem(weenie, new ObjectGuid(nextGuid++));
        }

        private static Func<int, int> Script(params int[] draws)
        {
            var i = 0;
            return n => draws[i++ % draws.Length];
        }

        // ---------------- the gem ----------------

        [TestMethod]
        public void IsAspectGem_KeysOnTheGemCarryingTheProperty()
        {
            Assert.IsTrue(JewelryAspectApplicator.IsAspectGem(AspectGem()));
            Assert.IsFalse(JewelryAspectApplicator.IsAspectGem(AspectGem(withProperty: false)), "a gem without the property is not an applicator");

            var ring = Ring();
            ring.SetProperty(PropertyBool.JewelryAspectGem, true);
            Assert.IsFalse(JewelryAspectApplicator.IsAspectGem(ring), "a non-Gem carrying the property is never a source");
        }

        // ---------------- refusals (state) ----------------

        [TestMethod]
        public void Verify_UnaspectedRingNecklaceBracelet_Allowed()
        {
            Assert.IsNull(JewelryAspectApplicator.Verify(Ring()));
            Assert.IsNull(JewelryAspectApplicator.Verify(Jewelry("Necklace", EquipMask.NeckWear)));
            Assert.IsNull(JewelryAspectApplicator.Verify(Jewelry("Bracelet", EquipMask.WristWearLeft | EquipMask.WristWearRight)));
        }

        [TestMethod]
        public void Verify_NonAspectablePieces_RefusedWithTheExactLine()
        {
            const string line = "Aspects can only be given to rings, bracelets and necklaces.";

            Assert.AreEqual(line, JewelryAspectApplicator.Verify(Jewelry("Trinket", EquipMask.TrinketOne)), "trinket");
            Assert.AreEqual(line, JewelryAspectApplicator.Verify(Jewelry("Crown", EquipMask.HeadWear)), "crown");
            Assert.AreEqual(line, JewelryAspectApplicator.Verify(Jewelry("Cloak", EquipMask.Cloak)), "cloak");
            Assert.AreEqual(line, JewelryAspectApplicator.Verify(Jewelry("Sigil", EquipMask.SigilOne)), "sigil");
            Assert.AreEqual(line, JewelryAspectApplicator.Verify(Jewelry("Sword", EquipMask.MeleeWeapon, type: ItemType.MeleeWeapon)), "not jewelry at all");
            Assert.AreEqual(JewelryAspectApplicator.RefusalNotJewelry, line);
        }

        [TestMethod]
        public void Verify_AlreadyAspected_RefusedWithTheItemName()
        {
            Assert.AreEqual("The Test Ring already has its Aspect.", JewelryAspectApplicator.Verify(Ring(3, 3, 4)));
            Assert.AreEqual("The Test Ring already has its Aspect.", JewelryAspectApplicator.Verify(Ring(0, 1, 0)), "a single aspect is already aspected");
            Assert.AreEqual("The Test Ring already has its Aspect.", JewelryAspectApplicator.Verify(Ring(10, 0, 0)));
        }

        [TestMethod]
        public void Verify_Disabled_WinsOverEveryOtherRefusal()
        {
            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);

            Assert.AreEqual("Jewelry Aspects are disabled right now.", JewelryAspectApplicator.Verify(Ring()));
            Assert.AreEqual("Jewelry Aspects are disabled right now.", JewelryAspectApplicator.Verify(Jewelry("Trinket", EquipMask.TrinketOne)));
            Assert.AreEqual("Jewelry Aspects are disabled right now.", JewelryAspectApplicator.Verify(Ring(3, 3, 4)));
        }

        // ---------------- target location (worn / pack) ----------------

        [TestMethod]
        public void TargetInventoryRefusal_WornPiece_IsRefused()
        {
            var player = JewelryAspectTestKit.BarePlayer(out var equipped);
            var ring = Ring();

            // worn by its wielded location
            ring.CurrentWieldedLocation = EquipMask.FingerWearLeft;
            Assert.AreEqual("Take the Test Ring off first.", GreenJadeManager.TargetInventoryRefusal(player, ring));

            // worn by membership in the equipped set alone
            ring.CurrentWieldedLocation = null;
            JewelryAspectTestKit.Wear(equipped, ring);
            Assert.AreEqual("Take the Test Ring off first.", GreenJadeManager.TargetInventoryRefusal(player, ring));
        }

        [TestMethod]
        public void TargetInventoryRefusal_PackedPiece_Passes_AndOutOfPackIsRefused()
        {
            var player = JewelryAspectTestKit.BarePlayer(out _);
            var ring = Ring();
            var seam = GreenJadeManager.InMyInventory;

            try
            {
                GreenJadeManager.InMyInventory = (p, t) => true;
                Assert.IsNull(GreenJadeManager.TargetInventoryRefusal(player, ring), "in the pack and not worn");

                GreenJadeManager.InMyInventory = (p, t) => false;
                Assert.AreEqual("You must remove the Test Ring and place it in your inventory first.", GreenJadeManager.TargetInventoryRefusal(player, ring), "on the ground or in a chest");
            }
            finally
            {
                GreenJadeManager.InMyInventory = seam;
            }
        }

        // ---------------- the roll ----------------

        [TestMethod]
        public void ApplyTo_WritesExactlyOneAspect_OfTheDrawnType()
        {
            foreach (var (draw, expected) in new[] { (0, new AspectCounts(1, 0, 0)), (1, new AspectCounts(0, 1, 0)), (2, new AspectCounts(0, 0, 1)) })
            {
                var ring = Ring();

                var counts = JewelryAspectApplicator.ApplyTo(ring, Script(draw));

                Assert.AreEqual(1, counts.Total, "one aspect, not ten");
                Assert.AreEqual(expected.Attack, ring.GetProperty(PropertyInt.JewelryAspectAttack));
                Assert.AreEqual(expected.Defense, ring.GetProperty(PropertyInt.JewelryAspectDefense));
                Assert.AreEqual(expected.Utility, ring.GetProperty(PropertyInt.JewelryAspectUtility));
                Assert.IsTrue(JewelryAspectMath.IsAspected(ring));
                Assert.IsNotNull(JewelryAspectApplicator.Verify(ring), "once aspected the piece refuses a second gem");
            }
        }

        [TestMethod]
        public void ApplyTo_RandomDraws_ReachAllThreeTypes()
        {
            var random = new Random(20261109);
            var seen = new HashSet<int>();

            for (var i = 0; i < 200; i++)
            {
                var ring = Ring();
                var counts = JewelryAspectApplicator.ApplyTo(ring, n => random.Next(n));
                Assert.AreEqual(1, counts.Total);
                seen.Add(counts.Attack == 1 ? 0 : counts.Defense == 1 ? 1 : 2);
            }

            Assert.AreEqual(3, seen.Count);
        }

        [TestMethod]
        public void ApplyTo_NeverTouchesTheTinkerCounterOrLog()
        {
            var ring = Ring();

            JewelryAspectApplicator.ApplyTo(ring, new Random(1).Next);

            Assert.IsNull(ring.GetProperty(PropertyInt.NumTimesTinkered), "NumTimesTinkered stays unset");
            Assert.IsNull(ring.GetProperty(PropertyString.TinkerLog), "the tinker log stays unset");
        }

        [TestMethod]
        public void DefaultNext_IsHalfOpen_AndReachesEveryValue()
        {
            // ThreadSafeRandom.Next's max is INCLUSIVE: a draw over n must still land in [0, n)
            foreach (var n in new[] { 2, 3, 10 })
            {
                var seen = new HashSet<int>();

                for (var i = 0; i < 4000; i++)
                {
                    var draw = GreenJadeManager.DefaultNext(n);
                    Assert.IsTrue(draw >= 0 && draw < n, $"draw {draw} outside [0,{n})");
                    seen.Add(draw);
                }

                Assert.AreEqual(n, seen.Count, $"every value of [0,{n}) should appear");
            }
        }

        // ---------------- exact texts ----------------

        [TestMethod]
        public void Text_ConfirmationAndSuccess_AreByteForByte()
        {
            var ring = Ring();

            Assert.AreEqual("Give the Test Ring a permanent Aspect? Its first aspect will be rolled at random.", JewelryAspectApplicator.GetConfirmationText(ring));
            Assert.AreEqual("The Test Ring takes on its Aspect: Attack 0, Defense 1, Utility 0.", JewelryAspectApplicator.GetSuccessText(ring, new AspectCounts(0, 1, 0)));
            Assert.AreEqual("The Test Ring already has its Aspect.", JewelryAspectApplicator.RefusalHasAspect(ring));
        }

        // ---------------- appraisal ----------------

        [TestMethod]
        public void Display_AspectedPiece_ShowsCountsThenOneLinePerNonZeroLayer()
        {
            var lines = JewelryAspectDisplay.GetAppraisalLines(Ring(attack: 5, defense: 3, utility: 2)).ToList();

            CollectionAssert.AreEqual(new[]
            {
                "Aspects: Attack 5 / Defense 3 / Utility 2",
                "Fortitude: 3% less damage taken",
                "Penetration: +4% elemental damage, or 4% of elemental resistance ignored if that is greater",
                "Resolve: +4% chance on kill to remove a debuff",
            }, lines);
        }

        [TestMethod]
        public void Display_AmplifyZero_ShowsTheOldPenetrationText()
        {
            try
            {
                PropertyManager.ModifyDouble("jewelry_aspects_penetration_amplify", 0.0);

                var lines = JewelryAspectDisplay.GetAppraisalLines(Ring(attack: 5, defense: 3, utility: 2)).ToList();

                CollectionAssert.Contains(lines, "Penetration: 4% of elemental resistance ignored");

                PropertyManager.ModifyDouble("jewelry_aspects_penetration_amplify", 0.5);

                lines = JewelryAspectDisplay.GetAppraisalLines(Ring(attack: 5, defense: 3, utility: 2)).ToList();

                CollectionAssert.Contains(lines, "Penetration: +2% elemental damage, or 4% of elemental resistance ignored if that is greater");
            }
            finally
            {
                PropertyManager.ModifyDouble("jewelry_aspects_penetration_amplify", DefaultPropertyManager.DefaultDoubleProperties["jewelry_aspects_penetration_amplify"].Item);
            }
        }

        [TestMethod]
        public void Display_PartiallyFilledPiece_AppendsNOfTen_UntilTen()
        {
            CollectionAssert.AreEqual(new[] { "Aspects: Attack 0 / Defense 1 / Utility 0 (1 of 10)", "Fortitude: 1% less damage taken" }, JewelryAspectDisplay.GetAppraisalLines(Ring(0, 1, 0)).ToList());
            Assert.AreEqual("Aspects: Attack 2 / Defense 3 / Utility 4 (9 of 10)", JewelryAspectDisplay.GetAppraisalLines(Ring(2, 3, 4)).First());
            Assert.AreEqual("Aspects: Attack 3 / Defense 3 / Utility 4", JewelryAspectDisplay.GetAppraisalLines(Ring(3, 3, 4)).First(), "no suffix at ten");
        }

        [TestMethod]
        public void Display_ZeroCountLayer_HasNoLine_AndPerPieceShareIsNotCapped()
        {
            // 10 Defense is 10 x 1% = 10%; a 50% cap applies to the worn total, never to one piece's share
            PropertyManager.ModifyDouble("jewelry_aspects_fortitude_per_slot", 0.2);
            PropertyManager.ModifyDouble("jewelry_aspects_fortitude_cap", 0.5);

            var lines = JewelryAspectDisplay.GetAppraisalLines(Ring(attack: 0, defense: 10, utility: 0)).ToList();

            CollectionAssert.AreEqual(new[] { "Aspects: Attack 0 / Defense 10 / Utility 0", "Fortitude: 200% less damage taken" }, lines);
        }

        [TestMethod]
        public void Display_RoundsToOneDecimal_AwayFromZero()
        {
            Assert.AreEqual("0.1", JewelryAspectDisplay.Share(1, 0.00125), "0.125 rounds down to 0.1");
            Assert.AreEqual("0.1", JewelryAspectDisplay.Share(1, 0.0005), "0.05 midpoint rounds away from zero");
            Assert.AreEqual("0.5", JewelryAspectDisplay.Share(1, 0.0049), "0.49 rounds up to 0.5");
            Assert.AreEqual("0.4", JewelryAspectDisplay.Share(1, 0.0041), "0.41 rounds down to 0.4");
            Assert.AreEqual("8", JewelryAspectDisplay.Share(8, 0.01));
            Assert.AreEqual("14.4", JewelryAspectDisplay.Share(8, 0.018));
        }

        [TestMethod]
        public void Display_ReadsSettingsLive_AndHidesLayersWhenDisabled()
        {
            var ring = Ring(attack: 0, defense: 10, utility: 0);

            PropertyManager.ModifyDouble("jewelry_aspects_fortitude_per_slot", 0.02);
            Assert.AreEqual("Fortitude: 20% less damage taken", JewelryAspectDisplay.GetAppraisalLines(ring).Last());

            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);
            CollectionAssert.AreEqual(new[] { "Aspects: Attack 0 / Defense 10 / Utility 0" }, JewelryAspectDisplay.GetAppraisalLines(ring).ToList());
        }

        [TestMethod]
        public void Display_UnaspectedOrMalformedOrNull_NoLines()
        {
            Assert.AreEqual(0, JewelryAspectDisplay.GetAppraisalLines(Ring()).Count());
            Assert.AreEqual(0, JewelryAspectDisplay.GetAppraisalLines(Ring(attack: 4, defense: 4, utility: 4)).Count(), "over ten is invalid");
            Assert.AreEqual(0, JewelryAspectDisplay.GetAppraisalLines(null).Count());
        }

        // ---------------- wiring pins (source text) ----------------

        [TestMethod]
        public void Wiring_GemDispatchIsKeyedOnTheProperty_AndAppraisalIsHooked()
        {
            var gem = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Gem.cs");
            var body = PooledLootSourceText.MethodBody(gem, "public override void HandleActionUseOnTarget(Player player, WorldObject target)");
            StringAssert.Contains(body, "JewelryAspectApplicator.IsAspectGem(this)");
            StringAssert.Contains(body, "JewelryAspectApplicator.UseObjectOnTarget(player, this, target);");

            var info = PooledLootSourceText.Read("Source/ACE.Server/Network/Structure/AppraiseInfo.cs");
            StringAssert.Contains(info, "JewelryAspectDisplay.GetAppraisalLines(wo)");

            var confirmation = PooledLootSourceText.Read("Source/ACE.Server/Entity/Confirmation.cs");
            StringAssert.Contains(confirmation, "class Confirmation_JewelryAspect : Confirmation");
            StringAssert.Contains(confirmation, "JewelryAspectApplicator.UseObjectOnTarget(player, source, target, true);");
        }

        [TestMethod]
        public void Wiring_ApplicatorHonoursTheMuleGate_Reverifies_AndNeverBuildsAnUpdateMessage()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/Entity/JewelryAspectApplicator.cs");
            var flat = JewelryAspectTestKit.Flat(text);

            var use = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(text, "public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)"));

            // mule gate before anything else, and again after the clap
            Assert.IsTrue(use.IndexOf("player.MuleBlocked(MuleAction.Craft)", StringComparison.Ordinal) < use.IndexOf("player.IsBusy", StringComparison.Ordinal), "mule gate comes first");
            Assert.AreEqual(2, JewelryAspectTestKit.Count(use, "player.MuleBlocked(MuleAction.Craft)"), "before the confirmation and again after the clap");

            // re-verify happens inside the post-clap action, before Apply
            Assert.IsTrue(use.IndexOf("VerifyUseRequirements(player, source, target);", use.IndexOf("SendMotionAsCommands", StringComparison.Ordinal), StringComparison.Ordinal) < use.IndexOf("Apply(player, source, target);", StringComparison.Ordinal));

            // never constructs the update message itself; reuses the shared helper
            Assert.AreEqual(0, JewelryAspectTestKit.Count(flat, "new GameMessageUpdateObject"));
            StringAssert.Contains(flat, "PrismaticDriftStone.UpdateObj(player, target);");

            // never reads or writes the tinker counter or log
            Assert.AreEqual(0, JewelryAspectTestKit.Count(flat, "NumTimesTinkered"));
            Assert.AreEqual(0, JewelryAspectTestKit.Count(flat, "TinkerLog"));

            // every exit ends the use
            // the skill gate runs before the confirmation, with its exit ending the use
            var skillAt = use.IndexOf("ImbueGemRisk.SkillRefusal(ImbueGemRisk.AspectSkill, player.GetCreatureSkill(ImbueGemRisk.AspectSkill).AdvancementClass, source.Name);", StringComparison.Ordinal);
            var refuseAt = use.IndexOf("if (skillRefusal != null) { player.SendTransientError(skillRefusal); player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements); return; }", StringComparison.Ordinal);
            var confirmAt = use.IndexOf("if (!confirmed)", StringComparison.Ordinal);
            Assert.IsTrue(skillAt >= 0 && refuseAt > skillAt && confirmAt > refuseAt, "untrained is refused before the confirmation");
            StringAssert.Contains(flat, "GreenJadeManager.InTradeWindow(player, source, target)");
            StringAssert.Contains(flat, "return WeenieError.TradeItemBeingTraded;");
        }

        [TestMethod]
        public void Wiring_ApplyPaysBeforeItWrites()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/Entity/JewelryAspectApplicator.cs");
            var apply = JewelryAspectTestKit.Flat(PooledLootSourceText.MethodBody(text, "private static void Apply(Player player, WorldObject source, WorldObject target)"));

            var consume = apply.IndexOf("TryConsumeFromInventoryWithNetworking(source, 1)", StringComparison.Ordinal);
            var write = apply.IndexOf("ApplyTo(target", StringComparison.Ordinal);

            Assert.IsTrue(consume >= 0 && write > consume, "the gem is taken before the piece is rewritten, so a failed take leaves no free Aspect");

            // the failed roll destroys the target after the gem is spent and before any write
            const string gate = "if (ImbueGemRisk.Resolve(true, ImbueGemRisk.Roll(ImbueGemRisk.ChanceFor(player))) == ImbueGemOutcome.Destroy)";
            Assert.AreEqual(1, JewelryAspectTestKit.Count(apply, gate), "exactly one roll gate, as written");
            Assert.IsTrue(apply.IndexOf(gate, StringComparison.Ordinal) > consume, "the gem is spent before the roll");
            Assert.AreEqual(3, JewelryAspectTestKit.Count(apply, "SendUseDoneEvent"), "consume failure, destroyed piece and success each end the use");

            var destroy = apply.IndexOf("ImbueGemRisk.DestroyTarget(player, target", StringComparison.Ordinal);
            Assert.IsTrue(destroy > consume && destroy < write, "a failure spends the gem, destroys the piece, and never writes an Aspect");
        }
    }
}
