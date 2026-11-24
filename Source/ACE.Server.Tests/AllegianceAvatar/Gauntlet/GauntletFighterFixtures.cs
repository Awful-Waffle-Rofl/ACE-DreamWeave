using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Factories;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Dat-free builders for the Avatar fighter tests: a donor shaped like a retail CombatPet weenie (plus every field
    /// BuildFighterWeenie must strip), fighters and monsters with a bare PhysicsObj (no cell, so CurInstance resolves to
    /// KnownInstance, the PetMultiShotCandidatesTests seam), and a GauntletRun driven by a stepped clock with the real
    /// LiveGauntletFighterHooks behind seams. No PropertyManager key is read by anything built here.
    /// </summary>
    internal static class GauntletFighterFixtures
    {
        public const uint InstanceA = 0x0001_0064;
        public const uint InstanceB = 0x0002_0064;

        // dynamic guid range, so the persistence pin sees a dynamic object
        private static uint nextGuid = 0x8F5C0000;
        private static uint nextWcid = 995900;

        public static ObjectGuid NextGuid() => new ObjectGuid(System.Threading.Interlocked.Increment(ref nextGuid));

        public static void EnsureInitialized()
        {
            TestGameTables.EnsureInitialized();
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        public static Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> Vitals(uint health = 100) =>
            new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd> { [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = health } };

        public static Position At(float x, uint instance) =>
            new Position(0x00010064, x, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, instance);

        public static T WithPhysics<T>(T wo, uint instance) where T : WorldObject
        {
            var physicsObj = new PhysicsObj
            {
                ID = wo.Guid.Full,
                Position = new ACE.Server.Physics.Common.Position(wo.Location),
                KnownInstance = instance,
            };

            physicsObj.WeenieObj = new WeenieObject(wo);

            var prop = typeof(WorldObject).GetProperty("PhysicsObj");
            Assert.IsNotNull(prop, "WorldObject.PhysicsObj was not found by reflection - has it been renamed?");
            prop.SetValue(wo, physicsObj);

            return wo;
        }

        /// <summary>
        /// A donor shaped like a retail CombatPet weenie, plus every field BuildFighterWeenie must strip. The create list is
        /// only for the pure shaping pin: a constructed creature with a Wield row runs GenerateWieldList, which reads
        /// PropertyManager, so the constructed fixtures use a donor without one. Two skills and one body part let the restamp
        /// pins see a write.
        /// </summary>
        public static Weenie Donor(bool withCreateList = false, uint health = 100) => new Weenie
        {
            WeenieClassId = nextWcid++,
            ClassName = "fighter-donor",
            WeenieType = WeenieType.CombatPet,
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Donor Pet" } },
            PropertiesBool = new Dictionary<PropertyBool, bool>
            {
                { PropertyBool.Attackable, true },
                { PropertyBool.SummonNeverInitiates, true },
                { PropertyBool.SummonCasterBrain, true },
            },
            PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Lifespan, 43 }, { PropertyInt.Level, 100 } },
            PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.DeathTreasureType, 4 } },
            PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.SummonTauntInterval, 6.0 } },
            PropertiesCreateList = !withCreateList ? null : new List<PropertiesCreateList>
            {
                new PropertiesCreateList { WeenieClassId = 1001, DestinationType = DestinationType.Wield },
                new PropertiesCreateList { WeenieClassId = 1002, DestinationType = DestinationType.WieldTreasure },
                new PropertiesCreateList { WeenieClassId = 1003, DestinationType = DestinationType.Contain },
                new PropertiesCreateList { WeenieClassId = 1004, DestinationType = DestinationType.Treasure },
            },
            PropertiesAttribute2nd = Vitals(health),
            PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
            {
                [Skill.LightWeapons] = new PropertiesSkill { InitLevel = 10, SAC = SkillAdvancementClass.Trained },
                [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = 10, SAC = SkillAdvancementClass.Trained },
            },
            PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = new PropertiesBodyPart { DType = DamageType.Slash, DVal = 20, BaseArmor = 50 },
                [CombatBodyPart.Chest] = new PropertiesBodyPart { DType = DamageType.Slash, DVal = 10, BaseArmor = 50 },
            },
        };

        public static GauntletAvatarFighter Fighter(uint instance = InstanceA, float x = 50.0f, uint health = 100)
        {
            var weenie = GauntletAvatarFighter.BuildFighterWeenie(Donor(health: health), GauntletAvatarFighter.FighterName);

            var fighter = WorldObjectFactory.CreateWorldObject(weenie, NextGuid()) as GauntletAvatarFighter;
            Assert.IsNotNull(fighter, "the factory arm must build a GauntletAvatarFighter from the fighter weenie");

            fighter.Location = At(x, instance);
            return WithPhysics(fighter, instance);
        }

        public static Creature Monster(uint instance = InstanceA, float x = 55.0f)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Monster" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = Vitals(),
            };

            var monster = new Creature(weenie, NextGuid());
            monster.Location = At(x, instance);
            return WithPhysics(monster, instance);
        }

        /// <summary>A player's ordinary combat pet (retail shape). Its owner does not enter the admission or damage rules.</summary>
        public static CombatPet PlayerPet(uint instance = InstanceA, float x = 45.0f)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Player Pet" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = Vitals(),
            };

            var pet = WorldObjectFactory.CreateWorldObject(weenie, NextGuid()) as CombatPet;
            Assert.IsNotNull(pet);
            Assert.IsNotInstanceOfType(pet, typeof(GauntletAvatarFighter));

            pet.Location = At(x, instance);
            return WithPhysics(pet, instance);
        }

        /// <summary>Bare, never written to: Player.CanDamage must not read the attacker at all.</summary>
        public static Player BarePlayer() => (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

        /// <summary>A band standard with one attack skill, one defense skill and two attributes, for the restamp pins.</summary>
        public static DungeonBandStandard Standard() => DungeonBandStandard.ForTest(
            new Dictionary<Skill, uint> { [Skill.LightWeapons] = 400, [Skill.MeleeDefense] = 300, [Skill.MissileDefense] = 250 },
            maxBodyDamage: 90, maxBaseArmor: 200,
            attributeMedians: new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = 200, [PropertyAttribute.Endurance] = 180 });

        /// <summary>An enchantment row, beneficial or harmful, straight onto the registry (no Spell, so no dats).</summary>
        public static PropertiesEnchantmentRegistry Enchant(Creature c, int spellId, bool beneficial, double duration = 1800)
        {
            var row = new PropertiesEnchantmentRegistry
            {
                SpellId = spellId,
                LayerId = 1,
                Duration = duration,
                StatModType = beneficial ? EnchantmentTypeFlags.Beneficial | EnchantmentTypeFlags.Skill : EnchantmentTypeFlags.Skill,
                StatModKey = (uint)Skill.MeleeDefense,
                StatModValue = beneficial ? 20 : -20,
            };

            c.Biota.PropertiesEnchantmentRegistry ??= new List<PropertiesEnchantmentRegistry>();
            c.Biota.PropertiesEnchantmentRegistry.AddEnchantment(row, c.BiotaDatabaseLock);
            return row;
        }

        public static bool HasEnchantment(Creature c, int spellId) =>
            c.Biota.PropertiesEnchantmentRegistry?.Clone(c.BiotaDatabaseLock).Any(e => e.SpellId == spellId) == true;
    }

    /// <summary>
    /// A real GauntletRun on a stepped clock, its world faked, and the real <see cref="LiveGauntletFighterHooks"/> installed
    /// with its world seams replaced: queued actions run on <see cref="Drain"/>, a placement always succeeds and counts,
    /// fighters come from <see cref="GauntletFighterFixtures.Fighter"/>.
    /// </summary>
    internal sealed class FighterRunHarness
    {
        public const uint AvatarId = 4200;
        public const uint Monarch = 0x50000042;
        public const uint MemberGuid = 0x500000A1;

        public DateTime Now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        public GauntletTuning Tuning = GauntletTuning.Defaults;
        public int AvatarLevel = 10;
        public readonly List<Action> Queue = new List<Action>();
        public readonly List<string> Lines = new List<string>();
        public readonly List<GauntletAvatarFighter> Placed = new List<GauntletAvatarFighter>();
        public readonly List<(GauntletAvatarFighter, uint)> Dressed = new List<(GauntletAvatarFighter, uint)>();
        public readonly List<bool> DressedBeforePlace = new List<bool>();
        public readonly List<GauntletAvatarFighter> Destroyed = new List<GauntletAvatarFighter>();
        public readonly List<int> PlanWaves = new List<int>();

        /// <summary>The next this many placements are refused.</summary>
        public int FailPlacements;

        /// <summary>The death-pen holds and deaths the fighter's IsDown reads.</summary>
        public Func<uint, bool> Held = _ => false;
        public Func<Player, bool> Dead = _ => false;
        public Func<int, GauntletWavePlan> Plans;
        public readonly LiveGauntletFighterHooks Hooks;
        public readonly GauntletManager Manager;
        public List<GauntletOccupant> Inside = new List<GauntletOccupant> { new GauntletOccupant(MemberGuid, null) };

        public FighterRunHarness()
        {
            Hooks = new LiveGauntletFighterHooks
            {
                Create = () => GauntletFighterFixtures.Fighter(health: 1000),
                Dress = (f, avatarId) => Dressed.Add((f, avatarId)),
                Enqueue = (run, action) => { Queue.Add(action); return true; },
                Place = f => { DressedBeforePlace.Add(Dressed.Exists(d => ReferenceEquals(d.Item1, f))); if (FailPlacements > 0) { FailPlacements--; return false; } Placed.Add(f); return true; },
                AvatarLevel = _ => AvatarLevel,
                Destroy = f => Destroyed.Add(f),
                IsHeld = g => Held(g),
                IsDead = p => Dead(p),
            };

            var site = GauntletFixtureSites.Site(GauntletFixtureSites.SixPortalRow());

            Manager = new GauntletManager(new GauntletEnvironment
            {
                UtcNow = () => Now,
                Tuning = () => Tuning,
                Sites = () => new[] { site },
                World = new World(this),
                Store = new Store(),
                Spawner = new Spawner(),
                Favor = new Favor(),
                Records = new Records(),
                Planner = (t, s, wave, headcount, seed, label) => { PlanWaves.Add(wave); return (Plans ?? DefaultPlan)(wave); },
                Hooks = new GauntletRunHooks { Fighter = Hooks },
                MemberLookup = _ => _ => true,
                Random = new Random(5),
            });
        }

        public GauntletRun Start()
        {
            Assert.IsTrue(Manager.TryStart(new GauntletStartRequest
            {
                AvatarId = AvatarId,
                MonarchGuid = Monarch,
                BuyerGuid = MemberGuid,
                Price = 3000,
                NowUtc = Now,
            }, out var refusal), refusal);

            return Manager.FindByAvatar(AvatarId);
        }

        public void Drain()
        {
            while (Queue.Count > 0)
            {
                var batch = Queue.ToList();
                Queue.Clear();
                foreach (var a in batch) a();
            }
        }

        public void Advance(double seconds)
        {
            var end = Now.AddSeconds(seconds);

            while (Now < end)
            {
                Now = Now.AddSeconds(0.5);
                if (Now > end) Now = end;
                Manager.TickAt(Now);
                Drain();
            }
        }

        public GauntletRun StartActive()
        {
            var run = Start();
            Advance(Tuning.StagingSeconds + 0.5);
            Assert.AreEqual(GauntletState.Active, run.State, "fixture: the run is Active after staging");
            return run;
        }

        public static GauntletWavePlan DefaultPlan(int wave) => Plan(wave, GauntletFighterFixtures.Standard(), 2000, 50);

        /// <summary>A one-trash wave at L(n) carrying the fields the fighter reads: curve target, band standard, per-hit median.</summary>
        public static GauntletWavePlan Plan(int wave, DungeonBandStandard standard, double healthCurveTarget, double perHitMedian)
        {
            var rules = new GauntletRules(GauntletTuning.Defaults);
            var point = GauntletFixtureSites.At(100, 96);

            return new GauntletWavePlan
            {
                Wave = wave,
                Level = rules.LevelOf(wave),
                Headcount = 1,
                Plan = new DungeonSpawnPlan { FamilyId = "testfamily", HealthCurveTarget = healthCurveTarget, BandStandard = standard, PerHitMedian = perHitMedian },
                TimeLimitSeconds = rules.TimeLimitSeconds(wave),
                Spawns = new List<PortalWaveSpawn> { new PortalWaveSpawn(0, 2.0, new DungeonSpawnPlanEntry(0x7F1E0000, DungeonRole.Trash, point)) },
                UnspawnedHealthEstimate = new Dictionary<DungeonRole, double> { [DungeonRole.Trash] = 1000 },
            };
        }

        // ---- fakes -------------------------------------------------------------------------------------------------------

        private sealed class World : IGauntletWorld
        {
            private readonly FighterRunHarness h;
            private uint nextInstance = 0x7F020001;
            public World(FighterRunHarness h) { this.h = h; }

            public bool OpenCopy(GauntletRun run, out uint instance, out string failure) { instance = nextInstance++; failure = null; return true; }
            public bool IsCopyLoaded(uint instance) => true;
            public bool Enqueue(GauntletRun run, Action action) { h.Queue.Add(action); return true; }
            public IReadOnlyList<GauntletOccupant> Occupants(GauntletRun run) => h.Inside.ToList();
            public void TellInside(GauntletRun run, string text) => h.Lines.Add(text);
            public ICollection<uint> ToAllegiance(uint monarchGuid, string text) => new HashSet<uint>();
            public void TellInsideExcept(GauntletRun run, string text, ICollection<uint> except) => h.Lines.Add(text);
            public void TellCharacter(uint characterGuid, string text) { }
            public void EvictAndRelease(GauntletRun run, string line) { }
        }

        private sealed class Store : IGauntletStore
        {
            private uint next = 801;
            public void InsertRun(AllegianceGauntletRun row, Action<bool, uint> callback) => callback(true, next++);
            public void SaveRunEnd(AllegianceGauntletRun row) { }
            public void SaveWave(AllegianceGauntletWave row) { }
            public bool CloseOpenRuns(DateTime nowUtc, out int closed) { closed = 0; return true; }
        }

        private sealed class Creature : IGauntletSpawnedCreature
        {
            public uint Guid { get; init; }
            public uint SpawnMax => 1000;
            public bool IsAlive => true;
            public double SampleDealt() => 0;
            public long ActivityStamp() => 1;
            public void Engage() { }
            public bool PullTo(Position centre) => true;
            public void Destroy() { }
        }

        private sealed class Spawner : IGauntletSpawnSink
        {
            private uint next = 0x7F2E0001;
            public uint ObservedMaxNonBoss(GauntletWavePlan wave) => 1000;
            public IGauntletSpawnedCreature Spawn(GauntletWavePlan wave, PortalWaveSpawn spawn, uint observedMaxNonBoss, GauntletRun owner) => new Creature { Guid = next++ };
        }

        private sealed class Favor : IGauntletFavor
        {
            public void Commit(uint avatarId, ulong price) { }
            public void Release(uint avatarId, ulong price) { }
            public bool RefundCommitted(uint avatarId, ulong price) => true;
        }

        private sealed class Records : IGauntletRecordSink
        {
            public long SnapshotTop() => 0;
            public bool Bank(uint avatarId, long scoreCenti, int participants, uint? runId, DateTime nowUtc) => true;
            public bool TryAnnounce(uint runId, uint avatarId, uint? monarchGuid, long scoreCenti, long snapshotTop, int fought) => false;
            public void NoteRunCommitted(uint avatarId, DateTime nowUtc) { }
            public DateTime? LastRunAt(uint avatarId) => null;
        }
    }
}
