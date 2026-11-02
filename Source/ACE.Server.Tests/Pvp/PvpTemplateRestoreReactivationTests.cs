using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Facets;
using ACE.Server.Managers;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvP template restore re-equips the player's own gear with the facet swap-back's rule (#1422): gear whose
    /// spells were active at match entry (FacetEquipEntry.SpellsActive, captured by CaptureFacetEquip at apply) is
    /// equipped inside the facet spell-activation waiver, so TryActivateSpells does not re-run the two buffed checks
    /// in CheckUseRequirements, and the buff-dependent wield kinds are waived. Player report 2026-10-16: "when you come
    /// out of the arena it does not reactivate your armor".
    ///
    /// Runs on PvpTemplatePlayerTests' seeded Player. The real detach-and-equip needs a session, so the equip goes
    /// through the PvpTemplateSettings.EquipOwnItem seam, which records whether the waiver was open at that moment.
    /// Seeds use_wield_requirements (CheckWieldRequirements reads it) and restores the shipped default in a finally.
    /// </summary>
    [TestClass]
    public class PvpTemplateRestoreReactivationTests
    {
        private Func<Player, Container, WorldObject, EquipMask, List<string>, bool> savedEquip;

        [TestInitialize]
        public void Setup()
        {
            savedEquip = PvpTemplateSettings.EquipOwnItem;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpTemplateSettings.EquipOwnItem = savedEquip;
        }

        private static Player NewPlayer() => PvpTemplatePlayerTests.SeededPlayer();

        private static WorldObject PackItem(Player player)
        {
            var item = PvpTemplatePlayerTests.Item(issued: false);
            PvpTemplatePlayerTests.Give(player, item, 0);
            return item;
        }

        private static List<FacetEquipEntry> Remembered(WorldObject item, bool spellsActive)
            => new List<FacetEquipEntry> { new FacetEquipEntry { Guid = item.Guid.Full, Wcid = 1, Slot = (int)EquipMask.ChestArmor, SpellsActive = spellsActive } };

        /// <summary>Runs the restore with use_wield_requirements on, recording each equip and whether the waiver was open for it.</summary>
        private static (List<string> Report, List<(uint Guid, bool Waived)> Equips) Restore(Player player, List<FacetEquipEntry> remembered)
        {
            var equips = new List<(uint, bool)>();

            PvpTemplateSettings.EquipOwnItem = (p, root, item, slot, report) =>
            {
                Assert.AreSame(p, root, "the restore resolves its items from the player's own inventory");
                equips.Add((item.Guid.Full, p.IsFacetSpellActivationWaived(item.Guid.Full)));
                return true;
            };

            var shipped = DefaultPropertyManager.DefaultBooleanProperties["use_wield_requirements"].Item;
            Assert.IsTrue(PropertyManager.ModifyBool("use_wield_requirements", true));

            try
            {
                return (player.RestorePvpTemplateOwnEquip(remembered), equips);
            }
            finally
            {
                PropertyManager.ModifyBool("use_wield_requirements", shipped);
            }
        }

        [TestMethod]
        public void ActiveAtEntry_IsEquippedInsideTheSpellActivationWaiver_AndTheWaiverClosesAfter()
        {
            var player = NewPlayer();
            var armor = PackItem(player);

            var (report, equips) = Restore(player, Remembered(armor, spellsActive: true));

            Assert.AreEqual(0, report.Count, string.Join("; ", report));
            Assert.AreEqual(1, equips.Count, "the item reached the equip");
            Assert.IsTrue(equips[0].Waived, "an item whose spells were active at match entry reactivates without the buffed CheckUseRequirements checks");
            Assert.IsFalse(player.IsFacetSpellActivationWaived(armor.Guid.Full), "the waiver never outlives the one equip it guards");
        }

        [TestMethod]
        public void TwoEntries_OnlyTheActiveOneIsWaived_AndNeitherAfterwards()
        {
            var player = NewPlayer();
            var active = PackItem(player);
            var inactive = PackItem(player);

            var remembered = new List<FacetEquipEntry>
            {
                new FacetEquipEntry { Guid = active.Guid.Full, Wcid = 1, Slot = (int)EquipMask.ChestArmor, SpellsActive = true },
                new FacetEquipEntry { Guid = inactive.Guid.Full, Wcid = 2, Slot = (int)EquipMask.HeadWear, SpellsActive = false },
            };

            var (report, equips) = Restore(player, remembered);

            Assert.AreEqual(0, report.Count, string.Join("; ", report));
            Assert.AreEqual(2, equips.Count, "both items reached the equip");
            Assert.AreEqual(active.Guid.Full, equips[0].Guid);
            Assert.IsTrue(equips[0].Waived, "the entry captured active is waived during its own equip");
            Assert.AreEqual(inactive.Guid.Full, equips[1].Guid);
            Assert.IsFalse(equips[1].Waived, "the entry captured inactive is not waived (a waiver opened outside the loop would leak onto it)");
            Assert.IsFalse(player.IsFacetSpellActivationWaived(active.Guid.Full), "no waiver outlives the restore");
            Assert.IsFalse(player.IsFacetSpellActivationWaived(inactive.Guid.Full), "no waiver outlives the restore");
        }

        [TestMethod]
        public void SpellsActive_SurvivesTheRestoreRecordJsonRoundTrip()
        {
            var record = new PvpTemplateRestoreRecord
            {
                MatchId = Guid.NewGuid(),
                TemplateKey = "roundtrip",
                OwnEquip = new List<FacetEquipEntry>
                {
                    new FacetEquipEntry { Guid = 0x80200001, Wcid = 1, Slot = (int)EquipMask.ChestArmor, SpellsActive = true },
                    new FacetEquipEntry { Guid = 0x80200002, Wcid = 2, Slot = (int)EquipMask.HeadWear, SpellsActive = false },
                },
            };

            Assert.IsTrue(PvpTemplateJson.TrySerializeRecord(record, out var json, out _));
            Assert.IsTrue(PvpTemplateJson.TryDeserializeRecord(json, out var back, out var error), error);

            Assert.AreEqual(2, back.OwnEquip.Count);
            Assert.IsTrue(back.OwnEquip[0].SpellsActive, "an entry captured active is still active after the record is stored and read back");
            Assert.IsFalse(back.OwnEquip[1].SpellsActive, "control: an inactive entry stays inactive");
        }

        [TestMethod]
        public void InactiveAtEntry_IsEquippedWithoutTheWaiver()
        {
            var player = NewPlayer();
            var armor = PackItem(player);

            var (report, equips) = Restore(player, Remembered(armor, spellsActive: false));

            Assert.AreEqual(0, report.Count, string.Join("; ", report));
            Assert.AreEqual(1, equips.Count);
            Assert.IsFalse(equips[0].Waived, "control: an item that was NOT active at entry gets the full activation check");
        }

        [TestMethod]
        public void BuffedAttributeWieldRequirement_IsWaived()
        {
            var player = NewPlayer();
            var armor = PackItem(player);

            // Strength 9999 (current, i.e. buffed): unreachable, so only the waiver lets this through.
            armor.SetProperty(PropertyInt.WieldRequirements, (int)WieldRequirement.Attrib);
            armor.SetProperty(PropertyInt.WieldSkillType, (int)PropertyAttribute.Strength);
            armor.SetProperty(PropertyInt.WieldDifficulty, 9999);

            var (report, equips) = Restore(player, Remembered(armor, spellsActive: true));

            Assert.AreEqual(0, report.Count, string.Join("; ", report));
            Assert.AreEqual(1, equips.Count, "worn at match entry, so a buffed wield requirement does not keep it in the pack");
        }

        [TestMethod]
        public void LevelWieldRequirement_StillRefuses()
        {
            var player = NewPlayer();
            var armor = PackItem(player);

            // Level is not buff-dependent, so the waiver must not touch it: proves the wield check is live in this rig.
            armor.SetProperty(PropertyInt.WieldRequirements, (int)WieldRequirement.Level);
            armor.SetProperty(PropertyInt.WieldDifficulty, 9999);

            var (report, equips) = Restore(player, Remembered(armor, spellsActive: true));

            Assert.AreEqual(0, equips.Count, "a non-buffed requirement still refuses the equip");
            Assert.AreEqual(1, report.Count);
            StringAssert.Contains(report[0], "no longer meets its wield requirements");
        }

        [TestMethod]
        public void EquipThrows_TheWaiverIsStillClosed()
        {
            var player = NewPlayer();
            var armor = PackItem(player);

            PvpTemplateSettings.EquipOwnItem = (p, root, item, slot, report) => throw new InvalidOperationException("equip threw");

            var shipped = DefaultPropertyManager.DefaultBooleanProperties["use_wield_requirements"].Item;
            Assert.IsTrue(PropertyManager.ModifyBool("use_wield_requirements", true));

            try
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => player.RestorePvpTemplateOwnEquip(Remembered(armor, spellsActive: true)));
            }
            finally
            {
                PropertyManager.ModifyBool("use_wield_requirements", shipped);
            }

            Assert.IsFalse(player.IsFacetSpellActivationWaived(armor.Guid.Full), "the finally closes the waiver even when the equip throws");
        }
    }
}
