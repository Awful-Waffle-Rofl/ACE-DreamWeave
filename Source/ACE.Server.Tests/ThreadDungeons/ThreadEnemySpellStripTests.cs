using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Adapter;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Threads-only strip of caster-vital-scaled life projectiles (Martyr's Hecatomb and kin), owner ruling 2026-11-21.
    /// The selection rule and the book strip are pure; Apply is exercised on a real Creature with a spell book and spell-bearing
    /// items; the wiring is pinned by exact non-comment source lines: PrepareForPlacement applies it only for a creature with a
    /// Thread run, the shared guard and every World Events file do not reference it, and every encounter creature path reaches
    /// ShapeCreature with a run. The dat-backed census of the real spell table is ThreadEnemySpellStripDatTests.
    /// </summary>
    [TestClass]
    public class ThreadEnemySpellStripTests
    {
        // Real values from the local ace_world.spell (control: SELECT id, drain_percentage ... WHERE drain_percentage > 0 returns exactly
        // these 25 ids): Martyr's Hecatomb/Tenacity/Blight tiers 2760-2780 and 4428-4430 at 0.25, Curse of Raven Fury 3818 at 0.5.
        // 1175 Harm Other V / 2070 Heart Rend / 3940 Exsanguinating Wave / 3914 Dark Vortex have NULL drain; the rule test below pairs them with LifeProjectile on purpose, to prove drain alone excludes them (1175 is a Boost in the dat; the dat census is ThreadEnemySpellStripDatTests).
        private static readonly int[] DrainIds = Enumerable.Range(2760, 21).Concat(new[] { 3818, 4428, 4429, 4430 }).ToArray();

        private static readonly Dictionary<int, (SpellType Meta, float Drain)> Spells = BuildSpells();

        private static Dictionary<int, (SpellType, float)> BuildSpells()
        {
            var table = new Dictionary<int, (SpellType, float)>();

            foreach (var id in DrainIds)
                table[id] = (SpellType.LifeProjectile, id == 3818 ? 0.5f : 0.25f);

            foreach (var id in new[] { 1175, 2070, 3940, 3914 })
                table[id] = (SpellType.LifeProjectile, 0f);

            table[100] = (SpellType.Projectile, 0f);
            table[101] = (SpellType.Projectile, 0.4f);

            return table;
        }

        [TestMethod]
        public void The_drain_id_set_is_the_25_real_spells()
        {
            Assert.AreEqual(25, DrainIds.Length);
            Assert.AreEqual(25, DrainIds.Distinct().Count());
            Assert.AreEqual(25, Spells.Keys.Count(Selected), "the rule selects exactly the 25 drain spells and nothing else in the table");
        }

        private static bool Selected(int id) => Spells.TryGetValue(id, out var s) && ThreadEnemySpellStrip.IsCasterVitalScaled(s.Meta, s.Drain);

        [TestMethod]
        public void The_rule_selects_caster_vital_life_projectiles_and_leaves_fixed_health_spells()
        {
            Assert.IsTrue(Selected(2764), "Martyr's Hecatomb V");
            Assert.IsTrue(Selected(2772), "Martyr's Tenacity VI (stamina) scales with the caster too");
            Assert.IsTrue(Selected(3818), "Curse of Raven Fury");

            foreach (var kept in new[] { 1175, 2070, 3940, 3914 })
                Assert.IsFalse(Selected(kept), $"fixed-intensity Health spell {kept} stays");

            Assert.IsFalse(Selected(100));
            Assert.IsFalse(Selected(101), "a non-life spell with a stray DrainPercentage is not selected: the meta type is half the rule");
        }

        [TestMethod]
        public void StripBook_removes_only_the_selected_spells_in_place_and_reports_them()
        {
            var book = new Dictionary<int, float> { [2764] = 2.05f, [1175] = 2.1f, [3940] = 2.05f, [2772] = 2.02f };

            var removed = ThreadEnemySpellStrip.StripBook(book, Selected);

            CollectionAssert.AreEqual(new[] { 2764, 2772 }, removed);
            CollectionAssert.AreEquivalent(new[] { 1175, 3940 }, book.Keys.ToList());
            Assert.AreEqual(2.1f, book[1175], "survivors keep their probabilities");
        }

        [TestMethod]
        public void StripBook_tolerates_a_null_or_empty_book()
        {
            Assert.AreEqual(0, ThreadEnemySpellStrip.StripBook(null, Selected).Count);
            Assert.AreEqual(0, ThreadEnemySpellStrip.StripBook(new Dictionary<int, float>(), Selected).Count);
        }

        /// <summary>
        /// Per-instance safety, against the real converter: two instances built from one weenie each own their spell book, so
        /// stripping one leaves the sibling and the cached weenie intact (PropertiesSpellBook is copied, unlike PropertiesBodyPart).
        /// </summary>
        [TestMethod]
        public void A_second_instance_of_the_same_wcid_keeps_its_spellbook()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 38830,
                WeenieType = WeenieType.Creature,
                PropertiesSpellBook = new Dictionary<int, float> { [2764] = 2.05f, [1175] = 2.1f },
            };

            var first = weenie.ConvertToBiota(0x70000001);
            var second = weenie.ConvertToBiota(0x70000002);

            Assert.AreNotSame(first.PropertiesSpellBook, second.PropertiesSpellBook);
            Assert.AreNotSame(weenie.PropertiesSpellBook, first.PropertiesSpellBook);

            var removed = ThreadEnemySpellStrip.StripBook(first.PropertiesSpellBook, Selected);

            CollectionAssert.AreEqual(new[] { 2764 }, removed);
            CollectionAssert.AreEquivalent(new[] { 1175 }, first.PropertiesSpellBook.Keys.ToList());
            CollectionAssert.AreEquivalent(new[] { 2764, 1175 }, second.PropertiesSpellBook.Keys.ToList(), "the sibling instance keeps both");
            CollectionAssert.AreEquivalent(new[] { 2764, 1175 }, weenie.PropertiesSpellBook.Keys.ToList(), "the cached weenie keeps both");
        }

        /// <summary>A creature left with an empty book rolls no spell and does not throw (Monster_Magic.TryRollSpell reads the book through this selector, or a plain foreach).</summary>
        [TestMethod]
        public void An_empty_spellbook_rolls_no_spell_and_does_not_throw()
        {
            var empty = new Dictionary<int, float>();
            var shape = new Func<int, MonsterSpellShape>(id => default);

            Assert.IsFalse(MonsterSpellSelector.TrySelect(empty, shape, default, () => 0.5, out var spellId));
            Assert.AreEqual(0, spellId);

            var unconditional = 0;

            foreach (var spell in empty) // Monster_Magic.TryRollSpell_Unconditional's loop
                unconditional++;

            Assert.AreEqual(0, unconditional);
        }

        // ---- Apply on a real Creature ---------------------------------------------------------------------

        private static uint nextGuid = 0x7E3A0000;
        private static uint nextWcid = 997500;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        private static Creature MakeCreature(Dictionary<int, float> book)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesSpellBook = book,
            };

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject MakeItem(PropertyDataId property, uint spellId, bool equipped, Creature owner)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesDID = new Dictionary<PropertyDataId, uint> { [property] = spellId },
            };

            var item = new GenericObject(weenie, new ObjectGuid(nextGuid++));

            if (equipped)
                owner.EquippedObjects[item.Guid] = item;
            else
                owner.Inventory[item.Guid] = item;

            return item;
        }

        [TestMethod]
        public void Apply_strips_the_book_and_equipped_and_inventory_spell_items_of_a_real_creature()
        {
            var creature = MakeCreature(new Dictionary<int, float> { [2764] = 2.05f, [1175] = 2.1f, [3940] = 2.05f });
            var equipped = MakeItem(PropertyDataId.ProcSpell, 2764, true, creature);
            var inInventory = MakeItem(PropertyDataId.Spell, 2772, false, creature);
            var kept = MakeItem(PropertyDataId.ProcSpell, 3914, true, creature);

            var changes = ThreadEnemySpellStrip.Apply(creature, Selected);

            CollectionAssert.AreEquivalent(new[] { 1175, 3940 }, creature.Biota.PropertiesSpellBook.Keys.ToList());
            Assert.IsNull(equipped.GetProperty(PropertyDataId.ProcSpell), "the equipped item's proc is removed");
            Assert.IsNull(inInventory.GetProperty(PropertyDataId.Spell), "an Inventory item is swept too: it can be equipped mid-fight");
            Assert.AreEqual(3914u, kept.GetProperty(PropertyDataId.ProcSpell), "a fixed-intensity proc stays");

            CollectionAssert.AreEquivalent(
                new[] { "Spell2764", $"ProcSpell2764@{equipped.WeenieClassId}", $"Spell2772@{inInventory.WeenieClassId}" },
                changes);
        }

        [TestMethod]
        public void Apply_on_a_creature_with_no_book_and_no_items_changes_nothing()
        {
            var creature = MakeCreature(null);

            Assert.AreEqual(0, ThreadEnemySpellStrip.Apply(creature, Selected).Count);
            Assert.AreEqual(0, ThreadEnemySpellStrip.Apply(null, Selected).Count);
        }

        // ---- wiring (exact non-comment lines) ---------------------------------------------------------------

        private static string[] Lines(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { FindRepoRoot(), "Source", "ACE.Server" }.Concat(parts).ToArray())).Replace("\r\n", "\n").Split('\n');

        /// <summary>The index of the single non-comment line whose trimmed text is exactly <paramref name="code"/>; fails unless there is exactly one.</summary>
        private static int ExactLine(string[] lines, string code, string file)
        {
            var hits = Enumerable.Range(0, lines.Length)
                .Where(i => lines[i].Trim() == code && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();

            Assert.AreEqual(1, hits.Count, $"{file}: expected exactly one line '{code}', found {hits.Count}");
            return hits[0];
        }

        [TestMethod]
        public void PrepareForPlacement_applies_the_strip_only_for_a_creature_with_a_Thread_run_after_the_shared_guard()
        {
            var lines = Lines("ThreadDungeons", "ThreadDungeonSpawner.cs");

            var guard = ExactLine(lines, "var stripped = DungeonCreatureNormalizer.StripCombatTraits(creature, plan, entry.Role == DungeonRole.Boss, capStandard);", "ThreadDungeonSpawner.cs");
            var apply = ExactLine(lines, "stripped.AddRange(ThreadEnemySpellStrip.Apply(creature));", "ThreadDungeonSpawner.cs");

            Assert.IsTrue(guard < apply, "the spell strip runs after the shared guard");
            Assert.AreEqual("if (run != null || options?.StripCasterVitalSpells == true)", lines[apply - 1].Trim(), "gated on a Thread run or the explicit speed-course flag: Gauntlet and Balance Trials pass neither and keep their spell books");

            var scope = ExactLine(lines, "if (plan.StripCombatTraits)", "ThreadDungeonSpawner.cs");
            Assert.IsTrue(scope < guard && guard - scope <= 3, "inside the dynamic_dungeons_strip_combat_traits block");
        }

        [TestMethod]
        public void The_Thread_speed_course_flags_its_creatures_for_the_strip()
        {
            var lines = Lines("SpeedCourses", "LiveSpeedCourseWorld.cs");
            var call = ExactLine(lines, "ThreadDungeonSpawner.ShapeCreature(creature, plan, entry, stamp.ObservedMaxNonBoss, new CreatureShapeOptions", "LiveSpeedCourseWorld.cs");
            var flag = ExactLine(lines, "StripCasterVitalSpells = true,", "LiveSpeedCourseWorld.cs");

            Assert.IsTrue(flag > call && flag - call <= 8, "the flag sits inside the ShapeCreature options");
        }

        [TestMethod]
        public void Gauntlet_flags_its_creatures_for_the_strip_and_Balance_Trials_do_not()
        {
            Assert.IsNull(ACE.Server.Avatars.Gauntlet.GauntletSpawner.ShapeOptions(null, "t").Run);
            Assert.IsNull(ACE.Server.BalanceTrials.TrialSpawner.ShapeOptions("t").Run);
            Assert.IsTrue(ACE.Server.Avatars.Gauntlet.GauntletSpawner.ShapeOptions(null, "t").StripCasterVitalSpells, "the Gauntlet strips (owner ruling 2026-11-22)");
            Assert.IsFalse(ACE.Server.BalanceTrials.TrialSpawner.ShapeOptions("t").StripCasterVitalSpells);

            var gauntlet = Lines("AllegianceAvatar", "Gauntlet", "GauntletSpawner.cs");
            ExactLine(gauntlet, "Run = null,", "GauntletSpawner.cs");
            ExactLine(gauntlet, "StripCasterVitalSpells = true,", "GauntletSpawner.cs");

            // Both Gauntlet ShapeCreature paths take their options from ShapeOptions, so the one flag covers both.
            Assert.AreEqual(2, gauntlet.Count(l => l.Contains("ThreadDungeonSpawner.ShapeCreature(") && l.Contains("ShapeOptions(wave.Group, tag)") && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void The_shared_guard_and_World_Events_do_not_reference_the_Threads_spell_strip()
        {
            Assert.IsFalse(string.Join("\n", Lines("ThreadDungeons", "CombatTraitGuard.cs")).Contains("ThreadEnemySpellStrip"), "the guard World Events share must not strip spells");
            Assert.IsFalse(string.Join("\n", Lines("ThreadDungeons", "DungeonCreatureNormalizer.cs")).Contains("ThreadEnemySpellStrip"), "the normalizer wrapper stays the plain shared guard");

            var offenders = Directory.GetFiles(Path.Combine(FindRepoRoot(), "Source", "ACE.Server", "WorldEvents"), "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("ThreadEnemySpellStrip")).Select(Path.GetFileName).ToList();

            Assert.AreEqual(0, offenders.Count, "World Events files referencing the Threads spell strip: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// Every creature a Thread encounter places is shaped by ThreadDungeonSpawner.ShapeCreature with the run, which reaches the
        /// strip above: Defense and Gamble through the shared portal-wave placer, Shrine guardians, the Golem. Jump creates no enemy.
        /// </summary>
        [TestMethod]
        public void Every_enemy_spawning_Thread_encounter_shapes_its_creature_with_the_run()
        {
            var waves = Lines("ThreadDungeons", "ThreadEncounterPortalWaves.cs");
            var w = ExactLine(waves, "request.ShapeCreature(creature, entry, new CreatureShapeOptions", "ThreadEncounterPortalWaves.cs");
            Assert.AreEqual("Run = request.Run,", waves[w + 2].Trim());

            var shrine = Lines("ThreadDungeons", "ShrineEncounterType.cs");
            var s = ExactLine(shrine, "request.ShapeCreature(creature, entry, new CreatureShapeOptions", "ShrineEncounterType.cs");
            Assert.AreEqual("Run = request.Run,", shrine[s + 2].Trim());

            var golem = Lines("ThreadDungeons", "GolemEncounterType.cs");
            var g = ExactLine(golem, "request.ShapeCreature(golem, entry, new CreatureShapeOptions", "GolemEncounterType.cs");
            Assert.AreEqual("Run = run,", golem[g + 2].Trim());

            // ...and none of them wraps its factory in a second strip any more.
            foreach (var file in new[] { "DefenseEncounterType.cs", "GolemEncounterType.cs", "JumpEncounterType.cs", "GambleEncounterType.cs", "ShrineEncounterType.cs" })
                ExactLine(Lines("ThreadDungeons", file), "internal static Func<uint, WorldObject> CreateObject = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);", file);
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Source", "property-registry.tsv")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new AssertFailedException($"Could not find the repo root by walking up from {AppContext.BaseDirectory}.");
        }
    }
}
