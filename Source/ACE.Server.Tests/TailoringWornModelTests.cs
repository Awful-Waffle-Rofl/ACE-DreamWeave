using System;
using System.IO;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers Tailoring.CopyWornModelProperties, which carries PropertyString.WornModelOverrides (9020) and
    /// PropertyString.ClothingPartExclusions (9019) from a tailoring source onto its target, and clears them on the target when
    /// the source has none.
    ///
    /// The helper is exercised directly, as TailoringObjDescRowTests does for CopyObjDescRows: SetCommonProperties needs
    /// PropertyManager and LootGenerationFactory, and UpdateCommonProps a live Player. That both of them actually CALL the helper
    /// is pinned by a source scan below, so deleting either call fails a test.
    /// </summary>
    [TestClass]
    public class TailoringWornModelTests
    {
        /// <summary>Asheron's Regalia (1006363), verbatim from Content/sql/weenies/1006363 Asheron's Regalia.sql (the 9020 row).</summary>
        private const string RegaliaOverrides = "0:0x01003F99,1:0x01003F9A,2:0x01003F9B,3:0x010001EC,4:0x010001EC,5:0x01003F9C,6:0x01003F9D,7:0x010001EC,8:0x010001EC,9:0x01003F9E,10:0x01003F9F,11:0x01003FA0,13:0x01003FA2,14:0x01003FA3";

        private const string SuitOverrides = "0:0x01001FD7,9:0x01001FE2,16:0x01001FE5,17:0x010001EC";

        [TestMethod]
        public void OverrideSource_OntoPlainTarget_CarriesBothProperties()
        {
            var source = TestCreatures.CreateQuestBearer("suit");
            source.SetProperty(PropertyString.WornModelOverrides, SuitOverrides);
            source.SetProperty(PropertyString.ClothingPartExclusions, "12,15");
            var target = TestCreatures.CreateQuestBearer("plain robe");

            Assert.IsNull(target.GetProperty(PropertyString.WornModelOverrides), "precondition");

            Tailoring.CopyWornModelProperties(source, target);

            Assert.AreEqual(SuitOverrides, target.GetProperty(PropertyString.WornModelOverrides));
            Assert.AreEqual("12,15", target.GetProperty(PropertyString.ClothingPartExclusions));
        }

        [TestMethod]
        public void PlainSource_OntoOverrideTarget_ClearsBoth_SoTheNormalLookShows()
        {
            var source = TestCreatures.CreateQuestBearer("plain robe");
            var target = TestCreatures.CreateQuestBearer("suit");
            target.SetProperty(PropertyString.WornModelOverrides, SuitOverrides);
            target.SetProperty(PropertyString.ClothingPartExclusions, "16");

            Tailoring.CopyWornModelProperties(source, target);

            Assert.IsNull(target.GetProperty(PropertyString.WornModelOverrides), "a source without the override must strip it, or the old body swap stays on top of the new look");
            Assert.IsNull(target.GetProperty(PropertyString.ClothingPartExclusions));
        }

        [TestMethod]
        public void OverrideSource_ReplacesADifferentOverride_OnTheTarget()
        {
            var source = TestCreatures.CreateQuestBearer("regalia");
            source.SetProperty(PropertyString.WornModelOverrides, RegaliaOverrides);
            var target = TestCreatures.CreateQuestBearer("suit");
            target.SetProperty(PropertyString.WornModelOverrides, SuitOverrides);
            target.SetProperty(PropertyString.ClothingPartExclusions, "16");

            Tailoring.CopyWornModelProperties(source, target);

            Assert.AreEqual(RegaliaOverrides, target.GetProperty(PropertyString.WornModelOverrides), "replacement, never a merge of two body swaps");
            Assert.IsNull(target.GetProperty(PropertyString.ClothingPartExclusions), "the Regalia carries no exclusions, so the target's are cleared");
        }

        [TestMethod]
        public void AsheronsRegalia_TailoredOntoAPlainPiece_KeepsItsExactOverrideString()
        {
            var regalia = TestCreatures.CreateQuestBearer("Asheron's Regalia");
            regalia.SetProperty(PropertyString.WornModelOverrides, RegaliaOverrides);
            var target = TestCreatures.CreateQuestBearer("plain robe");

            Tailoring.CopyWornModelProperties(regalia, target);

            Assert.AreEqual(RegaliaOverrides, target.GetProperty(PropertyString.WornModelOverrides));
            Assert.AreEqual(14, WornModelOverrides.Parse(target.GetProperty(PropertyString.WornModelOverrides)).Count, "every Regalia pair survives the copy");
            Assert.IsNull(target.GetProperty(PropertyString.ClothingPartExclusions));
            Assert.AreEqual(RegaliaOverrides, regalia.GetProperty(PropertyString.WornModelOverrides), "the source is read, never changed");
        }

        [TestMethod]
        public void PlainSource_OntoPlainTarget_LeavesBothAbsent()
        {
            var source = TestCreatures.CreateQuestBearer("a");
            var target = TestCreatures.CreateQuestBearer("b");

            Tailoring.CopyWornModelProperties(source, target);

            Assert.IsNull(target.GetProperty(PropertyString.WornModelOverrides));
            Assert.IsNull(target.GetProperty(PropertyString.ClothingPartExclusions));
        }

        // ---- the prestige intermediate ----

        [TestMethod]
        public void GatedSource_GivesAnAttunedBondedIntermediate()
        {
            var suit = TestCreatures.CreateQuestBearer("Guise of the Hopeslayer");
            suit.SetProperty(PropertyBool.PvpPrestigeGated, true);
            var intermediate = TestCreatures.CreateQuestBearer("Leather Vest");

            Tailoring.BindPrestigeIntermediate(suit, intermediate);

            Assert.AreEqual((int)AttunedStatus.Attuned, intermediate.GetProperty(PropertyInt.Attuned), "a prestige look must not become a tradeable kit");
            Assert.AreEqual((int)BondedStatus.Bonded, intermediate.GetProperty(PropertyInt.Bonded));
            Assert.AreEqual(true, intermediate.GetProperty(PropertyBool.PvpPrestigeGated), "the flag travels with the look onto the kit");
        }

        [TestMethod]
        public void UngatedAttunedSource_LeavesTheIntermediateUnchanged()
        {
            var retail = TestCreatures.CreateQuestBearer("attuned retail robe");
            retail.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            retail.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            var intermediate = TestCreatures.CreateQuestBearer("Leather Vest");

            Tailoring.BindPrestigeIntermediate(retail, intermediate);

            Assert.IsNull(intermediate.GetProperty(PropertyInt.Attuned), "retail attuned items keep tradeable intermediates");
            Assert.IsNull(intermediate.GetProperty(PropertyInt.Bonded));

            // The bool set to false is ungated too; and the positive control: set to true, the same call binds.
            retail.SetProperty(PropertyBool.PvpPrestigeGated, false);
            Tailoring.BindPrestigeIntermediate(retail, intermediate);
            Assert.IsNull(intermediate.GetProperty(PropertyInt.Attuned));

            retail.SetProperty(PropertyBool.PvpPrestigeGated, true);
            Tailoring.BindPrestigeIntermediate(retail, intermediate);
            Assert.AreEqual((int)AttunedStatus.Attuned, intermediate.GetProperty(PropertyInt.Attuned));
        }

        [TestMethod]
        public void SetCommonProperties_BindsThePrestigeIntermediate_SourceFirst()
        {
            var body = MethodBody(ReadTailoringSource(), "public static void SetCommonProperties(");
            StringAssert.Contains(body, "BindPrestigeIntermediate(source, target)", "the intermediate a prestige suit is tailored into must be attuned and bonded");

            // TailorArmor passes the tailored piece as SetArmorProperties' FIRST argument and the new kit second, so
            // "source" in SetCommonProperties is the suit. Pin that order so the binding reads the right object.
            StringAssert.Contains(MethodBody(ReadTailoringSource(), "public static void TailorArmor("), "SetArmorProperties(target, wo)");
            Assert.IsFalse(MethodBody(ReadTailoringSource(), "public static void UpdateCommonProps(").Contains("BindPrestigeIntermediate("),
                "positive control: the kit-creation binder is not the one the apply step uses");
        }

        // ---- applying a kit: the look binds the piece it lands on ----

        private static WorldObjects.Creature GatedKit()
        {
            var kit = TestCreatures.CreateQuestBearer("Leather Vest");
            kit.SetProperty(PropertyBool.PvpPrestigeGated, true);
            return kit;
        }

        [TestMethod]
        public void GatedKit_OntoAnUnattunedPiece_BindsIt()
        {
            var piece = TestCreatures.CreateQuestBearer("Platemail Hauberk");
            Assert.IsNull(piece.GetProperty(PropertyInt.Attuned), "precondition: tradeable");

            Assert.IsTrue(Tailoring.BindPrestigeTarget(GatedKit(), piece), "the call reports the binding, which is what sends the player message");

            Assert.AreEqual((int)AttunedStatus.Attuned, piece.GetProperty(PropertyInt.Attuned));
            Assert.AreEqual((int)BondedStatus.Bonded, piece.GetProperty(PropertyInt.Bonded));
            Assert.AreEqual(true, piece.GetProperty(PropertyBool.PvpPrestigeGated));
        }

        [TestMethod]
        public void NormalKit_OntoAPrestigePiece_ClearsTheFlag_ButStaysBound()
        {
            var piece = TestCreatures.CreateQuestBearer("bound hauberk");
            Tailoring.BindPrestigeTarget(GatedKit(), piece);
            Assert.AreEqual(true, piece.GetProperty(PropertyBool.PvpPrestigeGated), "precondition: a prestige piece");

            var normalKit = TestCreatures.CreateQuestBearer("Leather Vest");
            Assert.IsFalse(Tailoring.BindPrestigeTarget(normalKit, piece), "a normal look binds nothing new, so no message");

            Assert.IsNull(piece.GetProperty(PropertyBool.PvpPrestigeGated), "a normal look tailored over the prestige look removes the flag");
            Assert.AreEqual((int)AttunedStatus.Attuned, piece.GetProperty(PropertyInt.Attuned), "once bound, the piece stays bound");
            Assert.AreEqual((int)BondedStatus.Bonded, piece.GetProperty(PropertyInt.Bonded));
        }

        [TestMethod]
        public void GatedKit_OntoAStickyPiece_KeepsSticky_StillFlags()
        {
            var piece = TestCreatures.CreateQuestBearer("no-drop quest coat");
            piece.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Sticky);

            var bound = Tailoring.BindPrestigeTarget(GatedKit(), piece);

            Assert.AreEqual((int)AttunedStatus.Sticky, piece.GetProperty(PropertyInt.Attuned), "Sticky outranks Attuned and must not be lowered");
            Assert.AreEqual(true, piece.GetProperty(PropertyBool.PvpPrestigeGated));
            Assert.IsFalse(bound, "a Sticky piece was already untradeable, so nothing newly binds and no message is sent");
        }

        [TestMethod]
        public void GatedKit_OntoADestroyOnDeathPiece_KeepsDestroy_StillFlags()
        {
            var piece = TestCreatures.CreateQuestBearer("boss coat");
            piece.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Destroy);

            Tailoring.BindPrestigeTarget(GatedKit(), piece);

            Assert.AreEqual((int)BondedStatus.Destroy, piece.GetProperty(PropertyInt.Bonded), "Destroy is the opposite intent of Bonded, never a weaker form of it");
            Assert.AreEqual((int)AttunedStatus.Attuned, piece.GetProperty(PropertyInt.Attuned), "the piece is still made untradeable");
            Assert.AreEqual(true, piece.GetProperty(PropertyBool.PvpPrestigeGated));
        }

        [TestMethod]
        public void GatedKit_OntoASlipperyPiece_KeepsSlippery_StillFlags()
        {
            var piece = TestCreatures.CreateQuestBearer("slippery coat");
            piece.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Slippery);

            Tailoring.BindPrestigeTarget(GatedKit(), piece);

            Assert.AreEqual((int)BondedStatus.Slippery, piece.GetProperty(PropertyInt.Bonded), "Slippery is a deliberate status and must survive");
            Assert.AreEqual((int)AttunedStatus.Attuned, piece.GetProperty(PropertyInt.Attuned));
            Assert.AreEqual(true, piece.GetProperty(PropertyBool.PvpPrestigeGated));
        }

        [TestMethod]
        public void GatedKit_OntoAnAlreadyBoundPiece_ReturnsFalse_SoNoRepeatMessage()
        {
            var piece = TestCreatures.CreateQuestBearer("bound hauberk");
            Assert.IsTrue(Tailoring.BindPrestigeTarget(GatedKit(), piece), "positive control: the first application binds and reports it");

            Assert.IsFalse(Tailoring.BindPrestigeTarget(GatedKit(), piece), "the piece was already Attuned, so the second application binds nothing new");
            Assert.AreEqual((int)AttunedStatus.Attuned, piece.GetProperty(PropertyInt.Attuned));
            Assert.AreEqual(true, piece.GetProperty(PropertyBool.PvpPrestigeGated));
        }

        [TestMethod]
        public void NormalKit_OntoAPlainPiece_ChangesNothing()
        {
            var piece = TestCreatures.CreateQuestBearer("plain hauberk");
            Assert.IsFalse(Tailoring.BindPrestigeTarget(TestCreatures.CreateQuestBearer("Leather Vest"), piece));
            Assert.IsNull(piece.GetProperty(PropertyInt.Attuned));
            Assert.IsNull(piece.GetProperty(PropertyInt.Bonded));
            Assert.IsNull(piece.GetProperty(PropertyBool.PvpPrestigeGated));
        }

        [TestMethod]
        public void FullChain_SuitKitPieceKitPiece_EveryHopIsBound()
        {
            static void AssertBound(WorldObjects.WorldObject wo, string hop)
            {
                Assert.AreEqual((int)AttunedStatus.Attuned, wo.GetProperty(PropertyInt.Attuned), $"{hop}: Attuned");
                Assert.AreEqual((int)BondedStatus.Bonded, wo.GetProperty(PropertyInt.Bonded), $"{hop}: Bonded");
                Assert.AreEqual(true, wo.GetProperty(PropertyBool.PvpPrestigeGated), $"{hop}: PvpPrestigeGated");
                Assert.AreEqual(SuitOverrides, wo.GetProperty(PropertyString.WornModelOverrides), $"{hop}: carries the look");
            }

            var suit = TestCreatures.CreateQuestBearer("Guise of the Hopeslayer");
            suit.SetProperty(PropertyString.WornModelOverrides, SuitOverrides);
            suit.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            suit.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            suit.SetProperty(PropertyBool.PvpPrestigeGated, true);
            AssertBound(suit, "suit");

            // Each hop runs the same two helpers the real steps run: SetCommonProperties (kit creation) and UpdateCommonProps (apply).
            var kit1 = TestCreatures.CreateQuestBearer("Leather Vest");
            Tailoring.CopyWornModelProperties(suit, kit1);
            Tailoring.BindPrestigeIntermediate(suit, kit1);
            AssertBound(kit1, "kit made from the suit");

            var piece1 = TestCreatures.CreateQuestBearer("Platemail Hauberk");
            Tailoring.CopyWornModelProperties(kit1, piece1);
            Tailoring.BindPrestigeTarget(kit1, piece1);
            AssertBound(piece1, "piece the kit was applied to");

            var kit2 = TestCreatures.CreateQuestBearer("Leather Vest");
            Tailoring.CopyWornModelProperties(piece1, kit2);
            Tailoring.BindPrestigeIntermediate(piece1, kit2);
            AssertBound(kit2, "kit made by re-tailoring that piece");

            var piece2 = TestCreatures.CreateQuestBearer("Celdon Breastplate");
            Tailoring.CopyWornModelProperties(kit2, piece2);
            Tailoring.BindPrestigeTarget(kit2, piece2);
            AssertBound(piece2, "second piece");
        }

        [TestMethod]
        public void UpdateCommonProps_BindsTheTarget_AndTellsThePlayer()
        {
            var body = MethodBody(ReadTailoringSource(), "public static void UpdateCommonProps(");

            var iBind = body.IndexOf("var newlyBound = BindPrestigeTarget(source, target);", StringComparison.Ordinal);
            Assert.IsTrue(iBind >= 0, "applying a kit must run the prestige binding on the target");

            // GameMessageUpdateObject does not carry Attuned or Bonded, so the client must be sent both as property updates.
            var iAttuned = body.IndexOf("player.UpdateProperty(target, PropertyInt.Attuned, target.GetProperty(PropertyInt.Attuned));", StringComparison.Ordinal);
            var iBonded = body.IndexOf("player.UpdateProperty(target, PropertyInt.Bonded, target.GetProperty(PropertyInt.Bonded));", StringComparison.Ordinal);
            Assert.IsTrue(iAttuned > iBind, "the new Attuned status is sent to the client after the binding");
            Assert.IsTrue(iBonded > iBind, "the new Bonded status is sent to the client after the binding");

            var iIf = body.IndexOf("if (newlyBound)", StringComparison.Ordinal);
            var iSend = body.IndexOf("player.Session.Network.EnqueueSend(new GameMessageSystemChat(PrestigeBindMessage, ChatMessageType.Broadcast))", StringComparison.Ordinal);
            Assert.IsTrue(iIf > iBind && iSend > iIf && iSend < iIf + 200, "the player is told only when the piece has just become bound");

            Assert.AreEqual("The Hopeslayer's likeness binds itself to your armor. It can no longer be traded.", Tailoring.PrestigeBindMessage);
        }

        // ---- the two callers ----

        private static string ReadTailoringSource()
        {
            var native = Path.Combine("Source", "ACE.Server", "Entity", "Tailoring.cs");

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate))
                    return AccountVaultPurgeTests.StripComments(File.ReadAllText(candidate));
            }

            Assert.Fail($"Could not find {native} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static string MethodBody(string code, string signatureAnchor)
        {
            var start = code.IndexOf(signatureAnchor, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"could not find {signatureAnchor}");

            var open = code.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after {signatureAnchor}");
            return null;
        }

        [TestMethod]
        public void SetCommonProperties_And_UpdateCommonProps_BothCarryTheWornModelProperties()
        {
            var code = ReadTailoringSource();

            StringAssert.Contains(MethodBody(code, "public static void SetCommonProperties("), "CopyWornModelProperties(source, target)",
                "the intermediate tailoring kit must pick the override up from the source piece");
            StringAssert.Contains(MethodBody(code, "public static void UpdateCommonProps("), "CopyWornModelProperties(source, target)",
                "the final target must pick the override up (or lose it) from the kit");

            // Positive control: the scan really does discriminate on the method body, not the whole file.
            Assert.IsFalse(MethodBody(code, "public static void UpdateArmorProps(").Contains("CopyWornModelProperties("));
        }
    }
}
