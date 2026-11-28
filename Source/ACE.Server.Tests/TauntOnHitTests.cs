using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Vanguard Taunt's on-hit hold (2026-11-25): a struck monster is taunted onto the owner for
    /// class_ability_taunt_on_hit_duration, without shortening a longer hold of the owner's and without
    /// overriding another taunter's live hold. Player's static initializer cannot run under the test host, so the
    /// owner is a plain Creature through the explicit-duration overload that the Player overload forwards to.
    /// </summary>
    [TestClass]
    public class TauntOnHitTests
    {
        private static uint nextGuid = 0x7F5D0000;
        private static uint nextWcid = 995800;

        private static readonly Landblock SpawnedLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        private static readonly string[] Keys =
        {
            "class_ability_taunt_on_hit_duration",
            "class_ability_taunt_duration",
            "monster_effect_taunt_hold_floor",
        };

        private static readonly Dictionary<string, double> Saved = new Dictionary<string, double>();

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            Saved.Clear();
            foreach (var key in Keys)
            {
                Saved[key] = PropertyManager.GetDouble(key).Item;
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
            }
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (var kv in Saved)
                PropertyManager.ModifyDouble(kv.Key, kv.Value);
        }

        private static Creature NewCreature(string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 },
                },
            };

            var c = new Creature(weenie, new ObjectGuid(nextGuid++));
            c.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            c.CurrentLandblock = SpawnedLandblock;
            c.MonsterState = Creature.State.Awake;
            c.IsAwake = true;
            return c;
        }

        // Already targeting the taunter so ApplyTaunt skips the new-enemy emote path (no EmoteManager in the harness).
        private static Creature Monster(Creature alreadyTargeting)
        {
            var m = NewCreature("Monster");
            m.AttackTarget = alreadyTargeting;
            return m;
        }

        [TestMethod]
        public void DefaultDuration_IsFifteenSeconds()
        {
            Assert.AreEqual(15.0, TauntAbility.OnHitDuration());
        }

        [TestMethod]
        public void OnHit_SetsTauntTargetAndAFifteenSecondExpiry()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);

            var before = Timers.RunningTime;
            Assert.IsTrue(TauntAbility.TauntOnHit(owner, monster, TauntAbility.OnHitDuration()));
            var after = Timers.RunningTime;

            Assert.AreSame(owner, monster.TauntTarget);
            Assert.IsTrue(monster.TauntExpiry >= before + 15.0 - 0.001 && monster.TauntExpiry <= after + 15.0 + 0.001,
                $"expiry {monster.TauntExpiry} not ~now+15");
        }

        [TestMethod]
        public void ExtendOnly_DoesNotShortenALongerHoldFromTheSameTaunter()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);
            Assert.IsTrue(monster.ApplyTaunt(owner, 60.0));
            var longExpiry = monster.TauntExpiry;

            Assert.IsTrue(monster.ApplyTaunt(owner, 15.0, extendOnly: true), "still held by the owner");

            Assert.AreEqual(longExpiry, monster.TauntExpiry, "the longer hold is untouched");
        }

        [TestMethod]
        public void ExtendOnly_ExtendsAShorterHoldFromTheSameTaunter()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);
            Assert.IsTrue(monster.ApplyTaunt(owner, 3.0));
            var shortExpiry = monster.TauntExpiry;

            Assert.IsTrue(monster.ApplyTaunt(owner, 15.0, extendOnly: true));

            Assert.IsTrue(monster.TauntExpiry > shortExpiry + 10.0);
        }

        [TestMethod]
        public void WithoutExtendOnly_ASameTaunterReapplyStillShortens()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);
            monster.ApplyTaunt(owner, 60.0);

            monster.ApplyTaunt(owner, 15.0);

            Assert.IsTrue(monster.TauntExpiry < Timers.RunningTime + 20.0, "existing callers keep the refresh-to-new-expiry behaviour");
        }

        [TestMethod]
        public void AnotherTaunters_LiveHold_IsNotOverridden()
        {
            var owner = NewCreature("Owner");
            var rival = NewCreature("Rival");
            var monster = Monster(rival);
            Assert.IsTrue(monster.ApplyTaunt(rival, 10.0));
            var expiry = monster.TauntExpiry;

            Assert.IsFalse(TauntAbility.TauntOnHit(owner, monster, 15.0));

            Assert.AreSame(rival, monster.TauntTarget);
            Assert.AreEqual(expiry, monster.TauntExpiry);
        }

        [TestMethod]
        public void ZeroOrNegativeDuration_DisablesTheOnHitTaunt()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);

            Assert.IsFalse(TauntAbility.TauntOnHit(owner, monster, 0.0));
            Assert.IsFalse(TauntAbility.TauntOnHit(owner, monster, -5.0));

            Assert.IsNull(monster.TauntTarget);
        }

        [TestMethod]
        public void TunableZero_ReadThroughOnHitDuration_DisablesIt()
        {
            PropertyManager.ModifyDouble("class_ability_taunt_on_hit_duration", 0.0);
            var owner = NewCreature("Owner");
            var monster = Monster(owner);

            Assert.IsFalse(TauntAbility.TauntOnHit(owner, monster, TauntAbility.OnHitDuration()));
            Assert.IsNull(monster.TauntTarget);
        }

        [TestMethod]
        public void DeadAndNullTargets_AreSkipped()
        {
            var owner = NewCreature("Owner");
            var dead = Monster(owner);
            dead.Health.Current = 0;

            Assert.IsFalse(TauntAbility.TauntOnHit(owner, dead, 15.0));
            Assert.IsNull(dead.TauntTarget);
            Assert.IsFalse(TauntAbility.TauntOnHit(owner, null, 15.0));
            Assert.IsFalse(TauntAbility.TauntOnHit((Creature)null, Monster(owner), 15.0));
        }

        // Player's static initializer cannot run under the test host, but an uninitialized instance is enough here
        // (PetEngagementTests.Owner does the same): the hooks only pass it through as the taunter reference.
        private static Player UninitPlayer() => (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

        private static CombatPet NewPet()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Pet" } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 },
                },
            };

            var pet = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            pet.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            pet.CurrentLandblock = SpawnedLandblock;
            return pet;
        }

        [TestMethod]
        public void CombatPetTarget_IsSkipped()
        {
            var owner = NewCreature("Owner");
            var pet = NewPet();
            pet.MonsterState = Creature.State.Awake;
            pet.IsAwake = true;
            pet.AttackTarget = owner;

            Assert.IsFalse(TauntAbility.TauntOnHit(owner, pet, 15.0));
            Assert.IsNull(pet.TauntTarget);
        }

        [TestMethod]
        public void PlayerTarget_IsSkipped()
        {
            var owner = NewCreature("Owner");
            var victim = UninitPlayer();

            Assert.IsFalse(TauntAbility.TauntOnHit(owner, victim, 15.0));
        }

        [TestMethod]
        public void ModifyOutgoingDamage_TauntsTheStruckTargetOntoTheAttacker()
        {
            var attacker = UninitPlayer();
            var struck = Monster(attacker);
            var bystander = Monster(attacker);

            new TauntAbility().ModifyOutgoingDamage(attacker, 1, struck, null);

            Assert.AreSame(attacker, struck.TauntTarget);
            Assert.IsTrue(struck.TauntExpiry > Timers.RunningTime + 14.0);
            Assert.IsNull(bystander.TauntTarget, "only the struck target");
        }

        [TestMethod]
        public void OnSpellHit_TauntsThePrimaryTargetOntoTheCaster()
        {
            var caster = UninitPlayer();
            var primary = Monster(caster);
            var other = Monster(caster);

            new TauntAbility().OnSpellHit(caster, 1, primary, null);

            Assert.AreSame(caster, primary.TauntTarget);
            Assert.IsTrue(primary.TauntExpiry > Timers.RunningTime + 14.0);
            Assert.IsNull(other.TauntTarget);
        }

        [TestMethod]
        public void BothHooks_UseTheLiveTunable()
        {
            PropertyManager.ModifyDouble("class_ability_taunt_on_hit_duration", 0.0);
            var player = UninitPlayer();
            var a = Monster(player);
            var b = Monster(player);

            new TauntAbility().ModifyOutgoingDamage(player, 1, a, null);
            new TauntAbility().OnSpellHit(player, 1, b, null);

            Assert.IsNull(a.TauntTarget);
            Assert.IsNull(b.TauntTarget);
        }

        // ---- on-hit holds and Opportunist (TauntNonOnHitExpiry) ----------------------------------------------
        // TauntQualifies takes the clock as a parameter, so "later" is evaluated rather than waited for.

        private static bool Qualifies(Creature monster, Creature owner, double now) =>
            OpportunistAbility.TauntQualifies(monster, t => ReferenceEquals(t, owner), now);

        [TestMethod]
        public void OnHitOnly_NeverQualifies()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);
            var t0 = Timers.RunningTime;

            Assert.IsTrue(TauntAbility.TauntOnHit(owner, monster, 15.0));

            Assert.AreEqual(0.0, monster.TauntNonOnHitExpiry);
            Assert.IsFalse(Qualifies(monster, owner, t0 + 1.0));
            Assert.IsFalse(Qualifies(monster, owner, t0 + 10.0));
        }

        [TestMethod]
        public void AoeThenOnHitOneSecondLater_QualifiesOnlyWhileTheAoeHoldLasts()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);
            var t0 = Timers.RunningTime;

            Assert.IsTrue(monster.ApplyTaunt(owner, 11.0));
            var aoeExpiry = monster.TauntExpiry;
            Assert.IsTrue(TauntAbility.TauntOnHit(owner, monster, 15.0), "the on-hit write extends the hold past the AoE expiry");
            Assert.IsTrue(monster.TauntExpiry > aoeExpiry, "precondition: the write fell through, not the early return");

            Assert.AreEqual(aoeExpiry, monster.TauntNonOnHitExpiry, 1e-6, "the AoE backing is untouched");
            Assert.IsTrue(Qualifies(monster, owner, t0 + 5.0), "inside the AoE window");
            Assert.IsFalse(Qualifies(monster, owner, aoeExpiry + 1.0), "past the AoE window, even though the on-hit hold is still live");
            Assert.IsTrue(aoeExpiry + 1.0 < monster.TauntExpiry);
        }

        [TestMethod]
        public void OnHitThenAoeOverwrite_Qualifies()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);
            var t0 = Timers.RunningTime;
            Assert.IsTrue(TauntAbility.TauntOnHit(owner, monster, 15.0));

            // Hunter's Echo nulls the owner's hold first and re-applies as a plain ApplyTaunt: the plain write is what counts.
            Assert.IsTrue(monster.ApplyTaunt(owner, 11.0));

            Assert.IsTrue(Qualifies(monster, owner, t0 + 5.0));
        }

        [TestMethod]
        public void LongerAoeHoldKeptByTheEarlyReturn_Qualifies()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);
            var t0 = Timers.RunningTime;
            Assert.IsTrue(monster.ApplyTaunt(owner, 60.0));
            var expiry = monster.TauntExpiry;

            Assert.IsTrue(TauntAbility.TauntOnHit(owner, monster, 15.0));

            Assert.AreEqual(expiry, monster.TauntExpiry, "precondition: the early return kept the hold");
            Assert.IsTrue(Qualifies(monster, owner, t0 + 30.0));
        }

        [TestMethod]
        public void AnotherTaunters_OnHit_AfterAnExpiredAoeHold_DoesNotQualify()
        {
            var a = NewCreature("A");
            var b = NewCreature("B");
            var monster = Monster(a);
            var t0 = Timers.RunningTime;
            Assert.IsTrue(monster.ApplyTaunt(a, -5.0), "an AoE hold that has already expired");

            Assert.IsTrue(TauntAbility.TauntOnHit(b, monster, 15.0));

            Assert.AreSame(b, monster.TauntTarget);
            Assert.AreEqual(0.0, monster.TauntNonOnHitExpiry, "the stale AoE backing is not inherited");
            Assert.IsFalse(Qualifies(monster, b, t0 + 1.0));
            Assert.IsFalse(Qualifies(monster, a, t0 + 1.0));
        }

        [TestMethod]
        public void Opportunist_AoeHold_StillQualifies_AndAForeignTaunterDoesNot()
        {
            var owner = NewCreature("Owner");
            var monster = Monster(owner);
            var t0 = Timers.RunningTime;
            monster.ApplyTaunt(owner, 11.0);

            Assert.IsTrue(Qualifies(monster, owner, t0 + 1.0));
            Assert.IsFalse(OpportunistAbility.TauntQualifies(monster, t => false, t0 + 1.0));
            Assert.IsFalse(Qualifies(monster, owner, t0 + 12.0), "expired like before");
        }

        // ---- World Events Hold ------------------------------------------------------------------------------

        [TestMethod]
        public void HoldTaggedMonster_IsNotTauntedOnHit()
        {
            var owner = NewCreature("Owner");
            var seeker = Monster(owner);
            Assert.IsTrue(seeker.SetWorldEventHoldTag(new ACE.Server.WorldEvents.Hold.WorldEventHoldTag
            {
                RunId = 1,
                Kind = ACE.Server.WorldEvents.Hold.HoldUnitKind.Monster,
                Seeker = true,
            }));

            Assert.IsFalse(TauntAbility.TauntOnHit(owner, seeker, 15.0));
            Assert.IsNull(seeker.TauntTarget);

            Assert.IsTrue(TauntAbility.TauntOnHit(owner, Monster(owner), 15.0), "a plain monster is still taunted");
        }

        [TestMethod]
        public void Registry_DispatchesTauntOnBothNewHooks()
        {
            Assert.IsTrue(ClassAbilityRegistry.OutgoingDamageAbilities.OfType<TauntAbility>().Any(), "melee/missile hook");
            Assert.IsTrue(ClassAbilityRegistry.SpellHitAbilities.OfType<TauntAbility>().Any(), "spell projectile hook");
            Assert.IsTrue(ClassAbilityRegistry.ItemProcAbilities.OfType<TauntAbility>().Any(), "AoE proc hook still registered");
        }
    }
}
