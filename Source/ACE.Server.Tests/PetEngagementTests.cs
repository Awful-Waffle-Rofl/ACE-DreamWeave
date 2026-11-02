using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom Summons Task 3, the combat primitives: PetEngagement.IsEngagedWithOwner (what "already in combat"
    /// means for a ranged pet), the never-initiates target filter a flagged pet applies to its own picks and to
    /// its multishot extra targets, the Tank's creature-sourced taunt pulse (run through the real
    /// Creature.ApplyTaunt, not a decider alone), and the weapon multishot arrow list a pet's volley is built from.
    ///
    /// A Player cannot be constructed in this harness, so owners are RuntimeHelpers.GetUninitializedObject
    /// Players: nothing here reads a Player field or calls a Player method, the owner is only ever compared
    /// by reference. Reads class_ability_taunt_radius_r3 and class_ability_taunt_duration, seeded from the code
    /// defaults in ClassInitialize; the one test that moves the radius restores it in a finally.
    /// </summary>
    [TestClass]
    public class PetEngagementTests
    {
        private static uint nextGuid = 0x7F5B0000;
        private static uint nextWcid = 995500;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();

            DefaultPropertyManager.LoadDefaultProperties();
            ResetTauntKeys();
        }

        private static void ResetTauntKeys()
        {
            PropertyManager.ModifyDouble("class_ability_taunt_radius_r3",
                DefaultPropertyManager.DefaultDoubleProperties["class_ability_taunt_radius_r3"].Item);
            PropertyManager.ModifyDouble("class_ability_taunt_duration",
                DefaultPropertyManager.DefaultDoubleProperties["class_ability_taunt_duration"].Item);
        }

        private static Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> Vitals() =>
            new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> { [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 } };

        private static Player Owner() => (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

        private static Position At(float x) => new Position(0x00010064, x, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

        private static CombatPet Pet(Player owner, bool neverInitiates = false, double tauntInterval = 0.0, float x = 50.0f)
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
            pet.Location = At(x);
            return pet;
        }

        private static Creature Monster(WorldObject attackTarget, float x = 55.0f, Creature.State state = Creature.State.Awake)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Monster" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = Vitals(),
            };

            var monster = new Creature(weenie, new ObjectGuid(nextGuid++));
            monster.Location = At(x);
            monster.AttackTarget = attackTarget;
            monster.MonsterState = state;
            monster.IsAwake = state == Creature.State.Awake;
            return monster;
        }

        // ---- IsEngagedWithOwner --------------------------------------------------------------------------

        [TestMethod]
        public void Engaged_WhenTheMonsterTargetsTheOwner()
        {
            var owner = Owner();

            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(Monster(owner), owner));
        }

        [TestMethod]
        public void Engaged_WhenTheMonsterTargetsASiblingPet()
        {
            var owner = Owner();
            var sibling = Pet(owner);

            Assert.IsTrue(PetEngagement.IsEngagedWithOwner(Monster(sibling), owner), "a sibling pet's fight is the owner's fight");
        }

        [TestMethod]
        public void NotEngaged_WhenTheMonsterIsIdle()
        {
            var owner = Owner();

            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(Monster(null, state: Creature.State.Idle), owner));
        }

        [TestMethod]
        public void NotEngaged_WhenTheMonsterFightsAnotherPlayerOrTheirPet()
        {
            var owner = Owner();
            var stranger = Owner();

            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(Monster(stranger), owner), "another player's target");
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(Monster(Pet(stranger)), owner), "another player's pet's target");
        }

        [TestMethod]
        public void NotEngaged_WithNoOwnerOrNoMonster()
        {
            var owner = Owner();

            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(null, owner));
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(Monster(owner), null));
            Assert.IsFalse(PetEngagement.IsEngagedWithOwner(Monster(Pet(null)), null), "an ownerless pet never matches a null owner");
        }

        // ---- the never-initiates target filter ---------------------------------------------------------

        [TestMethod]
        public void NeverInitiatesPet_AcceptsOnlyEngagedMonsters()
        {
            var owner = Owner();
            var archer = Pet(owner, neverInitiates: true);

            Assert.IsTrue(archer.NeverInitiates);
            Assert.IsTrue(archer.AcceptsPetTarget(Monster(owner), archer.NeverInitiates), "engaged on the owner");
            Assert.IsTrue(archer.AcceptsPetTarget(Monster(archer), archer.NeverInitiates), "engaged on the archer itself");
            Assert.IsFalse(archer.AcceptsPetTarget(Monster(null, state: Creature.State.Idle), archer.NeverInitiates), "an idle monster is never started on");
            Assert.IsFalse(archer.AcceptsPetTarget(Monster(Owner()), archer.NeverInitiates), "another player's fight is not joined");
        }

        [TestMethod]
        public void UnflaggedPet_KeepsTodaysFilter_IdleMonstersStayValid()
        {
            // retail parity: a pet without SummonNeverInitiates (Tank, Melee, every retail pet) still takes an
            // idle monster, exactly as GetNearbyMonsters always has
            var owner = Owner();
            var tank = Pet(owner);

            Assert.IsFalse(tank.NeverInitiates);
            Assert.IsTrue(tank.AcceptsPetTarget(Monster(null, state: Creature.State.Idle), tank.NeverInitiates));
            Assert.IsTrue(tank.AcceptsPetTarget(Monster(Owner()), tank.NeverInitiates));
        }

        [TestMethod]
        public void PetCheckMonsters_NeverInitiatesPetWakesNothing()
        {
            // through the real method: a bare test pet has no PhysicsObj, so the waking scan cannot run here.
            // The flagged pet returns before it; the unflagged control proves the scan is genuinely reached
            // (and so that the guard, not an accident of the fixture, is what stops the flagged one).
            var archer = Pet(Owner(), neverInitiates: true);
            archer.PetCheckMonsters();

            var tank = Pet(Owner());
            Assert.ThrowsExactly<System.NullReferenceException>(() => tank.PetCheckMonsters(),
                "an unflagged pet runs today's waking scan");
        }

        [TestMethod]
        public void MultiShotExtraTargets_ExcludeIdleMonsters_ForANeverInitiatesPet()
        {
            var owner = Owner();
            var archer = Pet(owner, neverInitiates: true);

            Assert.IsTrue(archer.AcceptsMultiShotExtraTarget(Monster(owner)));
            Assert.IsTrue(archer.AcceptsMultiShotExtraTarget(Monster(Pet(owner))), "engaged on a sibling pet");
            Assert.IsFalse(archer.AcceptsMultiShotExtraTarget(Monster(null, state: Creature.State.Idle)), "an idle monster in the cone is never woken by an extra arrow");
            Assert.IsFalse(archer.AcceptsMultiShotExtraTarget(Monster(Owner())));
        }

        [TestMethod]
        public void MultiShotExtraTargets_UnflaggedPetAndPlainCreature_AcceptIdleMonsters()
        {
            var idle = Monster(null, state: Creature.State.Idle);

            Assert.IsTrue(Pet(Owner()).AcceptsMultiShotExtraTarget(idle), "an unflagged pet keeps the pet target filter only");
            Assert.IsTrue(Monster(null).AcceptsMultiShotExtraTarget(idle), "the Creature default adds no filter (the player path is unchanged)");
        }

        [TestMethod]
        public void MultiShotExtraTargets_PetNeverShootsADeadOrUnattackableCreature()
        {
            var owner = Owner();
            var pet = Pet(owner);

            var dead = Monster(owner);
            dead.Health.Current = 0;

            var unattackable = Monster(owner);
            unattackable.Attackable = false;

            Assert.IsFalse(pet.AcceptsMultiShotExtraTarget(dead));
            Assert.IsFalse(pet.AcceptsMultiShotExtraTarget(unattackable));
        }

        // ---- the taunt pulse, through the real ApplyTaunt -------------------------------------------------

        [TestMethod]
        public void TauntPulse_TauntsEngagedMonstersInRadius_NeverIdleOrForeignOrDistant()
        {
            var owner = Owner();
            var tank = Pet(owner, tauntInterval: 10.0);
            var sibling = Pet(owner);

            var onOwner = Monster(owner, x: 60.0f);
            var onSibling = Monster(sibling, x: 70.0f);
            var idle = Monster(null, x: 55.0f, state: Creature.State.Idle);
            var foreign = Monster(Owner(), x: 56.0f);

            var radius = TauntAbility.RadiusForRank(TauntAbility.TopRank);
            var distant = Monster(owner, x: 50.0f + (float)radius + 5.0f);

            var taunted = tank.ApplyTauntPulse(new[] { onOwner, onSibling, idle, foreign, distant }, m => true);

            Assert.AreEqual(2, taunted);

            Assert.AreSame(tank, onOwner.TauntTarget);
            Assert.AreSame(tank, onOwner.AttackTarget, "the taunt pulls the monster off the owner");
            Assert.AreSame(tank, onSibling.TauntTarget);

            Assert.IsNull(idle.TauntTarget);
            Assert.AreEqual(Creature.State.Idle, idle.MonsterState, "an idle monster is never woken by the pulse");
            Assert.IsNull(idle.AttackTarget);

            Assert.IsNull(foreign.TauntTarget, "another player's fight is left alone");
            Assert.IsNull(distant.TauntTarget, "beyond the rank-3 Taunt radius");
        }

        [TestMethod]
        public void TauntPulse_HoldsForThePlayerTauntBaseDuration()
        {
            var owner = Owner();
            var tank = Pet(owner, tauntInterval: 10.0);
            var monster = Monster(owner, x: 52.0f);

            tank.ApplyTauntPulse(new[] { monster }, m => true);

            var hold = monster.TauntExpiry - Timers.RunningTime;
            var expected = TauntAbility.EffectiveDuration(PropertyManager.GetDouble("class_ability_taunt_duration").Item);

            Assert.AreEqual(expected, hold, 0.05, "the pet reuses the player Taunt's hold, with no gear term");
        }

        [TestMethod]
        public void TauntPulse_RadiusIsReadFromTheRank3Tunable_NotACopy()
        {
            var owner = Owner();
            var tank = Pet(owner, tauntInterval: 10.0);
            var at15 = Monster(owner, x: 65.0f);

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble("class_ability_taunt_radius_r3", 12.0));
                Assert.AreEqual(0, tank.ApplyTauntPulse(new[] { at15 }, m => true), "15 m is outside a 12 m rank-3 radius");

                Assert.IsTrue(PropertyManager.ModifyDouble("class_ability_taunt_radius_r3", 20.0));
                Assert.AreEqual(1, tank.ApplyTauntPulse(new[] { at15 }, m => true), "and inside a 20 m one");
            }
            finally
            {
                ResetTauntKeys();
            }
        }

        [TestMethod]
        public void TauntPulse_SkipsWhatTheReachProbeRefuses()
        {
            // the probe is the world-touching half (same landblock group, line of sight); a refusal must
            // leave the monster untouched
            var owner = Owner();
            var tank = Pet(owner, tauntInterval: 10.0);
            var monster = Monster(owner, x: 52.0f);

            Assert.AreEqual(0, tank.ApplyTauntPulse(new[] { monster }, m => false));
            Assert.IsNull(monster.TauntTarget);
            Assert.AreSame(owner, monster.AttackTarget);
        }

        [TestMethod]
        public void TauntPulseClock_FiresEveryInterval_AndNeverWithoutOne()
        {
            var tank = Pet(Owner(), tauntInterval: 10.0);

            Assert.IsTrue(tank.TryConsumeTauntPulse(100.0, 10.0), "the first combat tick pulses");
            Assert.IsFalse(tank.TryConsumeTauntPulse(105.0, 10.0));
            Assert.IsFalse(tank.TryConsumeTauntPulse(109.9, 10.0));
            Assert.IsTrue(tank.TryConsumeTauntPulse(110.0, 10.0));
            Assert.IsFalse(tank.TryConsumeTauntPulse(111.0, 10.0));

            var plain = Pet(Owner());
            Assert.IsFalse(plain.TryConsumeTauntPulse(100.0, 0.0), "no interval, no pulse");
            Assert.IsFalse(plain.TryConsumeTauntPulse(1000.0, -1.0));
        }

        [TestMethod]
        public void ApplyTaunt_AcceptsACreatureTaunter()
        {
            var owner = Owner();
            var tank = Pet(owner);
            var monster = Monster(owner);

            monster.ApplyTaunt(tank, 5.0);

            Assert.AreSame(tank, monster.TauntTarget);
            Assert.AreSame(tank, monster.AttackTarget);
            Assert.AreEqual(5.0, monster.TauntExpiry - Timers.RunningTime, 0.05);
        }

        // ---- [?] probe: a dual-wield pet's wield selection and stance -----------------------------------

        private static WorldObject Sword(EquipMask validLocations, bool autoWieldLeft)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)validLocations },
                    { PropertyInt.DefaultCombatStyle, (int)CombatStyle.OneHanded },
                },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Sword" } },
            };

            if (autoWieldLeft)
                weenie.PropertiesBool[PropertyBool.AutowieldLeft] = true;

            return new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void DualWieldPet_WieldsBothSwords_AndTakesTheDualWieldStance()
        {
            // the selection Creature's constructor runs on a create-list Wield item (GenerateWieldList ->
            // EquipInventoryItems), driven here with hand-made items because the create list itself resolves
            // wcids through the world database
            var pet = Pet(Owner());
            pet.ItemCapacity = 10;
            pet.AiAllowedCombatStyle = CombatStyle.DualWield;

            Assert.IsTrue(pet.TryAddToInventory(Sword(EquipMask.MeleeWeapon, autoWieldLeft: false)));
            Assert.IsTrue(pet.TryAddToInventory(Sword(EquipMask.Shield, autoWieldLeft: true)));

            pet.EquipInventoryItems();

            Assert.IsNotNull(pet.GetEquippedMeleeWeapon(forceMainHand: true), "main hand");
            Assert.IsNotNull(pet.GetDualWieldWeapon(), "off hand");
            Assert.AreEqual(MotionStance.DualWieldCombat, pet.GetCombatStance(), "the stance SetCombatMode(Melee) switches to");
        }

        [TestMethod]
        public void PetWithoutTheDualWieldStyle_WieldsOneSword()
        {
            var pet = Pet(Owner());
            pet.ItemCapacity = 10;

            Assert.IsTrue(pet.TryAddToInventory(Sword(EquipMask.MeleeWeapon, autoWieldLeft: false)));
            Assert.IsTrue(pet.TryAddToInventory(Sword(EquipMask.Shield, autoWieldLeft: true)));

            pet.EquipInventoryItems();

            Assert.IsNull(pet.GetDualWieldWeapon(), "without AiAllowedCombatStyle DualWield the off-hand stays in the pack");
            Assert.AreEqual(MotionStance.SwordCombat, pet.GetCombatStance());
        }

        [TestMethod]
        public void PetWithNoItemCapacity_RefusesItsCreateListWeapon()
        {
            // GenerateWieldList adds each create-list Wield item to inventory first and DESTROYS it when that
            // add fails, so a pet weenie without ItemsCapacity never wields its weapon at all
            var pet = Pet(Owner());

            Assert.IsFalse(pet.TryAddToInventory(Sword(EquipMask.MeleeWeapon, autoWieldLeft: false)));
        }

        // ---- multishot arrow list --------------------------------------------------------------------

        private static WorldObject Bow(bool multiShot, int? count = null, double? mult = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MissileLauncher,
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Bow" } },
            };

            if (multiShot)
                weenie.PropertiesBool[PropertyBool.MultiShot] = true;
            if (count.HasValue)
                weenie.PropertiesInt[PropertyInt.MultiShotCount] = count.Value;
            if (mult.HasValue)
                weenie.PropertiesFloat[PropertyFloat.MultiShotDamageMultiplier] = mult.Value;

            return new MissileLauncher(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void WeaponMultiShot_TwoExtraArrows_EachAtTheWeaponMultiplier()
        {
            var shots = Creature.WeaponMultiShotMultipliers(Bow(true, 2, 0.6));

            Assert.AreEqual(2, shots.Count, "the Archer's +2 Multishot: 3 arrows per shot");
            Assert.IsTrue(shots.All(m => System.Math.Abs(m - 0.6f) < 1e-6f));
        }

        [TestMethod]
        public void WeaponMultiShot_NoFlag_NoExtraArrows()
        {
            Assert.AreEqual(0, Creature.WeaponMultiShotMultipliers(Bow(false, 2, 0.6)).Count, "a count without the flag fires nothing extra");
            Assert.AreEqual(0, Creature.WeaponMultiShotMultipliers(null).Count);
        }

        // ---- wiring pins (source scans: these call sites need a live landblock, physics and a target) ---------

        private static string Src(string file) => ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/" + file);

        private static string Body(string file, string signature) => ThreadDungeons.PooledLootSourceText.MethodBody(Src(file), signature);

        [TestMethod]
        public void Pin_PetVolley_RoutesThroughTheSharedVolley_WithNoPinRoll()
        {
            var body = Body("Creature_Missile.cs", "internal void FireWeaponMultiShot(");

            StringAssert.Contains(body, "FireMultiShotVolley(launcher, ammo, weapon, primary, projectileSpeed, shotMultipliers, PrimaryInSight);",
                "the pet fires the same volley (targets, overflow, sight gate) the player does");
            Assert.IsFalse(body.Contains("volleyPinRoll") || body.Contains("RollClassAbilityVolleyPin"),
                "Pinning Shot is a player class ability: a pet volley carries no pin roll");
            StringAssert.Contains(body, "if (primary == null || primary is Player || !primary.IsAlive)", "never at a player primary");
        }

        [TestMethod]
        public void Pin_MultiShotTargets_ApplyTheShootersExtraTargetFilter()
        {
            var body = Body("Creature_Missile.cs", "public List<Creature> GetMultiShotTargets(");

            var filter = body.IndexOf("if (!AcceptsMultiShotExtraTarget(creature))", System.StringComparison.Ordinal);
            var add = body.IndexOf("multiShotTargets.Add(creature);", System.StringComparison.Ordinal);

            Assert.IsTrue(filter >= 0, "GetMultiShotTargets must consult AcceptsMultiShotExtraTarget, or a never-initiates pet's extra arrows wake idle monsters");
            Assert.IsTrue(add > filter, "the filter runs before a candidate is taken");
        }

        [TestMethod]
        public void Pin_MonsterMissile_PetOnlyMultiShot_AndTheReleaseGuard()
        {
            var body = Body("Monster_Missile.cs", "public void LaunchMissile()");

            var guard = body.IndexOf("if (this is CombatPet shooter && !shooter.MayLooseAt(target))", System.StringComparison.Ordinal);
            var launch = body.IndexOf("LaunchProjectile(launcher, ammo, target, origin, orientation, velocity);", System.StringComparison.Ordinal);
            var gate = body.IndexOf("if (projectile != null && this is CombatPet && target is Creature primary)", System.StringComparison.Ordinal);
            var fan = body.IndexOf("FireWeaponMultiShot(launcher, ammo, weapon, primary, projectileSpeed);", System.StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "a never-initiates pet must hold fire at a target that left combat during the wind-up");
            Assert.IsTrue(launch > guard, "the release guard runs before the real arrow is loosed");
            Assert.IsTrue(gate >= 0, "the extra-arrow fan is gated to combat pets: a monster never fans");
            Assert.IsTrue(fan > gate, "FireWeaponMultiShot sits under the CombatPet gate");
            Assert.AreEqual(1, CountOf(body, "FireWeaponMultiShot("), "one call, the gated one");
        }

        [TestMethod]
        public void Pin_HandleFindTarget_CurrentTargetHeldOnlyWhileEngaged()
        {
            var body = Body("CombatPet.cs", "public override void HandleFindTarget()");

            StringAssert.Contains(body, "&& PetEngagement.HoldsCurrentTarget(neverInitiates, current, P_PetOwner);",
                "currentValid carries the engagement term");
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            var i = 0;
            while ((i = text.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }

        // ---- HoldsCurrentTarget / MayLooseAt -------------------------------------------------------------

        [TestMethod]
        public void HoldsCurrentTarget_FlaggedPetDropsADisengagedTarget_UnflaggedKeepsIt()
        {
            var owner = Owner();

            var engaged = Monster(owner);
            var turnedAway = Monster(Owner());
            var wentHome = Monster(null, state: Creature.State.Return);

            Assert.IsTrue(PetEngagement.HoldsCurrentTarget(true, engaged, owner));
            Assert.IsFalse(PetEngagement.HoldsCurrentTarget(true, turnedAway, owner), "turned on another player");
            Assert.IsFalse(PetEngagement.HoldsCurrentTarget(true, wentHome, owner), "went home");

            Assert.IsTrue(PetEngagement.HoldsCurrentTarget(false, turnedAway, owner), "an unflagged pet keeps today's rule");
            Assert.IsTrue(PetEngagement.HoldsCurrentTarget(false, wentHome, owner));
        }

        [TestMethod]
        public void MayLooseAt_FlaggedPetHoldsFireAtATargetThatLeftCombat()
        {
            var owner = Owner();
            var archer = Pet(owner, neverInitiates: true);

            Assert.IsTrue(archer.MayLooseAt(Monster(owner)));
            Assert.IsFalse(archer.MayLooseAt(Monster(null, state: Creature.State.Return)), "went Return during the wind-up");
            Assert.IsFalse(archer.MayLooseAt(null));

            var tank = Pet(owner);
            Assert.IsTrue(tank.MayLooseAt(Monster(null, state: Creature.State.Return)), "an unflagged pet always may");
        }

        [TestMethod]
        public void CombatRoleFlags_AreCachedFromTheWeenie()
        {
            var archer = Pet(Owner(), neverInitiates: true, tauntInterval: 10.0);

            Assert.IsTrue(archer.NeverInitiates);
            Assert.AreEqual(10.0, archer.SummonTauntInterval, 1e-9);

            // the cache is taken at construction: a later property write does not move it
            archer.SetProperty(PropertyBool.SummonNeverInitiates, false);
            Assert.IsTrue(archer.NeverInitiates);

            var plain = Pet(Owner());
            Assert.IsFalse(plain.NeverInitiates);
            Assert.AreEqual(0.0, plain.SummonTauntInterval, 1e-9);
        }

        // ---- taunt pulse: the player Taunt's own exclusions ------------------------------------------------

        [TestMethod]
        public void TauntPulse_SkipsTeleportingAndSceneryCreatures()
        {
            var owner = Owner();
            var tank = Pet(owner, tauntInterval: 10.0);

            var teleporting = Monster(owner, x: 52.0f);
            teleporting.Teleporting = true;

            var scenery = Monster(owner, x: 53.0f);
            scenery.Attackable = false;
            scenery.RemoveProperty(PropertyInt.TargetingTactic);

            var control = Monster(owner, x: 54.0f);

            Assert.AreEqual(1, tank.ApplyTauntPulse(new[] { teleporting, scenery, control }, m => true));
            Assert.IsNull(teleporting.TauntTarget);
            Assert.IsNull(scenery.TauntTarget);
            Assert.AreSame(tank, control.TauntTarget);

            Assert.IsFalse(PetEngagement.IsTauntPulseCandidate(teleporting, owner, 2.0, 65.0));
            Assert.IsFalse(PetEngagement.IsTauntPulseCandidate(scenery, owner, 3.0, 65.0));
        }

        [TestMethod]
        public void TauntPulse_UnattackableWithATactic_IsStillACandidate()
        {
            // mirrors TauntAbility.Activate exactly: only "not attackable AND no tactic" is excluded
            var owner = Owner();
            var monster = Monster(owner);
            monster.Attackable = false;
            // set directly: the TargetingTactic setter writes the OLD value (Monster_Awareness.cs, pre-existing)
            monster.SetProperty(PropertyInt.TargetingTactic, (int)TargetingTactic.Random);
            Assert.AreEqual(TargetingTactic.Random, monster.TargetingTactic);

            Assert.IsTrue(PetEngagement.IsTauntPulseCandidate(monster, owner, 2.0, 65.0));
        }
    }
}
