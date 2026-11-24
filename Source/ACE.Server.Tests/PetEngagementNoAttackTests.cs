using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// 2026-11-15 (Discord bug report 1570812629348393113): never-initiates summons (the Archer, every caster) never attacked
    /// the Matron Knot's Hardened Hive Rock (1007554, Tolerance NoAttack). PetEngagement.IsEngagedWithOwner only knew the
    /// monster's AttackTarget, and a NoAttack creature never takes one. A creature that never attacks now counts as engaged
    /// with an owner for 60 s after that owner, or one of the owner's pets, last damaged it (recorded in DamageHistory.Add;
    /// the time-window test drives the clock explicitly through the internal overloads). Every test
    /// has a no-damage or normal-monster control so the old rule is pinned alongside the new one.
    ///
    /// Owners are RuntimeHelpers.GetUninitializedObject Players (a Player cannot be constructed here) with a distinct guid
    /// written through an UnsafeAccessor (FieldInfo.SetValue on a Player runs its type initializer and poisons the process).
    /// </summary>
    [TestClass]
    public class PetEngagementNoAttackTests
    {
        private static uint nextGuid = 0x7F5C0000;
        private static uint nextWcid = 995700;
        private static uint nextOwnerGuid = 0x50000100;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        // the awake-rock pulse test reaches CombatPet.ApplyTauntPulse, which may read the taunt keys: seeded from the code
        // defaults here (as PetEngagementTests does), so the class passes alone and in any order
        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();

            DefaultPropertyManager.LoadDefaultProperties();
            PropertyManager.ModifyDouble("class_ability_taunt_radius_r3", DefaultPropertyManager.DefaultDoubleProperties["class_ability_taunt_radius_r3"].Item);
            PropertyManager.ModifyDouble("class_ability_taunt_duration", DefaultPropertyManager.DefaultDoubleProperties["class_ability_taunt_duration"].Item);
        }

        private static Position At(float x) => new Position(0x00010064, x, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

        // a stand-in for "spawned in a landblock": the taunt bookkeeping only tests CurrentLandblock for null
        private static readonly Landblock SpawnedLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        private static Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> Vitals() =>
            new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> { [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 } };

        private static Player Owner()
        {
            var owner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            GuidField(owner) = new ObjectGuid(nextOwnerGuid++);
            return owner;
        }

        private static CombatPet Pet(Player owner, bool neverInitiates, double tauntInterval = 0.0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Pet" } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesAttribute2nd = Vitals(),
            };

            if (neverInitiates)
                weenie.PropertiesBool[PropertyBool.SummonNeverInitiates] = true;

            if (tauntInterval > 0.0)
                weenie.PropertiesFloat[PropertyFloat.SummonTauntInterval] = tauntInterval;

            var pet = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            pet.P_PetOwner = owner;
            return pet;
        }

        /// <summary>A creature like 1007554 Hardened Hive Rock: attackable, Tolerance NoAttack, idle, no AttackTarget.</summary>
        private static Creature Rock() => MakeCreature(Tolerance.NoAttack);

        private static Creature MakeCreature(Tolerance tolerance)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Rock" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesAttribute2nd = Vitals(),
            };

            if (tolerance != Tolerance.None)
                weenie.PropertiesInt[PropertyInt.Tolerance] = (int)tolerance;

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.MonsterState = Creature.State.Idle;
            return creature;
        }

        [TestMethod]
        public void NoAttackCreature_IsNotEngaged_UntilTheOwnerDamagesIt()
        {
            var owner = Owner();
            var rock = Rock();
            var archer = Pet(owner, neverInitiates: true);

            Assert.IsTrue(PetEngagement.NeverAttacks(rock));
            Assert.IsNull(rock.AttackTarget, "the rock never takes a target");

            // control: undamaged, exactly the old rule - not engaged, the archer neither picks nor shoots it
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(rock, owner), "no damage: not engaged");
            Assert.IsFalse(archer.AcceptsPetTarget(rock, archer.NeverInitiates));
            Assert.IsFalse(archer.MayLooseAt(rock));
            Assert.IsFalse(PetEngagement.HoldsCurrentTarget(true, rock, owner));

            PetEngagement.RecordDamage(rock, owner);

            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner), "the owner damaged it: engaged");
            Assert.IsTrue(archer.AcceptsPetTarget(rock, archer.NeverInitiates), "the archer may now pick it (nearest and assist)");
            Assert.IsTrue(archer.MayLooseAt(rock), "and loose at it");
            Assert.IsTrue(archer.AcceptsMultiShotExtraTarget(rock));
            Assert.IsTrue(PetEngagement.HoldsCurrentTarget(true, rock, owner), "and keeps it as its target");
            Assert.AreEqual(Creature.State.Idle, rock.MonsterState, "nothing woke it");
        }

        [TestMethod]
        public void NoAttackCreature_DamagedByAPetOfTheOwner_IsEngagedWithThatOwner_AndNoOtherOwner()
        {
            var owner = Owner();
            var stranger = Owner();
            var rock = Rock();

            PetEngagement.RecordDamage(rock, Pet(owner, neverInitiates: false));

            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner), "the owner's Tank or Melee pet hitting it engages it with the owner's side");
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(rock, stranger), "control: another player's pets still leave it alone");

            PetEngagement.RecordDamage(rock, stranger);
            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, stranger), "two owners can both be recorded");
            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner), "and the first one stays");
        }

        [TestMethod]
        public void NormalMonster_DamagedByTheOwner_IsStillNotEngagedWhileIdle()
        {
            // normal monsters are unchanged: damage is not engagement, only the AttackTarget is
            var owner = Owner();
            var idle = MakeCreature(Tolerance.None);

            Assert.IsFalse(PetEngagement.NeverAttacks(idle));

            PetEngagement.RecordDamage(idle, owner);

            Assert.AreEqual(0, idle.DamagedByOwners.Count, "nothing is recorded on a monster that can attack");
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(idle, owner), "an idle normal monster the owner hit is still not engaged (it engages by targeting)");

            idle.AttackTarget = owner;
            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(idle, owner), "control: the AttackTarget rule still holds");
        }

        [TestMethod]
        public void NoAttackRecord_StopsCountingIfTheCreatureLaterCanAttack()
        {
            // a creature whose NoAttack is lifted (an emote can rewrite Tolerance) is back on the normal rule at once,
            // so an archer cannot wake it on the strength of an old hit
            var owner = Owner();
            var rock = Rock();

            PetEngagement.RecordDamage(rock, owner);
            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner));

            rock.Tolerance = Tolerance.None;
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(rock, owner));
        }

        [TestMethod]
        public void NonOwnerSideAttackers_RecordNothing()
        {
            var rock = Rock();

            PetEngagement.RecordDamage(rock, MakeCreature(Tolerance.None));   // a monster
            PetEngagement.RecordDamage(rock, Pet(null, neverInitiates: false));   // an ownerless pet
            PetEngagement.RecordDamage(rock, null);
            PetEngagement.RecordDamage(null, Owner());

            Assert.AreEqual(0, rock.DamagedByOwners.Count);
        }

        [TestMethod]
        public void DamageHistoryAdd_RecordsTheOwner_AndAZeroHitDoesNot()
        {
            // through the real sink: every damaging health write records through DamageHistory.Add
            var owner = Owner();
            var archer = Pet(owner, neverInitiates: true);
            var rock = Rock();

            rock.DamageHistory.Add(archer, DamageType.Pierce, 0);
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(rock, owner), "control: a hit that removed no health (a zero Add) is not damage");

            rock.DamageHistory.Add(archer, DamageType.Pierce, 7);
            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner), "a pet's damaging hit engages the rock with its owner");
        }

        [TestMethod]
        public void TauntPulse_AnIdleDamagedRock_IsNotACandidate()
        {
            // the Tank pulse keeps needing an Awake monster: a damaged rock stays idle and is not a pulse candidate
            var owner = Owner();
            var rock = Rock();

            PetEngagement.RecordDamage(rock, owner);

            Assert.IsFalse(PetEngagement.IsTauntPulseCandidate(rock, owner, 2.0, 15.0));
        }

        [TestMethod]
        public void TauntPulse_AnAwakeDamagedRock_IsNeverTaunted()
        {
            // even if something woke it, the pulse takes no hold on a NoAttack creature: ApplyTaunt refuses the
            // pet-retaliate exclusion (NoAttack), so no slot is spent and the rock gains no target. CONTROL: a normal
            // engaged monster in the same pulse is taunted, so the pulse really ran.
            var owner = Owner();
            var tank = Pet(owner, neverInitiates: false, tauntInterval: 10.0);
            tank.Location = At(50.0f);

            var rock = Rock();
            rock.Location = At(52.0f);
            rock.CurrentLandblock = SpawnedLandblock;
            rock.MonsterState = Creature.State.Awake;
            rock.IsAwake = true;
            PetEngagement.RecordDamage(rock, owner);

            var control = MakeCreature(Tolerance.None);
            control.Location = At(54.0f);
            control.CurrentLandblock = SpawnedLandblock;
            control.MonsterState = Creature.State.Awake;
            control.IsAwake = true;
            control.AttackTarget = owner;

            Assert.IsTrue(PetEngagement.IsTauntPulseCandidate(rock, owner, 2.0, 15.0), "awake and engaged, the rock does pass the pulse's candidate rule");
            Assert.AreEqual(1, tank.ApplyTauntPulse(new[] { rock, control }, m => true), "only the control is taunted");
            Assert.IsNull(rock.TauntTarget, "the rock is never held");
            Assert.IsNull(rock.AttackTarget, "and never given a target");
            Assert.AreSame(tank, control.TauntTarget, "control: the normal monster is held");
        }

        [TestMethod]
        public void Engagement_LastsSixtySecondsAfterTheOwnersLatestHit_AndAHitRefreshesIt()
        {
            var owner = Owner();
            var rock = Rock();
            const double t0 = 1000.0;

            Assert.AreEqual(60.0, PetEngagement.NeverAttacksEngagementSeconds, 1e-9);

            PetEngagement.RecordDamage(rock, owner, t0);

            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner, t0 + 30.0), "30 s after the hit: engaged");
            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner, t0 + 60.0), "60 s: still engaged (the window is inclusive)");
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(rock, owner, t0 + 61.0), "61 s: the record has lapsed, so a target dummy hit once is not the owner's fight forever");

            PetEngagement.RecordDamage(rock, Pet(owner, neverInitiates: false), t0 + 61.0);

            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner, t0 + 61.0), "a new hit (here the owner's pet) re-engages it");
            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner, t0 + 120.0), "and restarts the window from that hit");
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(rock, owner, t0 + 122.0));

            // an out-of-order earlier time never rolls the record back
            PetEngagement.RecordDamage(rock, owner, t0);
            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(rock, owner, t0 + 120.0), "a late-arriving older hit does not shorten the window");
        }

        [TestMethod]
        public void UnattackableNoAttackCreature_StaysExcludedFromPetTargeting_AfterARecordedHit()
        {
            // scenery (NoAttack and Attackable False) is engaged after a hit - harmless on its own - but every pet target
            // path also runs IsPetTargetCandidate, which refuses an unattackable creature first
            var owner = Owner();
            var archer = Pet(owner, neverInitiates: true);
            var tank = Pet(owner, neverInitiates: false);
            var scenery = Rock();
            scenery.Attackable = false;

            PetEngagement.RecordDamage(scenery, owner);

            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(scenery, owner), "the record itself is kept");
            Assert.IsFalse(archer.AcceptsPetTarget(scenery, archer.NeverInitiates), "a never-initiates pet never picks it");
            Assert.IsFalse(archer.AcceptsMultiShotExtraTarget(scenery), "nor fans an extra arrow at it");
            Assert.IsFalse(tank.AcceptsPetTarget(scenery, tank.NeverInitiates), "nor does an unflagged pet");
        }

        [TestMethod]
        public void APlayerVictim_RecordsNothing()
        {
            // PvP: a player hit by another player (or their pet) is never a never-attacks creature
            var victim = Owner();
            var attacker = Owner();

            PetEngagement.RecordDamage(victim, attacker);
            PetEngagement.RecordDamage(victim, Pet(attacker, neverInitiates: false));

            Assert.IsFalse(PetEngagement.NeverAttacks(victim));
            Assert.AreEqual(0, victim.DamagedByOwners.Count);
        }

        [TestMethod]
        public void Pin_DamageHistoryAdd_CallsRecordDamage_AfterTheZeroReturn()
        {
            var src = ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/Entity/DamageHistory.cs");
            var body = ThreadDungeons.PooledLootSourceText.MethodBody(src, "public void Add(WorldObject attacker, DamageType damageType, uint amount)");

            var zeroReturn = body.IndexOf("if (amount == 0)", System.StringComparison.Ordinal);
            var record = body.IndexOf("PetEngagement.RecordDamage(Creature, attacker);", System.StringComparison.Ordinal);

            Assert.IsTrue(zeroReturn >= 0, "the zero-amount early return");
            Assert.IsTrue(record > zeroReturn, "RecordDamage runs only for a hit that removed health");
        }
    }
}
