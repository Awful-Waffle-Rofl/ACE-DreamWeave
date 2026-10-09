using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PetDevice's placement refund: ShouldRefundActivation (which summon outcomes hand the activation cooldown
    /// back) and SelectCooldownEntriesToRefund (which registry entries that refund removes - every entry for the
    /// cooldown spell id, caster guid ignored).
    ///
    /// SCOPE NOTE: no test in this project can construct a live Player, and a placement attempt needs a live
    /// owner, a landblock and a physics object. So this covers only the pure decision and selection. The
    /// in-front then owner-spot fallback in Pet.Init and the Remove calls themselves need an in-game check.
    /// </summary>
    [TestClass]
    public class PetDevicePlacementRefundTests
    {
        [TestMethod]
        public void BothPlacementAttemptsFailed_RefundsTheCooldown()
        {
            Assert.IsTrue(PetDevice.ShouldRefundActivation(false, true));
        }

        [TestMethod]
        public void GateRefusalOrStowOrDataError_KeepsTheCooldown()
        {
            // HandleCurrentActivePet refusals, a pet_stow_replace stow, and a missing/non-Pet wcid all return
            // false without ever attempting a placement
            Assert.IsFalse(PetDevice.ShouldRefundActivation(false, false));
        }

        [TestMethod]
        public void RetailStowOfAPassivePet_KeepsTheCooldown()
        {
            // null is the retail "stowed the passive pet with a combat essence" result, which is a real use
            Assert.IsFalse(PetDevice.ShouldRefundActivation(null, false));
        }

        [TestMethod]
        public void FirstPetPlaced_KeepsTheCooldown_EvenIfTheSummon2xSecondPetFailed()
        {
            // only the FIRST summon's outcome is passed in: a Summon 2x activation whose first pet arrived was
            // paid for, whatever happened to the second
            Assert.IsFalse(PetDevice.ShouldRefundActivation(true, false));
        }

        // ---------------------------------------------------------------- SelectCooldownEntriesToRefund

        private const int CombatCooldownSpellId = 0x8000 | 213;
        private const int OtherCooldownSpellId = 0x8000 | 214;

        private const uint ThisEssence = 0x80000001;
        private const uint OtherEssence = 0x80000002;

        private static PropertiesEnchantmentRegistry Entry(int spellId, uint caster)
        {
            return new PropertiesEnchantmentRegistry { SpellId = spellId, CasterObjectId = caster, LayerId = 1 };
        }

        [TestMethod]
        public void Selection_FindsARefreshedEntryThatCarriesAnotherEssencesGuid()
        {
            // an in-place StartCooldown refresh keeps the guid of the essence that started the cooldown first,
            // so a lookup by this essence's guid would find nothing and silently skip the refund
            var refreshed = Entry(CombatCooldownSpellId, OtherEssence);

            var selected = PetDevice.SelectCooldownEntriesToRefund(new List<PropertiesEnchantmentRegistry> { refreshed }, CombatCooldownSpellId);

            Assert.AreEqual(1, selected.Count);
            Assert.AreSame(refreshed, selected[0]);
        }

        [TestMethod]
        public void Selection_TakesEveryDuplicateForTheSpellId_IncludingAnotherEssencesDuringSoulTether()
        {
            // An append-only StartCooldown leaves a second entry for one spell id only while Soul Tether's
            // per-player resummon skip is armed. The same-caster pair is this essence reused on its own live
            // cooldown. The other-caster entry is DELIBERATE, not an accidental catch: another essence on the
            // shared CooldownId whose pet died with its cooldown still running. A failed placement leaves the
            // skip armed, so clearing that entry removes no lockout the player was subject to.
            var older = Entry(CombatCooldownSpellId, ThisEssence);
            var newer = Entry(CombatCooldownSpellId, ThisEssence);
            var otherCaster = Entry(CombatCooldownSpellId, OtherEssence);

            var selected = PetDevice.SelectCooldownEntriesToRefund(new List<PropertiesEnchantmentRegistry> { older, newer, otherCaster }, CombatCooldownSpellId);

            Assert.AreEqual(3, selected.Count);
            CollectionAssert.Contains(selected, older);
            CollectionAssert.Contains(selected, newer);
            CollectionAssert.Contains(selected, otherCaster);
        }

        [TestMethod]
        public void Selection_LeavesOtherCooldownsAndSpellsAlone()
        {
            var ours = Entry(CombatCooldownSpellId, ThisEssence);
            var otherCooldown = Entry(OtherCooldownSpellId, ThisEssence);
            var ordinarySpell = Entry(2, ThisEssence);

            var selected = PetDevice.SelectCooldownEntriesToRefund(new List<PropertiesEnchantmentRegistry> { otherCooldown, ours, ordinarySpell }, CombatCooldownSpellId);

            Assert.AreEqual(1, selected.Count);
            Assert.AreSame(ours, selected[0]);
        }

        [TestMethod]
        public void Selection_NullRegistry_SelectsNothing()
        {
            Assert.AreEqual(0, PetDevice.SelectCooldownEntriesToRefund(null, CombatCooldownSpellId).Count);
        }
    }
}
