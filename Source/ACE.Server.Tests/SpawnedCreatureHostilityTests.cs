using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Physics.Common;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A roster-drawn creature fights the PLAYER, not the rest of its own wave
    /// (SpawnedCreatureHostility.MakeHostileToPlayers). Shared rule: it is rule 4 of the Thread run-creature
    /// standardization and the wave/boss normalization in WorldEventSpawner.Spawn.
    ///
    /// The reported symptom, 2026-09-07: a player in a shadow-family Thread watched two "Elite Void Lord
    /// [240]" attack each other. shadow.json holds three level-240 Void Lords - wcids 43899, 72837 and 72869,
    /// all role 1 - and only 72837 carries Faction1Bits (32) and Tolerance.Monster (128), so any run that drew
    /// 72837 alongside either of the others opened with the two of them fighting. World Events draws the same
    /// species tables, so it could produce the same pair.
    ///
    /// The fixtures below are shaped from those live weenie rows (ace_world, 2026-09-07).
    /// </summary>
    [TestClass]
    public class SpawnedCreatureHostilityTests
    {
        private static uint nextWcid = 992000;
        private static uint nextGuid = 0x7D000000;

        static SpawnedCreatureHostilityTests()
        {
            // Creature's constructor reads MaxHealth, which goes through the portal-dat formula tables the
            // server injects at boot. Same synthetic tables every other DB-free creature test uses.
            TestGameTables.EnsureInitialized();
        }

        /// <summary>
        /// A minimal roster-shaped weenie. Attackable is what makes SetMonsterState treat the creature as a
        /// monster at all, and every faction/foe cache under test is gated on that.
        ///
        /// Handed out separately from <see cref="CreatureFrom"/> because ONE Weenie instance is what
        /// WorldDatabaseWithEntityCache.GetCachedWeenie returns to every CreateNewWorldObject call for a wcid
        /// (WorldDatabaseWithEntityCache.cs:99-109) - so the per-instance test below has to build both
        /// creatures from a single weenie to be testing anything.
        /// </summary>
        private static Weenie RosterWeenie(Tolerance tolerance = Tolerance.None, int? faction1 = null,
            CreatureType? foeType = null, CreatureType creatureType = CreatureType.Shadow)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CreatureType, (int)creatureType },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = 500 } },
                },
            };

            if (tolerance != Tolerance.None)
                weenie.PropertiesInt[PropertyInt.Tolerance] = (int)tolerance;

            if (faction1 != null)
                weenie.PropertiesInt[PropertyInt.Faction1Bits] = faction1.Value;

            if (foeType != null)
                weenie.PropertiesInt[PropertyInt.FoeType] = (int)foeType.Value;

            return weenie;
        }

        /// <summary>One live instance of a weenie, the way WorldObjectFactory builds it.</summary>
        private static Creature CreatureFrom(Weenie weenie)
        {
            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.Location = new ACE.Entity.Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            return creature;
        }

        private static Creature CreateRosterCreature(Tolerance tolerance = Tolerance.None, int? faction1 = null,
            CreatureType? foeType = null, CreatureType creatureType = CreatureType.Shadow)
            => CreatureFrom(RosterWeenie(tolerance, faction1, foeType, creatureType));

        /// <summary>wcid 72837 "Void Lord": Tolerance 128 (Monster) and Faction1Bits 32.</summary>
        private static Creature CreateFactionVoidLord()
            => CreateRosterCreature(Tolerance.Monster, faction1: 32);

        /// <summary>wcid 43899 "Void Lord": the same creature by name and level, with neither property.</summary>
        private static Creature CreatePlainVoidLord() => CreateRosterCreature();

        // ---- the pure bit rule ----

        [TestMethod]
        public void PlayerHostileTolerance_strips_Monster_which_is_what_makes_a_creature_ignore_the_player()
        {
            Assert.AreEqual(Tolerance.None, SpawnedCreatureHostility.PlayerHostileTolerance(Tolerance.Monster));
        }

        [TestMethod]
        public void PlayerHostileTolerance_strips_NoAttack_which_is_the_Frozen_Gearknight_defect()
        {
            Assert.AreEqual(Tolerance.None, SpawnedCreatureHostility.PlayerHostileTolerance(Tolerance.NoAttack));
        }

        [TestMethod]
        public void PlayerHostileTolerance_keeps_every_bit_that_only_delays_the_fight()
        {
            // Retaliate alone is the single most common Tolerance in the shipped roster (47 of the 63 members
            // carrying any Tolerance at all). Stripping it would rewrite every ambush creature in the tables
            // into an aggro-on-sight one, which is not what this fix is for.
            var keepers = Tolerance.Appraise | Tolerance.Provoke | Tolerance.Target | Tolerance.Retaliate;

            Assert.AreEqual(keepers, SpawnedCreatureHostility.PlayerHostileTolerance(keepers));
        }

        [TestMethod]
        public void PlayerHostileTolerance_strips_only_the_banned_bits_from_a_mixed_value()
        {
            var mixed = Tolerance.Monster | Tolerance.NoAttack | Tolerance.Retaliate | Tolerance.Appraise;

            Assert.AreEqual(Tolerance.Retaliate | Tolerance.Appraise, SpawnedCreatureHostility.PlayerHostileTolerance(mixed));
        }

        // ---- the live creature ----

        [TestMethod]
        public void The_reported_pair_start_out_hostile_to_each_other()
        {
            // The premise. If this stops holding, every assertion below is measuring nothing, so assert the
            // untreated behaviour explicitly rather than trusting the bug report.
            var factionLord = CreateFactionVoidLord();
            var plainLord = CreatePlainVoidLord();

            Assert.IsTrue(factionLord.IsFactionMob, "wcid 72837's Faction1Bits no longer makes it a faction mob");

            var factionPhysics = new WeenieObject(factionLord);
            var plainPhysics = new WeenieObject(plainLord);

            Assert.IsTrue(factionPhysics.IsFactionMob,
                "ObjectMaint.ApplyFilter routes on this flag; false here means the two would never have seen each other");
            Assert.IsFalse(plainPhysics.IsFactionMob, "the factionless Void Lord picked up a faction");

            // WeenieObject.SameFaction is exactly this bit AND, so "some faction" and "no faction" are never
            // the same faction and GetAttackTargets does not skip the pair.
            Assert.AreEqual(FactionBits.None, factionPhysics.Faction1Bits & plainPhysics.Faction1Bits,
                "the two read as the SAME faction, which would have made them skip each other");
        }

        [TestMethod]
        public void MakeHostileToPlayers_clears_the_faction_that_made_them_fight_each_other()
        {
            var creature = CreateFactionVoidLord();

            SpawnedCreatureHostility.MakeHostileToPlayers(creature);

            Assert.IsNull(creature.Faction1Bits, "Faction1Bits must be REMOVED, not zeroed");
            Assert.IsNull(creature.Faction2Bits);
            Assert.IsNull(creature.Faction3Bits);
        }

        [TestMethod]
        public void MakeHostileToPlayers_refreshes_the_cached_faction_flag()
        {
            // SetMonsterState caches IsFactionMob and last ran inside the Creature constructor, i.e. before
            // any of these writes. Without the re-run the property is clear but the cache still says "faction
            // mob", and Monster_Tick/GetAttackTargets read the cache.
            var creature = CreateFactionVoidLord();

            Assert.IsTrue(creature.IsFactionMob);

            SpawnedCreatureHostility.MakeHostileToPlayers(creature);

            Assert.IsFalse(creature.IsFactionMob,
                "the Creature-level cache still reports a faction mob; SetMonsterState was not re-run");
        }

        [TestMethod]
        public void MakeHostileToPlayers_leaves_the_physics_layer_seeing_an_ordinary_monster()
        {
            // The physics WeenieObject is what ObjectMaint.ApplyFilter actually branches on, and it is built
            // later than this call - from Landblock.AddWorldObjectInternal during EnterWorld - so it must read
            // the corrected values.
            var creature = CreateFactionVoidLord();

            SpawnedCreatureHostility.MakeHostileToPlayers(creature);

            var physics = new WeenieObject(creature);

            Assert.IsTrue(physics.IsMonster, "the creature stopped being a monster entirely");
            Assert.IsFalse(physics.IsFactionMob, "the physics layer still sees a faction mob");
            Assert.AreEqual(FactionBits.None, physics.Faction1Bits);
            Assert.IsNull(physics.FoeType);
        }

        [TestMethod]
        public void MakeHostileToPlayers_clears_Tolerance_Monster_so_the_creature_can_see_the_player()
        {
            var creature = CreateFactionVoidLord();

            Assert.IsTrue(creature.Tolerance.HasFlag(Tolerance.Monster));

            SpawnedCreatureHostility.MakeHostileToPlayers(creature);

            Assert.IsFalse(creature.Tolerance.HasFlag(Tolerance.Monster),
                "Monster_Awareness.GetAttackTargets still skips every Player and CombatPet for this creature");
        }

        [TestMethod]
        public void MakeHostileToPlayers_clears_a_FoeType_even_though_no_shipped_roster_member_has_one()
        {
            // WeenieObject.PotentialFoe is symmetric: a single roster member whose FoeType matches its OWN
            // CreatureType makes every same-species creature in the wave a target for it and it a target for
            // them. No member ships with one today; this keeps that true after the next roster regeneration.
            var creature = CreateRosterCreature(foeType: CreatureType.Shadow, creatureType: CreatureType.Shadow);

            Assert.IsTrue(creature.HasFoeType);

            SpawnedCreatureHostility.MakeHostileToPlayers(creature);

            Assert.IsNull(creature.FoeType);
            Assert.IsFalse(creature.HasFoeType, "the cached HasFoeType was not refreshed");
        }

        [TestMethod]
        public void MakeHostileToPlayers_leaves_an_already_clean_creature_alone()
        {
            var creature = CreateRosterCreature(Tolerance.Retaliate);

            SpawnedCreatureHostility.MakeHostileToPlayers(creature);

            Assert.AreEqual(Tolerance.Retaliate, creature.Tolerance, "an ambush creature lost its ambush");
            Assert.IsTrue(creature.IsMonster);
            Assert.IsNull(creature.Faction1Bits);
        }

        [TestMethod]
        public void MakeHostileToPlayers_touches_only_the_creature_it_is_given()
        {
            // Faction1Bits, Tolerance and FoeType are all PropertyInt, and PropertiesInt is COPIED into each
            // new instance's biota rather than reference-shared with the cached weenie
            // (WeenieConverter.cs:29-30). This is the assertion that keeps that true.
            //
            // BOTH creatures are built from ONE Weenie instance on purpose: that is the production shape, in
            // which WorldDatabaseWithEntityCache.GetCachedWeenie hands the same object to every instance of a
            // wcid server-wide. Two separately-built weenies would be independent by construction and the
            // test would still pass if WeenieConverter ever started reference-sharing PropertiesInt the way
            // it already shares PropertiesEmote and PropertiesCreateList - which is exactly the regression
            // this is here to catch.
            var cachedWeenie = RosterWeenie(Tolerance.Monster, faction1: 32);

            var runCreature = CreatureFrom(cachedWeenie);
            var retailCreature = CreatureFrom(cachedWeenie);

            SpawnedCreatureHostility.MakeHostileToPlayers(runCreature);

            Assert.IsNull(runCreature.Faction1Bits);

            Assert.AreEqual((FactionBits)32, retailCreature.Faction1Bits,
                $"a sibling instance of wcid {cachedWeenie.WeenieClassId} lost its faction");
            Assert.IsTrue(retailCreature.Tolerance.HasFlag(Tolerance.Monster));

            Assert.AreEqual(32, cachedWeenie.PropertiesInt[PropertyInt.Faction1Bits],
                "the CACHED weenie lost its faction; every instance of this wcid server-wide is now affected");
            Assert.AreEqual((int)Tolerance.Monster, cachedWeenie.PropertiesInt[PropertyInt.Tolerance],
                "the CACHED weenie lost its Tolerance");
        }

        [TestMethod]
        public void MakeHostileToPlayers_tolerates_a_null_creature()
        {
            SpawnedCreatureHostility.MakeHostileToPlayers(null);
        }

        // ---- the World Events kind gate ----

        [TestMethod]
        public void NormalizesHostility_covers_the_two_roster_drawn_kinds()
        {
            Assert.IsTrue(SpawnedCreatureHostility.NormalizesHostility(WorldEventSpawnKind.Wave));
            Assert.IsTrue(SpawnedCreatureHostility.NormalizesHostility(WorldEventSpawnKind.Boss));
        }

        [TestMethod]
        public void NormalizesHostility_spares_the_objective_props()
        {
            // WorldEventSpawner.Spawn places these through the SAME loop as Wave and Boss, so this gate is
            // the only thing keeping them out. An InertObjective is stamped non-attackable scenery on
            // purpose, and a Source authored with Tolerance.NoAttack would start swinging at players if it
            // came through MakeHostileToPlayers.
            Assert.IsFalse(SpawnedCreatureHostility.NormalizesHostility(WorldEventSpawnKind.Source));
            Assert.IsFalse(SpawnedCreatureHostility.NormalizesHostility(WorldEventSpawnKind.InertObjective));
        }

        [TestMethod]
        public void NormalizesHostility_spares_friendlies_decor_and_generator_adds()
        {
            // These never reach Spawn's loop today. Asserted anyway: making an event NPC hostile is the whole
            // failure this class prevents, pointed the wrong way, and an Add is already in the world when the
            // event adopts it, so writing these properties then would break the nothing-after-EnterWorld rule.
            Assert.IsFalse(SpawnedCreatureHostility.NormalizesHostility(WorldEventSpawnKind.Npc));
            Assert.IsFalse(SpawnedCreatureHostility.NormalizesHostility(WorldEventSpawnKind.Decor));
            Assert.IsFalse(SpawnedCreatureHostility.NormalizesHostility(WorldEventSpawnKind.Add));
        }

        [TestMethod]
        public void NormalizesHostility_decides_every_spawn_kind_that_exists()
        {
            // A new WorldEventSpawnKind must be classified deliberately rather than defaulting to "not
            // normalized" unnoticed. This fails the moment the enum grows a member neither list names.
            var known = new HashSet<WorldEventSpawnKind>
            {
                WorldEventSpawnKind.Wave, WorldEventSpawnKind.Boss,
                WorldEventSpawnKind.Source, WorldEventSpawnKind.InertObjective,
                WorldEventSpawnKind.Npc, WorldEventSpawnKind.Decor, WorldEventSpawnKind.Add,
            };

            foreach (WorldEventSpawnKind kind in System.Enum.GetValues(typeof(WorldEventSpawnKind)))
            {
                Assert.IsTrue(known.Contains(kind),
                    $"WorldEventSpawnKind.{kind} is new; decide whether SpawnedCreatureHostility.NormalizesHostility should cover it");
            }
        }
    }
}
