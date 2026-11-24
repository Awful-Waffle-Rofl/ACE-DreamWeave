using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;

using Kind = ACE.Server.Entity.TinkerLock.ModSalvageKind;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure coverage for the salvage allow-list rule (PropertyInt.ModSalvageAllowMask 9095) inside TinkerLock.
    /// Every test passes explicit arguments; none reads PropertyManager (which throws outside a running server).
    /// </summary>
    [TestClass]
    public class TinkerLockModSalvageMaskTests
    {
        private static bool Refuses(int? mask, bool mana, bool pair, Kind kind, bool weaponOn, bool equipOn)
            => TinkerLock.ModSalvageMaskRefuses(mask, mana, pair, kind, weaponOn, equipOn, sourceIsTether: false);

        [TestMethod]
        public void AbsentMask_NeverRefuses()
        {
            foreach (Kind kind in new[] { Kind.Other, Kind.Tourmaline, Kind.Amethyst, Kind.Obsidian })
            foreach (var mana in new[] { false, true })
            foreach (var pair in new[] { false, true })
            foreach (var w in new[] { false, true })
            foreach (var e in new[] { false, true })
                Assert.IsFalse(Refuses(null, mana, pair, kind, w, e), $"{kind} mana={mana} pair={pair} w={w} e={e}");
        }

        [TestMethod]
        public void ManaStoneAndExactPair_AlwaysPass_ForEveryMask()
        {
            for (var mask = 0; mask <= 7; mask++)
            foreach (Kind kind in new[] { Kind.Other, Kind.Tourmaline, Kind.Amethyst, Kind.Obsidian })
            foreach (var w in new[] { false, true })
            foreach (var e in new[] { false, true })
            {
                Assert.IsFalse(Refuses(mask, true, false, kind, w, e), $"mana mask={mask} {kind}");
                Assert.IsFalse(Refuses(mask, false, true, kind, w, e), $"pair mask={mask} {kind}");
            }
        }

        [TestMethod]
        public void OtherSource_AlwaysRefused_ForEveryMask()
        {
            for (var mask = 0; mask <= 7; mask++)
            foreach (var w in new[] { false, true })
            foreach (var e in new[] { false, true })
                Assert.IsTrue(Refuses(mask, false, false, Kind.Other, w, e), $"mask={mask}");
        }

        [TestMethod]
        public void IsTetherSource_OnlyTetherAndRemover()
        {
            Assert.IsTrue(TinkerLock.IsTetherSource(45683u));
            Assert.IsTrue(TinkerLock.IsTetherSource(45684u));
            Assert.IsFalse(TinkerLock.IsTetherSource(45682u));
            Assert.IsFalse(TinkerLock.IsTetherSource(45685u));
            Assert.IsFalse(TinkerLock.IsTetherSource(0u));
        }

        [TestMethod]
        public void TetherSource_NeverRefused_ForEveryMaskAndFlagCombination()
        {
            foreach (uint wcid in new[] { 45683u, 45684u })
            for (var mask = 0; mask <= 7; mask++)
            foreach (Kind kind in new[] { Kind.Other, Kind.Tourmaline, Kind.Amethyst, Kind.Obsidian })
            foreach (var mana in new[] { false, true })
            foreach (var pair in new[] { false, true })
            foreach (var w in new[] { false, true })
            foreach (var e in new[] { false, true })
                Assert.IsFalse(TinkerLock.ModSalvageMaskRefuses(mask, mana, pair, kind, w, e, TinkerLock.IsTetherSource(wcid)),
                    $"wcid={wcid} mask={mask} {kind} mana={mana} pair={pair} w={w} e={e}");
        }

        [TestMethod]
        public void NonTetherOtherSource_StillRefused_ForEveryNonNullMask()
        {
            // Discriminating control: fails if the exemption were applied to every Other source.
            foreach (uint wcid in new[] { 0u, 45682u, 45685u, 1007600u })
            for (var mask = 0; mask <= 7; mask++)
            foreach (var w in new[] { false, true })
            foreach (var e in new[] { false, true })
                Assert.IsTrue(TinkerLock.ModSalvageMaskRefuses(mask, false, false, Kind.Other, w, e, TinkerLock.IsTetherSource(wcid)),
                    $"wcid={wcid} mask={mask} w={w} e={e}");
        }

        [TestMethod]
        public void NullMask_NeverRefuses_ForTetherOrNot()
        {
            foreach (var tether in new[] { false, true })
                Assert.IsFalse(TinkerLock.ModSalvageMaskRefuses(null, false, false, Kind.Other, true, true, tether));
        }

        [TestMethod]
        public void BothProductionCallers_PassTheTetherExemption()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/Entity/TinkerLock.cs");
            var refusesBody = CodeOnly(PooledLootSourceText.MethodBody(src, "public static bool IsRefused(WorldObject source, WorldObject target)"));
            var textBody = CodeOnly(PooledLootSourceText.MethodBody(src, "private static bool ModSalvageMaskRefusesProduction(int? mask, WorldObject source, WorldObject target)"));

            // The tether flag is the LAST argument of each ModSalvageMaskRefuses call, so a bool moved into another slot fails.
            const string trailing = "GetBool(\"equipment_mods_enabled\").Item, IsTetherSource(source.WeenieClassId));";
            StringAssert.Contains(refusesBody, trailing);
            StringAssert.Contains(textBody, trailing);
        }

        // Method body with // comment lines removed and whitespace runs collapsed, so a commented-out argument cannot satisfy a pin.
        private static string CodeOnly(string body)
        {
            var kept = new System.Collections.Generic.List<string>();
            foreach (var line in body.Split('\n'))
            {
                if (!line.TrimStart().StartsWith("//"))
                    kept.Add(line.Trim());
            }

            return System.Text.RegularExpressions.Regex.Replace(string.Join(" ", kept), @"\s+", " ");
        }
        [TestMethod]
        public void ModSalvage_TruthTable_PerBitAndFlag()
        {
            for (var mask = 0; mask <= 7; mask++)
            foreach (var w in new[] { false, true })
            foreach (var e in new[] { false, true })
            {
                Assert.AreEqual(!((mask & 1) != 0 && w), Refuses(mask, false, false, Kind.Tourmaline, w, e), $"Tourmaline mask={mask} w={w} e={e}");
                Assert.AreEqual(!((mask & 2) != 0 && w), Refuses(mask, false, false, Kind.Amethyst, w, e), $"Amethyst mask={mask} w={w} e={e}");
                Assert.AreEqual(!((mask & 4) != 0 && e), Refuses(mask, false, false, Kind.Obsidian, w, e), $"Obsidian mask={mask} w={w} e={e}");
            }
        }

        [TestMethod]
        public void FlagOff_RefusesAllowedMaterial()
        {
            Assert.IsTrue(Refuses(1, false, false, Kind.Tourmaline, weaponOn: false, equipOn: true));
            Assert.IsTrue(Refuses(2, false, false, Kind.Amethyst, weaponOn: false, equipOn: true));
            Assert.IsTrue(Refuses(4, false, false, Kind.Obsidian, weaponOn: true, equipOn: false));
            Assert.IsFalse(Refuses(1, false, false, Kind.Tourmaline, weaponOn: true, equipOn: false));
            Assert.IsFalse(Refuses(4, false, false, Kind.Obsidian, weaponOn: false, equipOn: true));
        }

        [TestMethod]
        public void MaskZero_PresentButAllowsNothingExceptMana()
        {
            Assert.IsTrue(Refuses(0, false, false, Kind.Tourmaline, true, true));
            Assert.IsTrue(Refuses(0, false, false, Kind.Obsidian, true, true));
            Assert.IsFalse(Refuses(0, true, false, Kind.Other, true, true));
        }

        [DataTestMethod]
        [DataRow(0, "This item cannot be tinkered.")]
        [DataRow(1, "This item cannot be tinkered, except with Tourmaline.")]
        [DataRow(2, "This item cannot be tinkered, except with Amethyst.")]
        [DataRow(3, "This item cannot be tinkered, except with Tourmaline or Amethyst.")]
        [DataRow(4, "This item cannot be tinkered, except with Obsidian.")]
        [DataRow(5, "This item cannot be tinkered, except with Tourmaline or Obsidian.")]
        [DataRow(6, "This item cannot be tinkered, except with Amethyst or Obsidian.")]
        [DataRow(7, "This item cannot be tinkered, except with Tourmaline, Amethyst or Obsidian.")]
        public void RefusalText_ForMasks0To7(int mask, string expected)
        {
            Assert.AreEqual(expected, TinkerLock.ModSalvageMaskRefusalText(mask));
        }

        [DataTestMethod]
        [DataRow(8, "This item cannot be tinkered, except with tailoring kits.")]
        [DataRow(12, "This item cannot be tinkered, except with Obsidian or tailoring kits.")]
        public void RefusalText_TailoringBit(int mask, string expected)
        {
            Assert.AreEqual(expected, TinkerLock.ModSalvageMaskRefusalText(mask));
        }

        [TestMethod]
        public void Tailoring_AllowedOnlyByItsBit_IndependentOfSystemFlags()
        {
            for (var mask = 0; mask <= 15; mask++)
            foreach (var w in new[] { false, true })
            foreach (var e in new[] { false, true })
                Assert.AreEqual((mask & TinkerLock.ModSalvageTailoringBit) == 0, Refuses(mask, false, false, Kind.Tailoring, w, e), $"Tailoring mask={mask} w={w} e={e}");
        }

        [TestMethod]
        public void ObsidianAndTailoringMask_StillRefusesOtherSalvage()
        {
            // The reissued rare armor carries mask 12: Steel and every other retail salvage classify as Other.
            Assert.IsTrue(Refuses(12, false, false, Kind.Other, true, true));
            Assert.IsFalse(Refuses(12, false, false, Kind.Obsidian, true, true));
            Assert.IsFalse(Refuses(12, false, false, Kind.Tailoring, true, true));
        }

        [TestMethod]
        public void ClassifyMaskSource_TailoringKitsIncludingVendorClones()
        {
            Assert.AreEqual(Kind.Tailoring, TinkerLock.ClassifyMaskSource(Tailoring.ArmorTailoringKit, ItemType.Misc, null));
            Assert.AreEqual(Kind.Tailoring, TinkerLock.ClassifyMaskSource(Tailoring.ArmorMainReductionTool, ItemType.Misc, null));
            Assert.AreEqual(Kind.Tailoring, TinkerLock.ClassifyMaskSource(1004315, ItemType.Misc, null));
            var kits = new uint[]
            {
                Tailoring.ArmorTailoringKit, Tailoring.WeaponTailoringKit, Tailoring.ArmorMainReductionTool, Tailoring.ArmorLowerReductionTool,
                Tailoring.ArmorMiddleReductionTool, Tailoring.ArmorLayeringToolTop, Tailoring.ArmorLayeringToolBottom,
                1004312, 1004313, 1004314, 1004315, 1004316, 1004317, 1004318,
                Tailoring.LeatherVest, Tailoring.WingedCoat, Tailoring.PlatemailGauntlets, Tailoring.YoroiGirth, Tailoring.YoroiGreaves, Tailoring.Heaume,
                Tailoring.YoroiLeggings, Tailoring.AmuliLeggings, Tailoring.YoroiPauldrons, Tailoring.CeldonSleeves, Tailoring.LeatherBoots, Tailoring.Tentacles,
            };
            foreach (var kit in kits)
                Assert.AreEqual(Kind.Tailoring, TinkerLock.ClassifyMaskSource(kit, ItemType.Misc, null), $"kit {kit}");

            Assert.AreEqual(Kind.Other, TinkerLock.ClassifyMaskSource(1007600, ItemType.MeleeWeapon, null));
            Assert.AreEqual(Kind.Obsidian, TinkerLock.ClassifyMaskSource(21063, ItemType.TinkeringMaterial, MaterialType.Obsidian));
            Assert.AreEqual(Kind.Other, TinkerLock.ClassifyMaskSource(20993, ItemType.TinkeringMaterial, MaterialType.Steel));
        }

        [TestMethod]
        public void BothProductionCallers_ClassifyThroughClassifyMaskSource()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/Entity/TinkerLock.cs");
            var refusesBody = CodeOnly(PooledLootSourceText.MethodBody(src, "public static bool IsRefused(WorldObject source, WorldObject target)"));
            var textBody = CodeOnly(PooledLootSourceText.MethodBody(src, "private static bool ModSalvageMaskRefusesProduction(int? mask, WorldObject source, WorldObject target)"));

            const string call = "ClassifyMaskSource(source.WeenieClassId, source.ItemType, source.MaterialType)";
            StringAssert.Contains(refusesBody, call);
            StringAssert.Contains(textBody, call);
        }

        [TestMethod]
        public void RefusalMessage_MaskComesAfterDrift_BeforeKnotAndSiraluun()
        {
            Assert.AreEqual(TinkerLock.DriftRefusalText, TinkerLock.RefusalMessage(5u, true, "X", true, 3));
            Assert.AreEqual(TinkerLock.ModSalvageMaskRefusalText(3), TinkerLock.RefusalMessage(5u, false, "X", true, 3));
            Assert.AreEqual(TinkerLock.KnotRefusalText, TinkerLock.RefusalMessage(5u, false, "X", true));
            Assert.AreEqual(string.Format(TinkerLock.SiraluunRefusalFormat, "X"), TinkerLock.RefusalMessage(5u, false, "X"));
            Assert.AreEqual(TinkerLock.ModSalvageMaskRefusalText(0), TinkerLock.RefusalMessage(null, false, "X", false, 0));
        }

        [TestMethod]
        public void StationText_NullSource_UsesNoMaterialsText_ForNonzeroMask()
        {
            for (var mask = 0; mask <= 7; mask++)
            {
                var text = TinkerLock.RefusalMessage(null, false, "X", false, TinkerLock.ModSalvageMaskForText(mask, isStation: true));
                Assert.AreEqual("This item cannot be tinkered.", text, $"station mask={mask}");
            }

            Assert.AreEqual(3, TinkerLock.ModSalvageMaskForText(3, isStation: false));
            Assert.AreEqual("This item cannot be tinkered, except with Tourmaline or Amethyst.",
                TinkerLock.RefusalMessage(5u, false, "X", false, TinkerLock.ModSalvageMaskForText(3, isStation: false)));
        }

        [TestMethod]
        public void RefusalMessage_WorldObjectOverload_RoutesNullSourceThroughStationText()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/Entity/TinkerLock.cs"),
                "public static string RefusalMessage(WorldObject source, WorldObject target)");
            StringAssert.Contains(body, "ModSalvageMaskForText(mask.Value, isStation: source == null)");
        }

        [TestMethod]
        public void Station_RefusesWheneverMaskPresent_IncludingZero()
        {
            Assert.IsTrue(TinkerLock.IsRefusedByStation(false, false, 0));
            Assert.IsTrue(TinkerLock.IsRefusedByStation(false, false, 7));
            Assert.IsFalse(TinkerLock.IsRefusedByStation(false, false, null));
            Assert.IsTrue(TinkerLock.IsRefusedByStation(true, false, null));
            Assert.IsTrue(TinkerLock.IsRefusedByStation(false, true, null));
        }

        [TestMethod]
        public void ClassifyModSalvage_ByItemTypeAndMaterial()
        {
            Assert.AreEqual(Kind.Tourmaline, TinkerLock.ClassifyModSalvage(ItemType.TinkeringMaterial, MaterialType.Tourmaline));
            Assert.AreEqual(Kind.Amethyst, TinkerLock.ClassifyModSalvage(ItemType.TinkeringMaterial, MaterialType.Amethyst));
            Assert.AreEqual(Kind.Obsidian, TinkerLock.ClassifyModSalvage(ItemType.TinkeringMaterial, MaterialType.Obsidian));
            Assert.AreEqual(Kind.Other, TinkerLock.ClassifyModSalvage(ItemType.Gem, MaterialType.Tourmaline));
            Assert.AreEqual(Kind.Other, TinkerLock.ClassifyModSalvage(ItemType.TinkeringMaterial, MaterialType.Ruby));
            Assert.AreEqual(Kind.Other, TinkerLock.ClassifyModSalvage(ItemType.TinkeringMaterial, null));
        }

        [TestMethod]
        public void AbsentProperty_ExistingRulesUnchanged()
        {
            Assert.IsFalse(TinkerLock.IsRefused(false, false, false, false, false, false));
            Assert.IsTrue(TinkerLock.IsRefused(true, false, false, false, false, false));
            Assert.IsTrue(TinkerLock.IsRefused(false, true, false, false, false, false));
            Assert.AreEqual(string.Format(TinkerLock.SiraluunRefusalFormat, "X"), TinkerLock.RefusalMessage(5u, false, "X"));
        }
    }
}
