using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class DungeonGemRollerTests
    {
        private static Dictionary<string, ModifierDef> Mods() => new Dictionary<string, ModifierDef>
        {
            ["hardy"] = new ModifierDef { Id = "hardy", Rarity = "common", Target = "monster", MinMagnitude = 1.3, MaxMagnitude = 2.0 },
            ["savage"] = new ModifierDef { Id = "savage", Rarity = "common", Target = "monster", MinMagnitude = 10, MaxMagnitude = 40 },
            ["swift"] = new ModifierDef { Id = "swift", Rarity = "rare", Target = "monster", MinMagnitude = 1.15, MaxMagnitude = 1.35 },
            ["boss_enraged"] = new ModifierDef { Id = "boss_enraged", Rarity = "common", Target = "boss", MinMagnitude = 20, MaxMagnitude = 60 },
        };

        private static Dictionary<string, DungeonEntryDef> Dungeons() => new Dictionary<string, DungeonEntryDef>
        {
            ["filos_doom"] = new DungeonEntryDef { Id = "filos_doom", Enabled = true },
            ["closed"] = new DungeonEntryDef { Id = "closed", Enabled = false },
        };

        [TestMethod]
        public void Rolls_the_requested_count_without_repeats_or_boss_modifiers()
        {
            for (var seed = 0; seed < 50; seed++)
            {
                var spec = DungeonGemRoller.Roll(120, 6, 2, Mods(), Dungeons(), null, null, new Random(seed));
                Assert.AreEqual(2, spec.Modifiers.Count);
                Assert.AreEqual(2, spec.Modifiers.Select(m => m.Id).Distinct().Count());
                Assert.IsFalse(spec.Modifiers.Any(m => m.Id == "boss_enraged"));
                Assert.AreEqual("any", spec.DungeonId);
                Assert.AreEqual("any", spec.Family);
                Assert.AreEqual(120, spec.Level);
                Assert.AreEqual(6, spec.Tier);
            }
        }

        [TestMethod]
        public void Magnitudes_stay_inside_the_modifier_range()
        {
            for (var seed = 0; seed < 200; seed++)
            {
                var spec = DungeonGemRoller.Roll(50, 3, 3, Mods(), Dungeons(), null, null, new Random(seed));
                foreach (var (id, mag) in spec.Modifiers)
                {
                    var def = Mods()[id];
                    Assert.IsTrue(mag >= def.MinMagnitude && mag <= def.MaxMagnitude, $"{id}={mag}");
                }
            }
        }

        [TestMethod]
        public void Count_is_capped_by_the_eligible_pool()
        {
            var spec = DungeonGemRoller.Roll(50, 3, 10, Mods(), Dungeons(), null, null, new Random(1));
            Assert.AreEqual(3, spec.Modifiers.Count);
        }

        [TestMethod]
        public void Same_seed_same_roll()
        {
            var a = DungeonGemRoller.Roll(50, 3, 2, Mods(), Dungeons(), null, null, new Random(99)).Serialize();
            var b = DungeonGemRoller.Roll(50, 3, 2, Mods(), Dungeons(), null, null, new Random(99)).Serialize();
            Assert.AreEqual(a, b);
        }

        [TestMethod]
        public void Rare_modifiers_roll_less_often_than_common()
        {
            int swift = 0, hardy = 0;
            for (var seed = 0; seed < 2000; seed++)
            {
                var spec = DungeonGemRoller.Roll(50, 3, 1, Mods(), Dungeons(), null, null, new Random(seed));
                if (spec.Modifiers[0].Id == "swift") swift++;
                if (spec.Modifiers[0].Id == "hardy") hardy++;
            }
            Assert.IsTrue(hardy > swift * 3, $"hardy={hardy} swift={swift}");
        }

        [TestMethod]
        public void Named_dungeon_must_exist_and_be_enabled()
        {
            Assert.AreEqual("filos_doom", DungeonGemRoller.Roll(50, 3, 0, Mods(), Dungeons(), "filos_doom", null, new Random(1)).DungeonId);
            Assert.ThrowsExactly<ArgumentException>(() => DungeonGemRoller.Roll(50, 3, 0, Mods(), Dungeons(), "closed", null, new Random(1)));
            Assert.ThrowsExactly<ArgumentException>(() => DungeonGemRoller.Roll(50, 3, 0, Mods(), Dungeons(), "nope", null, new Random(1)));
        }

        [TestMethod]
        public void Level_and_tier_are_clamped()
        {
            var spec = DungeonGemRoller.Roll(999, 12, 0, Mods(), Dungeons(), null, null, new Random(1));
            Assert.AreEqual(DungeonGemSpec.MaxGemLevel, spec.Level);
            Assert.AreEqual(DungeonGemSpec.MaxTier, spec.Tier);
            var low = DungeonGemRoller.Roll(0, 0, 0, Mods(), Dungeons(), null, null, new Random(1));
            Assert.AreEqual(1, low.Level);
            Assert.AreEqual(1, low.Tier);
        }

        [TestMethod]
        public void Non_ident_family_throws()
        {
            Assert.ThrowsExactly<ArgumentException>(() => DungeonGemRoller.Roll(50, 3, 0, Mods(), Dungeons(), null, "Banderling", new Random(1)));
        }

        [TestMethod]
        public void Non_ident_dungeon_id_throws()
        {
            Assert.ThrowsExactly<ArgumentException>(() => DungeonGemRoller.Roll(50, 3, 0, Mods(), Dungeons(), "Filos_Doom", null, new Random(1)));
        }

        [TestMethod]
        public void Valid_family_and_dungeon_round_trip_through_serialize_and_parse()
        {
            var spec = DungeonGemRoller.Roll(50, 3, 1, Mods(), Dungeons(), "filos_doom", "banderling", new Random(1));
            Assert.IsTrue(DungeonGemSpec.TryParse(spec.Serialize(), out var parsed, out var error), error);
            Assert.AreEqual("filos_doom", parsed.DungeonId);
            Assert.AreEqual("banderling", parsed.Family);
        }
    }
}
