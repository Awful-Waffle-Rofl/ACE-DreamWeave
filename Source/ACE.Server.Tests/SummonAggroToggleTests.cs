using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /summonaggro (PropertyBool 9097): CombatPet.NeverInitiates is the stored flag AND NOT the owner's toggle.
    /// A Player cannot be constructed in this harness, so the toggle's live read is pinned by source scan and by
    /// the pure PetEngagement.EffectiveNeverInitiates truth table; owners here are uninitialized Players that are
    /// only compared by reference. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class SummonAggroToggleTests
    {
        private static uint nextGuid = 0x7F5C0000;
        private static uint nextWcid = 995800;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();

            // VisualAwarenessRangeSq reads mob_awareness_range: seeded from the code default so the class passes alone and in any order
            DefaultPropertyManager.LoadDefaultProperties();
            PropertyManager.ModifyDouble("mob_awareness_range", DefaultPropertyManager.DefaultDoubleProperties["mob_awareness_range"].Item);
        }

        // A seeded Player: UnsafeAccessor, never FieldInfo.SetValue (that runs Player's type initializer and poisons the process)
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref ReaderWriterLockSlim BiotaLockField(WorldObject wo);

        /// <summary>A Player whose biota carries SummonAggro = <paramref name="aggro"/> (null = the property is absent).</summary>
        private static Player SeededOwner(bool? aggro)
        {
            var owner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            var biota = new Biota { PropertiesBool = new Dictionary<PropertyBool, bool>() };

            if (aggro.HasValue)
                biota.PropertiesBool[PropertyBool.SummonAggro] = aggro.Value;

            BiotaField(owner) = biota;
            BiotaLockField(owner) = new ReaderWriterLockSlim();
            return owner;
        }

        /// <summary>A seeded owner whose biota lock is disposed: ANY read of one of its properties throws ObjectDisposedException.</summary>
        private static Player TrippedOwner()
        {
            var owner = SeededOwner(true);
            BiotaLockField(owner).Dispose();
            return owner;
        }
        private static Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> Vitals() =>
            new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> { [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 } };

        private static Player Owner() => (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

        private static Position At(float x) => new Position(0x00010064, x, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

        private static CombatPet Pet(Player owner, bool neverInitiates)
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

            var pet = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            pet.P_PetOwner = owner;
            pet.Location = At(50.0f);
            return pet;
        }

        private static readonly Landblock SpawnedLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        private static Creature IdleMonster()
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
            monster.Location = At(55.0f);
            monster.CurrentLandblock = SpawnedLandblock;
            monster.AttackTarget = null;
            monster.MonsterState = Creature.State.Idle;
            monster.IsAwake = false;
            return monster;
        }

        private static string Server(string relative) => PooledLootSourceText.Read("Source/ACE.Server/" + relative);

        [TestMethod]
        public void EffectiveNeverInitiates_TruthTable()
        {
            Assert.IsTrue(PetEngagement.EffectiveNeverInitiates(true, false), "by design, toggle off: held");
            Assert.IsFalse(PetEngagement.EffectiveNeverInitiates(true, true), "by design, toggle on: released");
            Assert.IsFalse(PetEngagement.EffectiveNeverInitiates(false, false), "not by design: never held");
            Assert.IsFalse(PetEngagement.EffectiveNeverInitiates(false, true), "not by design: never held, whatever the toggle says");
        }

        [TestMethod]
        public void HoldsCurrentTarget_ToggleOnKeepsAnUnengagedTarget_ToggleOffDropsIt()
        {
            var owner = Owner();
            var idle = IdleMonster();

            Assert.IsTrue(PetEngagement.HoldsCurrentTarget(PetEngagement.EffectiveNeverInitiates(true, true), idle, owner),
                "toggle on: the pet keeps a target that is not fighting the owner");
            Assert.IsFalse(PetEngagement.HoldsCurrentTarget(PetEngagement.EffectiveNeverInitiates(true, false), idle, owner),
                "toggle off: the pet drops it on its next tick");
        }

        [TestMethod]
        public void NeverInitiates_PetWithNoOwner_IsTheStoredFlag_AndDoesNotThrow()
        {
            Assert.IsTrue(Pet(null, true).NeverInitiates, "no owner reads the toggle as off");
            Assert.IsTrue(Pet(null, true).NeverInitiatesByDesign);
            Assert.IsFalse(Pet(null, false).NeverInitiates, "a retail or Tank pet is never held");
            Assert.IsFalse(Pet(null, false).NeverInitiatesByDesign);
        }

        [TestMethod]
        public void Pin_CasterEnemyList_FollowsTheToggle_ReadOnceBeforeTheLoop()
        {
            var body = PooledLootSourceText.MethodBody(Server("WorldObjects/CombatPet_Caster.cs"), "internal void CollectCasterEnemies(");

            Assert.IsFalse(body.Contains("AcceptsPetTarget(creature, true)"), "the caster enemy collector must follow the toggle, not a literal true");
            StringAssert.Contains(body, "AcceptsPetTarget(creature, neverInitiates)");

            var read = body.IndexOf("var neverInitiates = NeverInitiates;", StringComparison.Ordinal);
            var loop = body.IndexOf("foreach (var creature in visible)", StringComparison.Ordinal);

            Assert.IsTrue(read >= 0, "the toggle is read into a local");
            Assert.IsTrue(loop > read, "...ONCE, before the loop (a per-candidate read costs the owner's biota lock each time)");
            Assert.AreEqual(1, body.Split(new[] { "NeverInitiates;" }, StringSplitOptions.None).Length - 1, "no second read of NeverInitiates in the method");
        }
        [TestMethod]
        public void Pin_HuntersEcho_TargetPickFollowsTheToggle_ReadOnceBeforeThePick()
        {
            var echo = Server("WorldObjects/CombatPet_HuntersEcho.cs");

            Assert.IsFalse(echo.Contains("AcceptsPetTarget(creature, true)"), "Hunter's Echo targeting must follow the toggle, not a literal true");

            var body = PooledLootSourceText.MethodBody(echo, "private Creature SelectHuntersEchoTarget(");
            var read = body.IndexOf("var neverInitiates = HuntersEchoNeverInitiates;", StringComparison.Ordinal);
            var pick = body.IndexOf("HuntersEchoMath.SelectTarget(", StringComparison.Ordinal);

            Assert.IsTrue(read >= 0, "the toggle is read into a local");
            Assert.IsTrue(pick > read, "...ONCE, before the pick (a per-candidate read costs the owner's biota lock each time)");
            StringAssert.Contains(body, "IsHuntersEchoTarget(creature, neverInitiates)");
            Assert.AreEqual(1, body.Split(new[] { "NeverInitiates;" }, StringSplitOptions.None).Length - 1, "no second read of the toggle in the method");
        }

        [TestMethod]
        public void HuntersEcho_TakesAnIdleMonster_OnlyWhenTheOwnerHasTheToggleOn_WhateverItsWeenieFlagSays()
        {
            foreach (var byDesignRow in new[] { true, false })
            {
                foreach (var (aggro, expected) in new[] { ((bool?)true, true), (false, false), (null, false) })
                {
                    var echo = Pet(SeededOwner(aggro), byDesignRow);
                    var idle = IdleMonster();

                    Assert.AreEqual(!expected, echo.HuntersEchoNeverInitiates, $"row {byDesignRow}, owner toggle {aggro?.ToString() ?? "absent"}: held");
                    Assert.AreEqual(expected, echo.IsHuntersEchoTarget(idle, echo.HuntersEchoNeverInitiates),
                        $"row {byDesignRow}, owner toggle {aggro?.ToString() ?? "absent"}: idle monster");
                }
            }

            // never a pet or itself, toggle on or not
            var released = Pet(SeededOwner(true), true);
            Assert.IsFalse(released.IsHuntersEchoTarget(Pet(Owner(), false), released.HuntersEchoNeverInitiates), "never a pet");
            Assert.IsFalse(released.IsHuntersEchoTarget(released, released.HuntersEchoNeverInitiates), "never itself");
        }

        [TestMethod]
        public void HuntersEcho_ToggleOff_StillTakesAMonsterEngagedWithTheOwner()
        {
            var owner = SeededOwner(false);
            var echo = Pet(owner, true);
            var engaged = IdleMonster();
            engaged.AttackTarget = owner;

            Assert.IsTrue(echo.IsHuntersEchoTarget(engaged, echo.HuntersEchoNeverInitiates), "toggle off: a monster attacking the owner is a target");
            Assert.IsFalse(echo.IsHuntersEchoTarget(IdleMonster(), echo.HuntersEchoNeverInitiates), "toggle off: an idle one is not");
        }

        [TestMethod]
        public void Pin_NeverInitiates_IsComputedFromTheOwnersToggle()
        {
            var pet = Server("WorldObjects/CombatPet.cs");

            StringAssert.Contains(pet, "public bool NeverInitiates => neverInitiatesFlag && !(P_PetOwner?.SummonAggro ?? false);");
            StringAssert.Contains(pet, "public bool NeverInitiatesByDesign => neverInitiatesFlag;");
        }

        [TestMethod]
        public void Pin_HarmfulSpellReleaseGate_SkippedOnlyForAByDesignPetWhoseOwnerHasAggroOn()
        {
            StringAssert.Contains(Server("WorldObjects/Monster_Magic.cs"),
                "if (!friendly && this is CombatPet pet && !(pet.NeverInitiatesByDesign && !pet.NeverInitiates) && !(target is Creature engaged && PetEngagement.IsEngagedWithOwner(engaged, pet.P_PetOwner)))");
        }

        // ---- seeded owners: the live toggle read -------------------------------------------------------------

        [TestMethod]
        public void SummonAggro_Getter_IsOffWhenAbsent_AndFollowsTheStoredValue()
        {
            Assert.IsFalse(SeededOwner(null).SummonAggro, "absent = off");
            Assert.IsTrue(SeededOwner(true).SummonAggro);
            Assert.IsFalse(SeededOwner(false).SummonAggro);
        }

        [TestMethod]
        public void ByDesignPet_IsReleasedOnlyByAnOwnerWithTheToggleOn()
        {
            var on = Pet(SeededOwner(true), true);
            Assert.IsFalse(on.NeverInitiates, "owner on: released");
            Assert.IsTrue(on.NeverInitiatesByDesign);

            Assert.IsTrue(Pet(SeededOwner(false), true).NeverInitiates, "owner off: held");
            Assert.IsTrue(Pet(SeededOwner(null), true).NeverInitiates, "owner absent: held");
            Assert.IsTrue(Pet(Owner(), true).NeverInitiates, "a biota-less owner reads as off");
        }

        [TestMethod]
        public void StoredFlagFalse_StaysUnrestricted_WhateverTheOwnerSays()
        {
            Assert.IsFalse(Pet(SeededOwner(true), false).NeverInitiates, "owner on, flag false");
            Assert.IsFalse(Pet(SeededOwner(false), false).NeverInitiates, "owner off, flag false: never MORE restricted");
            Assert.IsFalse(Pet(SeededOwner(null), false).NeverInitiates);
        }

        [TestMethod]
        public void StoredFlagFalse_NeverReadsTheOwner_ButAByDesignPetDoes()
        {
            var tripped = TrippedOwner();

            // positive control: the tripped owner really does throw when its property is read
            Assert.ThrowsExactly<ObjectDisposedException>(() => { var _ = tripped.SummonAggro; });
            Assert.ThrowsExactly<ObjectDisposedException>(() => { var _ = Pet(tripped, true).NeverInitiates; }, "a by-design pet reads its owner");

            // the point: a retail/Tank/Melee pet short-circuits on its stored flag and never touches the owner
            Assert.IsFalse(Pet(tripped, false).NeverInitiates);
        }

        [TestMethod]
        public void IdleMonster_MayBeLoosedAtAndMultiShotTargeted_OnlyWhenTheOwnerHasTheToggleOn()
        {
            foreach (var (aggro, expected) in new (bool?, bool)[] { (true, true), (false, false), (null, false) })
            {
                var owner = SeededOwner(aggro);
                var pet = Pet(owner, true);
                var idle = IdleMonster();

                Assert.AreEqual(expected, pet.MayLooseAt(idle), $"MayLooseAt, owner {aggro?.ToString() ?? "absent"}");
                Assert.AreEqual(expected, pet.AcceptsMultiShotExtraTarget(idle), $"AcceptsMultiShotExtraTarget, owner {aggro?.ToString() ?? "absent"}");
            }
        }

        // ---- CollectCasterEnemies ----------------------------------------------------------------------------

        private static CombatPet CasterWithOwner(bool? aggro)
        {
            var pet = Pet(SeededOwner(aggro), true);
            pet.CurrentLandblock = SpawnedLandblock;
            pet.VisualAwarenessRange = 20.0;   // set before the first read: VisualAwarenessRangeSq caches
            return pet;
        }

        private static List<Creature> Collect(CombatPet pet, Dictionary<Creature, float> distances, params Creature[] visible)
        {
            var enemies = new List<Creature>();
            pet.CollectCasterEnemies(visible, wo => distances[(Creature)wo], enemies, new List<float>());
            return enemies;
        }

        [TestMethod]
        public void CollectCasterEnemies_IncludesAnIdleMonster_OnlyWhenTheOwnerHasTheToggleOn()
        {
            foreach (var (aggro, expected) in new (bool?, bool)[] { (true, true), (false, false), (null, false) })
            {
                var pet = CasterWithOwner(aggro);
                var idle = IdleMonster();

                var near = (float)Math.Sqrt(pet.VisualAwarenessRangeSq) * 0.5f;
                var enemies = Collect(pet, new Dictionary<Creature, float> { [idle] = near }, idle);

                Assert.AreEqual(expected, enemies.Contains(idle), $"idle monster inside awareness range, owner {aggro?.ToString() ?? "absent"}");
            }
        }

        [TestMethod]
        public void CollectCasterEnemies_ToggleOn_UnengagedCandidateMustBeInsideAwarenessRange()
        {
            var pet = CasterWithOwner(true);
            var awareness = (float)Math.Sqrt(pet.VisualAwarenessRangeSq);

            Assert.IsTrue(awareness * 1.5f < Creature.MaxChaseRange, "precondition: the beyond-awareness distance is still inside MaxChaseRange");

            var idleBeyond = IdleMonster();
            var idleInside = IdleMonster();

            var enemies = Collect(pet, new Dictionary<Creature, float> { [idleBeyond] = awareness * 1.5f, [idleInside] = awareness * 0.5f }, idleBeyond, idleInside);

            CollectionAssert.AreEqual(new[] { idleInside }, enemies, "an unengaged monster beyond awareness range is not a candidate; one inside is");
        }

        [TestMethod]
        public void CollectCasterEnemies_ToggleOn_EngagedCandidateKeepsTheMaxChaseRangeBound()
        {
            var pet = CasterWithOwner(true);
            var awareness = (float)Math.Sqrt(pet.VisualAwarenessRangeSq);

            Assert.IsTrue(awareness * 1.5f < Creature.MaxChaseRange, "precondition");

            var engagedBeyondAwareness = IdleMonster();
            engagedBeyondAwareness.AttackTarget = pet.P_PetOwner;
            engagedBeyondAwareness.MonsterState = Creature.State.Awake;
            engagedBeyondAwareness.IsAwake = true;

            var engagedBeyondChase = IdleMonster();
            engagedBeyondChase.AttackTarget = pet.P_PetOwner;
            engagedBeyondChase.MonsterState = Creature.State.Awake;
            engagedBeyondChase.IsAwake = true;

            var enemies = Collect(pet,
                new Dictionary<Creature, float> { [engagedBeyondAwareness] = awareness * 1.5f, [engagedBeyondChase] = Creature.MaxChaseRange + 1.0f },
                engagedBeyondAwareness, engagedBeyondChase);

            CollectionAssert.AreEqual(new[] { engagedBeyondAwareness }, enemies, "engaged: beyond awareness but inside 96 m is kept; past 96 m is not");
        }
    }
}