using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity.Facets;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// FacetWithdrawnEquip.TryEquip: the facet vault restore's equip-first step (wield check, the
    /// spell-activation waiver held across the equip, the equip itself). Driven through its collaborator
    /// delegates, so no live Player is involved - production passes the Player's own CheckWieldRequirements,
    /// Begin/EndFacetSpellActivationWaiver and TryEquipWithdrawnForFacet, which these tests do not reach.
    /// </summary>
    [TestClass]
    public class FacetWithdrawnEquipTests
    {
        private const int Slot = 0x00000200;

        private sealed class Recorder
        {
            public readonly List<string> Calls = new List<string>();
            public readonly HashSet<uint> Waived = new HashSet<uint>();

            public WeenieError WieldAnswer = WeenieError.None;
            public bool EquipAnswer = true;
            public bool EquipThrows;

            public bool? WaivedDuringEquip;
            public EquipMask? EquipMaskSeen;

            public bool Run(WorldObject item, bool spellsActive, bool waive, out WeenieError wieldError)
            {
                var resolution = new FacetGearResolution(FacetGearSource.VaultItem, 0xDEAD0001, item.WeenieClassId, Slot, spellsActive);

                return FacetWithdrawnEquip.TryEquip(item, resolution, waive,
                    (wo, w) => { Calls.Add("check"); return WieldAnswer; },
                    guid => { Calls.Add("begin"); Waived.Add(guid); },
                    guid => { Calls.Add("end"); Waived.Remove(guid); },
                    (wo, mask) =>
                    {
                        Calls.Add("equip");
                        WaivedDuringEquip = Waived.Contains(wo.Guid.Full);
                        EquipMaskSeen = mask;

                        if (EquipThrows)
                            throw new InvalidOperationException("fake equip failure");

                        return EquipAnswer;
                    },
                    out wieldError);
            }
        }

        private static WorldObject Item() => FakeVaultWorld.MakeStack(8000, 1, 100);

        [TestMethod]
        public void WaiverEnd_RunsInAFinally_WhenTheEquipThrows()
        {
            var rec = new Recorder { EquipThrows = true };
            var item = Item();

            WeenieError wieldError = WeenieError.ActionCancelled;

            Assert.ThrowsExactly<InvalidOperationException>(() => rec.Run(item, spellsActive: true, waive: true, out wieldError),
                "the equip's exception must still propagate after the finally, as it did inline");

            CollectionAssert.AreEqual(new[] { "check", "begin", "equip", "end" }, rec.Calls);
            Assert.AreEqual(true, rec.WaivedDuringEquip, "the waiver must be held across the equip");
            Assert.AreEqual(0, rec.Waived.Count, "a waiver leaked past a throwing equip would apply to a later, unrelated equip of the same guid");
            Assert.AreEqual(WeenieError.None, wieldError, "wieldError is assigned before the equip, so it survives the throw");
        }

        [TestMethod]
        public void WaiverIsKeyedOnTheWithdrawnObjectsGuid_AndReleasedAfterASuccessfulEquip()
        {
            var rec = new Recorder();
            var item = Item();

            Assert.IsTrue(rec.Run(item, spellsActive: true, waive: true, out _));

            CollectionAssert.AreEqual(new[] { "check", "begin", "equip", "end" }, rec.Calls);
            Assert.AreEqual(true, rec.WaivedDuringEquip, "the waiver must name the withdrawn object's guid, not resolution.Guid");
            Assert.AreEqual(0, rec.Waived.Count);
            Assert.AreEqual((EquipMask)Slot, rec.EquipMaskSeen);
        }

        [TestMethod]
        public void NoWaiver_UnlessBothWaivingAndSpellsActive()
        {
            foreach (var (spellsActive, waive) in new[] { (true, false), (false, true), (false, false) })
            {
                var rec = new Recorder();

                Assert.IsTrue(rec.Run(Item(), spellsActive, waive, out _));

                CollectionAssert.AreEqual(new[] { "check", "equip" }, rec.Calls, $"spellsActive={spellsActive} waive={waive}");
                Assert.AreEqual(false, rec.WaivedDuringEquip);
            }
        }

        [TestMethod]
        public void WieldRefusal_SetsWieldError_AndNeverEquips()
        {
            var rec = new Recorder { WieldAnswer = WeenieError.SkillTooLow };

            var worn = rec.Run(Item(), spellsActive: true, waive: true, out var wieldError);

            Assert.IsFalse(worn);
            Assert.AreEqual(WeenieError.SkillTooLow, wieldError, "the caller's pack-delivery report reads this value");
            Assert.IsFalse(rec.Calls.Contains("equip"), "an item that fails its wield check must never be equipped");
            Assert.AreEqual(0, rec.Waived.Count, "a waiver begun must still be ended when the equip is skipped");
        }

        [TestMethod]
        public void EquipRefusal_ReturnsFalse_WithNoWieldError()
        {
            var rec = new Recorder { EquipAnswer = false };

            Assert.IsFalse(rec.Run(Item(), spellsActive: false, waive: false, out var wieldError));
            Assert.AreEqual(WeenieError.None, wieldError);
            CollectionAssert.AreEqual(new[] { "check", "equip" }, rec.Calls);
        }
    }
}
