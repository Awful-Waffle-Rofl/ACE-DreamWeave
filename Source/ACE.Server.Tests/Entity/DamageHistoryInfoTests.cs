using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.PooledLootSourceText;

namespace ACE.Server.Tests.Entity
{
    /// <summary>
    /// DamageHistoryInfo.ResolvePetOwnerAsKiller: a kill landed by a player's CombatPet must be eligible for
    /// rares and Marae Lassel treasure maps exactly as if the pet's owner had made the kill.
    ///
    /// No test in this project constructs a live Player (see MuleCommerceTests.cs's SCOPE NOTE and
    /// PersonalVendorTests.cs's class remarks: Player's constructor calls
    /// DatabaseManager.Authentication.GetAccountById unconditionally). Pet.P_PetOwner is typed Player, not
    /// WorldObject, so the "pet with a live owner" and "pet whose owner reference has been collected"
    /// branches cannot be driven through a real DamageHistoryInfo(CombatPet) construction in this harness
    /// either - they are pinned against source instead (ResolvePetOwnerAsKiller_source_matches_the_rule
    /// below), exactly as MlRelariaChargeTrophyTests.cs already does for the same Player-typed-field
    /// constraint. Every branch that does NOT require a live Player (null killer, a non-pet killer, a pet
    /// with no owner set) is driven directly.
    /// </summary>
    [TestClass]
    public class DamageHistoryInfoTests
    {
        private static int nextGuid = 0x7F200000;
        private static uint NextGuid() => (uint)System.Threading.Interlocked.Increment(ref nextGuid);

        private static WorldObject Named(uint guid, string name)
            => new GenericObject(new Weenie
            {
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            }, new ObjectGuid(guid));

        [TestMethod]
        public void Null_killer_returns_null()
        {
            Assert.IsNull(DamageHistoryInfo.ResolvePetOwnerAsKiller(null));
        }

        [TestMethod]
        public void Plain_player_range_killer_returns_the_same_instance()
        {
            // Guid.IsPlayer() is range-based (ObjectGuid), so a plain WorldObject in the player guid range
            // reports IsPlayer true without needing a real Player instance - the same pattern
            // GroupLootBankTests.Named already relies on.
            var attacker = Named(0x50000010u, "Attacker");
            var killer = new DamageHistoryInfo(attacker);

            var resolved = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);

            Assert.AreSame(killer, resolved, "a non-pet killer must be returned unchanged");
        }

        [TestMethod]
        public void Non_player_non_pet_killer_returns_the_same_instance()
        {
            var attacker = Named(NextGuid(), "SomeMonster");
            var killer = new DamageHistoryInfo(attacker);

            var resolved = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);

            Assert.AreSame(killer, resolved);
        }

        [TestMethod]
        public void CombatPet_with_no_owner_set_returns_the_same_instance()
        {
            // The Creature constructor reads the vital formulas (Creature.SetEphemeralValues -> GameTables),
            // same requirement as GroupLootBankTests.RunCreature / ThreadDungeonRunLootPoolTests.
            TestGameTables.EnsureInitialized();

            var pet = new CombatPet(new Weenie { WeenieClassId = 42, WeenieType = WeenieType.Creature }, new ObjectGuid(NextGuid()));
            // P_PetOwner intentionally left null: DamageHistoryInfo's constructor only sets PetOwner when
            // combatPet.P_PetOwner != null, so this pet's DamageHistoryInfo.PetOwner is null too.
            var killer = new DamageHistoryInfo(pet);

            Assert.IsNull(killer.PetOwner, "precondition: an owner-less pet must not carry a PetOwner reference");

            var resolved = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);

            Assert.AreSame(killer, resolved, "a pet with no live owner reference must be returned unchanged");
        }

        /// <summary>
        /// The "pet with a live owner" and "owner reference collected" branches cannot be driven through a
        /// real construction (see class remarks), so they are pinned against source instead: the method must
        /// read PetOwner off the killer it was given, resolve it through TryGetPetOwner(), build a fresh
        /// DamageHistoryInfo from that owner when it is still alive, and fall back to the original killer
        /// unchanged when it is not (the same "vanished reference keeps the original" shape as
        /// TryGetPetOwnerOrAttacker just above it in this file).
        /// </summary>
        [TestMethod]
        public void ResolvePetOwnerAsKiller_source_matches_the_rule()
        {
            var src = Read("Source/ACE.Server/Entity/DamageHistoryInfo.cs");
            var method = MethodBody(src, "public static DamageHistoryInfo ResolvePetOwnerAsKiller(DamageHistoryInfo killer)");

            StringAssert.Contains(method, "if (killer?.PetOwner == null)\n                return killer;");
            StringAssert.Contains(method, "var owner = killer.TryGetPetOwner();");
            StringAssert.Contains(method, "return owner != null ? new DamageHistoryInfo(owner) : killer;");
        }
    }
}
