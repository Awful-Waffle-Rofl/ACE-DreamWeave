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
            => TinkerLock.ModSalvageMaskRefuses(mask, mana, pair, kind, weaponOn, equipOn);

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
