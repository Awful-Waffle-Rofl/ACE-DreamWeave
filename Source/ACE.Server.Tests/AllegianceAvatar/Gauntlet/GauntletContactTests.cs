using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Realms;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Contact credit for a Gauntlet fight (plan Task 11): GauntletHooks.OnContact, reached through the two World Event entry
    /// points, reports the member and the creature's wave to the run's IGauntletContactSink. The hook is driven directly for
    /// the direction and state rules, and through the real DamageHistory.Add for the consumer pin (a bare-reflection Player
    /// cannot run the rest of Add, so a Player victim goes through the hook line, as in WorldEventCombatCreditTests).
    /// </summary>
    [TestClass]
    public class GauntletContactTests
    {
        private static uint nextGuid = 0x50E00000;

        private sealed class SinkOwner : IGauntletCreatureOwner, IGauntletContactSink
        {
            public bool IsLive => true;
            public int CurrentWave => 1;
            public double CentreGx => 0;
            public double CentreGy => 0;
            public double Radius => 60;
            public bool Throw;
            public readonly List<(Player Member, int Wave, bool Active)> Contacts = new List<(Player, int, bool)>();

            public IEnumerable<Player> OnlineMembersInside() => new List<Player>();
            public void OnGauntletCreatureDied(Creature creature) { }

            public void OnMemberContact(Player member, int wave, bool active)
            {
                if (Throw)
                    throw new InvalidOperationException("fake sink fault");

                Contacts.Add((member, wave, active));
            }
        }

        /// <summary>An owner that is NOT a contact sink (Task 6's fake): the hook must do nothing for it.</summary>
        private sealed class PlainOwner : IGauntletCreatureOwner
        {
            public bool IsLive => true;
            public int CurrentWave => 1;
            public double CentreGx => 0;
            public double CentreGy => 0;
            public double Radius => 60;
            public IEnumerable<Player> OnlineMembersInside() => new List<Player>();
            public void OnGauntletCreatureDied(Creature creature) { }
        }

        private static Creature MakeCreature(IGauntletCreatureOwner owner, int wave)
        {
            var wcid = 0x7E100000u + (nextGuid & 0xFFFF);

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"gauntletcontacttest{wcid}",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Gauntlet Contact Test Creature" } }
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.P_Gauntlet = owner;
            creature.P_GauntletWave = wave;
            return creature;
        }

        private static void SetField(object target, Type declaring, string name, object value)
        {
            var field = declaring.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{declaring.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }

        private static T MakeBare<T>(string name) where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetField(wo, typeof(WorldObject), "<Guid>k__BackingField", new ObjectGuid(nextGuid++));
            SetField(wo, typeof(WorldObject), "<Biota>k__BackingField", new ACE.Entity.Models.Biota
            {
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } }
            });
            SetField(wo, typeof(WorldObject), "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return wo;
        }

        private static Player MakePlayer(string name) => MakeBare<Player>(name);

        [TestMethod]
        public void MemberHitsGauntletCreature_WaveRecorded()
        {
            var owner = new SinkOwner();
            var creature = MakeCreature(owner, 7);
            var member = MakePlayer("Hitter");

            GauntletHooks.OnContact(creature, member);

            Assert.AreEqual(1, owner.Contacts.Count);
            Assert.AreSame(member, owner.Contacts[0].Member);
            Assert.AreEqual(7, owner.Contacts[0].Wave, "the creature's own wave stamp, not the run's current wave");
            Assert.IsTrue(owner.Contacts[0].Active, "the member dealt it: active contact");
        }

        [TestMethod]
        public void MemberPetHitsGauntletCreature_CreditsTheOwner()
        {
            var owner = new SinkOwner();
            var creature = MakeCreature(owner, 2);
            var master = MakePlayer("Summoner");
            var pet = MakeBare<CombatPet>("Pet");
            pet.P_PetOwner = master;

            GauntletHooks.OnContact(creature, pet);

            Assert.AreEqual(1, owner.Contacts.Count);
            Assert.AreSame(master, owner.Contacts[0].Member);
            Assert.IsTrue(owner.Contacts[0].Active, "their own pet dealt it: active contact for the owner");
        }

        [TestMethod]
        public void OwnerlessPet_NoCredit()
        {
            // The Avatar fighter is a CombatPet with no player owner: its hits are nobody's contact.
            var owner = new SinkOwner();
            var creature = MakeCreature(owner, 2);
            var fighter = MakeBare<CombatPet>("Avatar");

            GauntletHooks.OnContact(creature, fighter);

            Assert.AreEqual(0, owner.Contacts.Count);
        }

        [TestMethod]
        public void GauntletCreatureHitsMember_WaveRecorded()
        {
            var owner = new SinkOwner();
            var creature = MakeCreature(owner, 4);
            var member = MakePlayer("Tank");

            GauntletHooks.OnContact(member, creature);

            Assert.AreEqual(1, owner.Contacts.Count);
            Assert.AreSame(member, owner.Contacts[0].Member);
            Assert.AreEqual(4, owner.Contacts[0].Wave);
            Assert.IsFalse(owner.Contacts[0].Active, "being hit is fought, never active");
        }

        [TestMethod]
        public void AbsorbedHit_Counts()
        {
            // A fully absorbed hit and a stamina or mana attack reach the World Event entry point OnCombatContact (no health
            // amount). The Gauntlet branch is the first line of both entry points (GauntletContactHookSiteTests), so the
            // same call shape reaches the sink with no damage figure involved.
            var owner = new SinkOwner();
            var creature = MakeCreature(owner, 3);
            var member = MakePlayer("Warded");

            ACE.Server.WorldEvents.WorldEventHooks.OnCombatContact(creature, member);   // member hit it for 0
            ACE.Server.WorldEvents.WorldEventHooks.OnCombatContact(member, creature);   // it drained the member's stamina

            Assert.AreEqual(2, owner.Contacts.Count);
            Assert.IsTrue(owner.Contacts.TrueForAll(c => c.Wave == 3 && ReferenceEquals(c.Member, member)));
        }

        [TestMethod]
        public void InvincibleVictim_NoCredit()
        {
            var owner = new SinkOwner();
            var creature = MakeCreature(owner, 1);
            var member = MakePlayer("Gm");
            member.Invincible = true;

            GauntletHooks.OnContact(member, creature);

            Assert.AreEqual(0, owner.Contacts.Count, "an Invincible player never took the hit");

            member.Invincible = false;
            member.UnderLifestoneProtection = true;

            GauntletHooks.OnContact(member, creature);

            Assert.AreEqual(0, owner.Contacts.Count, "nor one under lifestone protection");

            member.UnderLifestoneProtection = false;

            GauntletHooks.OnContact(member, creature);

            Assert.AreEqual(1, owner.Contacts.Count, "positive control: the same hit credits once the states clear");
        }

        [TestMethod]
        public void NonGauntletCreature_NoCost()
        {
            var owner = new SinkOwner();
            var stranger = MakeCreature(null, 0);
            var member = MakePlayer("Bystander");
            var other = MakePlayer("Other");

            GauntletHooks.OnContact(stranger, member);
            GauntletHooks.OnContact(member, stranger);
            GauntletHooks.OnContact(other, member);      // player on player
            GauntletHooks.OnContact(member, member);     // falling
            GauntletHooks.OnContact(null, member);
            GauntletHooks.OnContact(stranger, null);

            Assert.AreEqual(0, owner.Contacts.Count);
        }

        [TestMethod]
        public void OwnerThatIsNotASink_IsIgnored()
        {
            var creature = MakeCreature(new PlainOwner(), 1);
            var member = MakePlayer("Hitter");

            GauntletHooks.OnContact(creature, member);
            GauntletHooks.OnContact(member, creature);   // no throw, nothing to assert beyond that
        }

        [TestMethod]
        public void SinkThatThrows_NeverAbortsTheHit()
        {
            var owner = new SinkOwner { Throw = true };
            var creature = MakeCreature(owner, 1);
            var member = MakePlayer("Hitter");

            GauntletHooks.OnContact(creature, member);

            Assert.AreEqual(0, owner.Contacts.Count);
        }

        [TestMethod]
        public void CreatureVsCreature_NoCredit()
        {
            var owner = new SinkOwner();
            var a = MakeCreature(owner, 1);
            var b = MakeCreature(owner, 1);

            GauntletHooks.OnContact(a, b);

            Assert.AreEqual(0, owner.Contacts.Count);
        }

        /// <summary>
        /// The consumer pin: a real DamageHistory.Add on a Gauntlet creature, with a player attacker, reaches the run's sink
        /// with the creature's wave, for a landed hit and for an absorbed (amount 0) one. Mutation: remove the OnContact line
        /// from either World Event entry point and the matching half fails (GauntletContactHookSiteTests pins the text).
        /// </summary>
        [TestMethod]
        public void RealDamageHistoryAdd_ReachesOnMemberContact_WithTheCreaturesWave()
        {
            var owner = new SinkOwner();
            var creature = MakeCreature(owner, 9);
            var member = MakePlayer("Hitter");

            creature.DamageHistory.Add(member, DamageType.Slash, 25);

            Assert.AreEqual(1, owner.Contacts.Count, "a damaging hit (OnDamageRecorded)");
            Assert.AreSame(member, owner.Contacts[0].Member);
            Assert.AreEqual(9, owner.Contacts[0].Wave);

            creature.DamageHistory.Add(member, DamageType.Slash, 0);

            Assert.AreEqual(2, owner.Contacts.Count, "an absorbed hit (OnCombatContact)");
            Assert.AreEqual(9, owner.Contacts[1].Wave);
        }

        private sealed class OtherHold : IEphemeralHold
        {
            public TimeSpan? UnloadIntervalOverride => null;
        }

        [TestMethod]
        public void IsGauntletHold_OnlyForTheGauntletArenaHold()
        {
            Assert.IsTrue(GauntletHooks.IsGauntletHold(new GauntletArenaHold(_ => true, TimeSpan.FromHours(1))));
            Assert.IsFalse(GauntletHooks.IsGauntletHold(new OtherHold()));
            Assert.IsFalse(GauntletHooks.IsGauntletHold(null));
            Assert.IsFalse(GauntletHooks.InGauntletCopy(null));
            Assert.IsFalse(GauntletHooks.InGauntletCopy(MakePlayer("Nowhere")), "a player on no landblock is in no copy");
        }
    }
}
