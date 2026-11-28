using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Plan C Task 11: the simulator prediction made at spawn (<see cref="TrialPrediction"/>), the player-side source that feeds it,
    /// and the wiring that carries it to the spawn event and the run row. Creature fixtures are live Creatures built from
    /// in-memory weenies (the CombatSimulatorCreatureSpecsTests way). The parity test takes its expected values from calling the
    /// simulators directly; it never calls TrialPrediction for them. No PropertyManager key is read beyond the compiled defaults
    /// that creature construction reads (ClassInitialize loads them).
    /// </summary>
    [TestClass]
    public class TrialPredictionTests
    {
        private static uint nextGuid = 0x7F1B0001;
        private static uint nextWcid = 0x7F1B8001;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        // ---- fixtures --------------------------------------------------------------------------------------

        private static PropertiesBodyPart Part(int dval, int armor) => new PropertiesBodyPart
        {
            DType = DamageType.Slash, DVal = dval, DVar = 0.5f, BaseArmor = armor,
            MLF = 1.0f, MRF = 1.0f, MLB = 1.0f, MRB = 1.0f, HLF = 1.0f, HRF = 1.0f, LLF = 1.0f, LRF = 1.0f,
        };

        private static Weenie CreatureWeenie(Dictionary<CombatBodyPart, PropertiesBodyPart> parts, uint maxHealth = 4000) => new Weenie
        {
            WeenieClassId = nextWcid++,
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 260 },
            PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
            {
                [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 250 },
                [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = 220 },
            },
            PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
            {
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = maxHealth },
            },
            PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
            {
                [Skill.UnarmedCombat] = new PropertiesSkill { InitLevel = 380, SAC = SkillAdvancementClass.Trained },
                [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = 300, SAC = SkillAdvancementClass.Trained },
            },
            PropertiesBodyPart = parts,
        };

        private static Creature Build(Weenie weenie)
        {
            var c = new Creature(weenie, new ObjectGuid(nextGuid++));
            Creature.SetBodyPartTable(c.WeenieClassId, new BodyPartTable(weenie));
            return c;
        }

        /// <summary>A shaped-style melee creature: one combat body part with a damage value and armor, weighted in every quadrant.</summary>
        private static Creature MeleeCreature() => Build(CreatureWeenie(new Dictionary<CombatBodyPart, PropertiesBodyPart> { [CombatBodyPart.Chest] = Part(dval: 120, armor: 300) }));

        private static Creature NoAttackCreature() => Build(CreatureWeenie(new Dictionary<CombatBodyPart, PropertiesBodyPart> { [CombatBodyPart.Chest] = Part(dval: 0, armor: 300) }));

        private static Creature Archer()
        {
            var archer = Build(CreatureWeenie(new Dictionary<CombatBodyPart, PropertiesBodyPart> { [CombatBodyPart.Chest] = Part(dval: 5, armor: 300) }));
            archer.SetProperty(PropertyInt.Level, 260);

            var bowWeenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MissileLauncher,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileWeapon },
                    { PropertyInt.DefaultCombatStyle, (int)CombatStyle.Bow },
                    { PropertyInt.WeaponSkill, (int)Skill.MissileWeapons },
                    { PropertyInt.AmmoType, (int)AmmoType.Arrow },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageMod, 2.0 } },
            };

            var arrowWeenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Ammunition,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileAmmo },
                    { PropertyInt.Damage, 30 },
                    { PropertyInt.DamageType, (int)DamageType.Pierce },
                    { PropertyInt.AmmoType, (int)AmmoType.Arrow },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } },
            };

            var bow = new MissileLauncher(bowWeenie, new ObjectGuid(nextGuid++));
            var arrows = new Ammunition(arrowWeenie, new ObjectGuid(nextGuid++));

            foreach (var item in new WorldObject[] { bow, arrows })
            {
                archer.EquippedObjects[item.Guid] = item;
                item.Wielder = archer;
            }

            return archer;
        }

        /// <summary>The master's side, hand-built: a melee attack on the creature and a defender profile for the creature's swings.</summary>
        private static TrialPlayerSide Side(string note = "fixture")
        {
            var armor = new Dictionary<ArmorKey, ArmorRow>();

            foreach (var part in Enum.GetValues<BodyPart>())
                armor[new ArmorKey(part, DamageType.Slash, false, false)] = new ArmorRow(400.0f);

            var defense = new DefenderProfile
            {
                Label = "master",
                MaxHealth = 30000,
                DefenseSkills = new Dictionary<CombatType, uint> { { CombatType.Melee, 350u }, { CombatType.Missile, 320u } },
                Armor = armor,
                Resistance = new Dictionary<DamageType, ResistancePair>
                {
                    { DamageType.Slash, new ResistancePair(1.0f, 1.0f) },
                    { DamageType.Pierce, new ResistancePair(1.0f, 1.0f) },
                },
                Shield = new Dictionary<ShieldKey, ShieldRow>(),
                DamageResistRatingBase = new Dictionary<CombatType, float> { { CombatType.Melee, 1.0f }, { CombatType.Missile, 1.0f } },
                BattleHardenedMultiplier = 1.0f,
            };

            var attack = new AttackerSpec
            {
                Label = "master attack",
                CombatType = CombatType.Melee,
                DamageType = DamageType.Slash,
                AttackHeight = AttackHeight.Medium,
                AttackSkill = 420,
                BaseDamageMin = 90.0f,
                BaseDamageMax = 180.0f,
                CritChance = 0.1f,
                CritDamageMod = 2.0f,
                PreMitigationMod = 1.5f,
            };

            return new TrialPlayerSide(attack, defense, note);
        }

        // ---- the prediction --------------------------------------------------------------------------------

        [TestMethod]
        public void Prediction_equals_direct_simulator_calls_for_a_shaped_fixture()
        {
            var creature = MeleeCreature();
            var side = Side();

            var prediction = TrialPrediction.For(creature, side);

            // Expected values: the simulators called directly, on the same inputs, never through TrialPrediction.
            var onCreature = MonsterCombatSimulator.Analytic(side.Attack, MonsterDefenderProfile.Build(creature));
            var onPlayer = ACE.Server.CombatSimulator.CombatSimulator.Analytic(CreatureAttackerSpecs.Melee(creature), side.Defense);

            Assert.IsNotNull(prediction);
            Assert.AreEqual("melee", prediction.Mode);
            Assert.AreEqual(onCreature.LandedHitsToKill, prediction.PlayerHitsToKill);
            Assert.AreEqual(onPlayer.LandedHitsToKill, prediction.CreatureHitsToKillPlayer);
            Assert.AreEqual(onPlayer.HitChance, prediction.CreatureHitChance);
            Assert.AreEqual("fixture", prediction.Note);

            // Guards against a vacuous comparison: the three numbers are real, finite and distinct from each other.
            Assert.IsTrue(float.IsFinite(prediction.PlayerHitsToKill) && prediction.PlayerHitsToKill > 0);
            Assert.IsTrue(float.IsFinite(prediction.CreatureHitsToKillPlayer) && prediction.CreatureHitsToKillPlayer > 0);
            Assert.IsTrue(prediction.CreatureHitChance > 0 && prediction.CreatureHitChance < 1, $"hit chance {prediction.CreatureHitChance}");
            Assert.AreNotEqual(prediction.PlayerHitsToKill, prediction.CreatureHitsToKillPlayer);
        }

        [TestMethod]
        public void A_ranged_creature_predicts_in_missile_mode_from_the_missile_spec()
        {
            var archer = Archer();
            var side = Side();

            var prediction = TrialPrediction.For(archer, side);

            var onPlayer = ACE.Server.CombatSimulator.CombatSimulator.Analytic(CreatureAttackerSpecs.Missile(archer), side.Defense);

            Assert.IsNotNull(prediction);
            Assert.AreEqual("missile", prediction.Mode, "the archer also has a combat body part, but it fires");
            Assert.AreEqual(onPlayer.LandedHitsToKill, prediction.CreatureHitsToKillPlayer);
            Assert.AreEqual(onPlayer.HitChance, prediction.CreatureHitChance);
        }

        [TestMethod]
        public void Combat_mode_is_restored()
        {
            foreach (var make in new Func<Creature>[] { MeleeCreature, Archer })
            {
                var creature = make();
                var before = creature.CombatMode;
                var stanceBefore = creature.CurrentMotionState.Stance;
                var swapBefore = creature.LastWeaponSwap;

                Assert.AreEqual(CombatMode.NonCombat, before, "guard: a fresh creature starts out of stance");
                Assert.AreEqual(MotionStance.NonCombat, stanceBefore, "guard: a fresh creature is out of motion stance");

                // Positive control: a bare spec call leaves the creature in a combat mode AND a combat motion stance (and, for
                // the archer, moves LastWeaponSwap), so the unchanged values below are what the prediction preserved.
                var probe = make();
                _ = probe.GetEquippedMissileWeapon() != null ? CreatureAttackerSpecs.Missile(probe) : CreatureAttackerSpecs.Melee(probe);
                Assert.AreNotEqual(CombatMode.NonCombat, probe.CombatMode, "guard: the spec call changes the combat mode");
                Assert.AreNotEqual(MotionStance.NonCombat, probe.CurrentMotionState.Stance, "guard: the spec call changes the motion stance");

                var prediction = TrialPrediction.For(creature, Side());

                Assert.IsNotNull(prediction);
                Assert.AreEqual(before, creature.CombatMode);
                Assert.AreEqual(MotionStance.NonCombat, creature.CurrentMotionState.Stance, "the spawned creature is left out of motion stance, so DoAttackStance still fires");
                Assert.AreEqual(swapBefore, creature.LastWeaponSwap);
            }
        }

        [TestMethod]
        public void No_attack_gives_null_with_a_note()
        {
            var creature = NoAttackCreature();

            var prediction = TrialPrediction.For(creature, Side(), out var note);

            Assert.IsNull(prediction);
            Assert.IsFalse(string.IsNullOrWhiteSpace(note));
            Assert.IsTrue(note.Contains(creature.WeenieClassId.ToString()), note);
            Assert.AreEqual(CombatMode.NonCombat, creature.CombatMode);
            Assert.IsNull(TrialPrediction.For(creature, Side()));
        }

        [TestMethod]
        public void No_player_side_gives_null_with_a_note()
        {
            var prediction = TrialPrediction.For(MeleeCreature(), null, out var note);

            Assert.IsNull(prediction);
            Assert.IsFalse(string.IsNullOrWhiteSpace(note));
        }

        [TestMethod]
        public void Json_key_set_is_closed()
        {
            var json = new TrialPrediction(12.5f, 3.25f, 0.75f, "melee", "a note").ToJson();

            using var doc = JsonDocument.Parse(json);
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

            CollectionAssert.AreEquivalent(new[] { "mode", "playerHitsToKill", "creatureHitsToKillPlayer", "creatureHitChance", "playerSource", "note" }, keys);
            Assert.AreEqual("master_offline", doc.RootElement.GetProperty("playerSource").GetString());
            Assert.AreEqual("melee", doc.RootElement.GetProperty("mode").GetString());
            Assert.AreEqual(12.5, doc.RootElement.GetProperty("playerHitsToKill").GetDouble());
            Assert.AreEqual(3.25, doc.RootElement.GetProperty("creatureHitsToKillPlayer").GetDouble());
            Assert.AreEqual(0.75, doc.RootElement.GetProperty("creatureHitChance").GetDouble());
            Assert.AreEqual("a note", doc.RootElement.GetProperty("note").GetString());
            Assert.IsTrue(json.Length < 1024, "fits trial_event.pred_json");
        }

        [TestMethod]
        public void Json_writes_a_non_finite_number_as_null_and_the_column_as_null()
        {
            var json = new TrialPrediction(float.PositiveInfinity, 3.0f, 0.5f, "melee", null).ToJson();

            using var doc = JsonDocument.Parse(json);

            Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("playerHitsToKill").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("note").ValueKind);
            Assert.IsNull(TrialPrediction.Column(float.PositiveInfinity));
            Assert.AreEqual(3.0, TrialPrediction.Column(3.0f));
        }

        // ---- the player-side source ------------------------------------------------------------------------

        private sealed class Harness
        {
            public readonly List<Action<TrialPlayerSide>> Callbacks = new List<Action<TrialPlayerSide>>();
            public readonly Queue<Action> Room = new Queue<Action>();
            public double Clock;
            public int Loads;
            public TrialPlayerSideSource Source;

            public Harness(bool synchronous = false, Func<TrialPlayerSide> answer = null)
            {
                Source = new TrialPlayerSideSource(0x50000001, () => Clock, (guid, cb) =>
                {
                    Loads++;
                    Callbacks.Add(cb);

                    if (synchronous)
                        cb(answer());
                }, a => { Room.Enqueue(a); return true; });
            }

            public void RunRoom()
            {
                while (Room.Count > 0)
                    Room.Dequeue()();
            }
        }

        [TestMethod]
        public void The_side_arrives_through_the_room_thread_only()
        {
            var h = new Harness();
            var side = Side();

            h.Source.Request();
            Assert.IsNull(h.Source.Side, "pending: no side yet");

            h.Callbacks[0](side);
            Assert.IsNull(h.Source.Side, "the callback only queues: nothing is stored until the room thread runs it");
            Assert.AreEqual(1, h.Room.Count);

            h.RunRoom();
            Assert.AreSame(side, h.Source.Side);
        }

        [TestMethod]
        public void A_synchronous_answer_is_enqueued_not_stored_inline()
        {
            var side = Side();
            var h = new Harness(synchronous: true, answer: () => side);

            h.Source.Request();
            Assert.IsNull(h.Source.Side);

            h.RunRoom();
            Assert.AreSame(side, h.Source.Side);
        }

        [TestMethod]
        public void A_second_request_while_in_flight_does_not_load_again_until_the_timeout()
        {
            var h = new Harness();

            h.Source.Request();
            h.Source.Request();
            Assert.AreEqual(1, h.Loads, "in-flight guard");

            h.Clock = TrialPlayerSideSource.TimeoutSeconds + 1.0;
            h.Source.Request();
            Assert.AreEqual(2, h.Loads, "a load past the timeout counts as dead");

            // The first, late answer is still good.
            var side = Side();
            h.Callbacks[0](side);
            h.RunRoom();
            Assert.AreSame(side, h.Source.Side);

            h.Source.Request();
            Assert.AreEqual(2, h.Loads, "ready: nothing more to load");
        }

        [TestMethod]
        public void A_null_answer_leaves_no_side_and_never_blocks()
        {
            var h = new Harness();

            h.Source.Request();
            h.Callbacks[0](null);
            h.RunRoom();

            Assert.IsNull(h.Source.Side, "failed: spawns run with no prediction");

            // A creature spawned now gets no prediction (rep.PlayerSide is null).
            Assert.IsNull(TrialPrediction.For(MeleeCreature(), h.Source.Side));
        }

        // ---- wiring ----------------------------------------------------------------------------------------

        [TestMethod]
        public void Spawn_predicts_after_Prepare_before_EnterWorld_and_emits_the_event_only_after_EnterWorld()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialSpawner.cs");
            var spawn = PooledLootSourceText.MethodBody(source, TrialSpawnerTests.SpawnSignature);

            var prepare = spawn.IndexOf("Prepare(creature, plan, e, observedMaxNonBoss, rep, tag);", StringComparison.Ordinal);
            var predict = spawn.IndexOf("var pred = Predict(creature, e, rep, tag);", StringComparison.Ordinal);
            var enter = spawn.IndexOf("creature.EnterWorld()", StringComparison.Ordinal);
            var emit = spawn.IndexOf("EmitSpawn(e, rep, pred, tag);", StringComparison.Ordinal);

            Assert.IsTrue(prepare >= 0 && predict > prepare && enter > predict && emit > enter, $"{prepare} {predict} {enter} {emit}");
            Assert.AreEqual(1, spawn.Split("EmitSpawn(").Length - 1, "one emit");
            Assert.IsTrue(spawn.IndexOf("return creature;", StringComparison.Ordinal) > emit, "the event is emitted on the success path");

            var emitBody = PooledLootSourceText.MethodBody(source, "private static void EmitSpawn(DungeonSpawnPlanEntry e, TrialRep rep, TrialPrediction pred, string tag)");
            Assert.IsTrue(emitBody.Contains("rep?.Recorder?.OnSpawn(e.Wcid, e.Role, pred?.ToJson(), TrialManager.Now())"));

            // A refused boss leaves pred_* null: BossPrediction is assigned on the EnterWorld success path (EmitSpawn) and nowhere else.
            Assert.IsTrue(emitBody.Contains("rep.BossPrediction = pred;"));
            Assert.IsFalse(PooledLootSourceText.MethodBody(source, "private static TrialPrediction Predict(Creature creature, DungeonSpawnPlanEntry e, TrialRep rep, string tag)").Contains("BossPrediction"));
            Assert.AreEqual(1, source.Split("BossPrediction =").Length - 1);
        }

        [TestMethod]
        public void The_boss_prediction_fills_the_three_run_columns_and_no_prediction_leaves_them_null()
        {
            TrialSession Session() => new TrialSession("u", null, null, null, null, null, 1, null, n => null);

            var rep = new TrialRep();
            var none = TrialRunLog.Facts(Session(), rep);
            Assert.IsNull(none.PredPlayerHitsToKillBoss);
            Assert.IsNull(none.PredBossHitsToKillPlayer);
            Assert.IsNull(none.PredBossHitChance);

            rep.BossPrediction = new TrialPrediction(12.5f, 3.25f, 0.75f, "melee", null);
            var facts = TrialRunLog.Facts(Session(), rep);
            Assert.AreEqual(12.5, facts.PredPlayerHitsToKillBoss);
            Assert.AreEqual(3.25, facts.PredBossHitsToKillPlayer);
            Assert.AreEqual(0.75, facts.PredBossHitChance);
        }
    }
}
