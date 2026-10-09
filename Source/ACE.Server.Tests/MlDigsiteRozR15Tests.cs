using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// RoZ round 15 digsite fixes: staff presence, the chest loot profile, the currency floor, the empty-chest
    /// line, the flat XP/luminance ruling, the wave-clear line, and the held-caster spawn guard.
    ///
    /// Pure rules are asserted directly. The call sites that need a live world or a Player (the chest build,
    /// the grant loop, the announcement fan-out, the spawner) are pinned against their source text, the
    /// pattern MlDigsiteRulesTests already uses, because ACE.Server.Tests cannot construct a Player. The
    /// actual property-writing delivery test for round 15 lives in MlTreasureSitePlacementDeliveryTests.
    ///
    /// Order-independent: no PropertyManager read, only DefaultPropertyManager's code-default dictionaries.
    /// </summary>
    [TestClass]
    public class MlDigsiteRozR15Tests
    {
        // ---- 1. staff count toward digsite presence -------------------------------------------------------

        [TestMethod]
        public void Every_access_level_counts_at_a_digsite_while_alive()
        {
            foreach (AccessLevel level in Enum.GetValues(typeof(AccessLevel)))
                Assert.IsTrue(MlDigsiteAudience.CountsAtDigsite(level, isDead: false), $"{level} should count at a digsite");

            Assert.IsTrue(MlDigsiteAudience.CountsAtDigsite(null, isDead: false));
        }

        [TestMethod]
        public void The_dead_never_count_at_a_digsite_staff_or_not()
        {
            Assert.IsFalse(MlDigsiteAudience.CountsAtDigsite(AccessLevel.Player, isDead: true));
            Assert.IsFalse(MlDigsiteAudience.CountsAtDigsite(AccessLevel.Admin, isDead: true));
        }

        [TestMethod]
        public void World_events_keep_excluding_staff()
        {
            // The change is digsite-only: the shared sampler is untouched.
            Assert.IsFalse(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Sentinel));
            Assert.IsFalse(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Admin));
        }

        [TestMethod]
        public void The_digsite_scan_no_longer_calls_the_world_event_staff_rule()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteAudience.cs");
            var counts = PooledLootSourceText.MethodBody(src, "private static bool Counts(Player player, Position anchor, float radius)");

            Assert.IsFalse(src.Contains("WorldEventAudienceSampler.CountsTowardAudience("), "the digsite scan must not apply the world event staff exclusion");
            StringAssert.Contains(counts, "return CountsAtDigsite(player.Session?.AccessLevel, player.IsDead);");
        }

        [TestMethod]
        public void Announce_fans_out_over_the_same_participant_scan()
        {
            // So staff, now counted by that scan, also receive every digsite line.
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");
            var announce = PooledLootSourceText.MethodBody(src,
                "internal static void Announce(MlDigsiteEncounter encounter, string message, ChatMessageType type = ChatMessageType.Broadcast)");

            StringAssert.Contains(announce, "MlDigsiteAudience.Participants(encounter.Anchor, MlDigsiteTunables.AudienceRadiusMetres)");
        }

        // ---- 2. chest loot profile, currency floor, empty-chest line ------------------------------------

        [TestMethod]
        public void Chest_profile_default_is_the_legendary_chest_treasure_type_not_its_row_id()
        {
            Assert.AreEqual(2001L, MlDigsiteTunables.DefaultChestTreasureType);
            Assert.AreEqual(2001L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_chest_treasure_death_id"].Item);
        }

        [TestMethod]
        public void ScaleCurrency_floors_at_one_whenever_anything_is_owed()
        {
            // The reported case: 5 x 0.1 = 0.5 rounds to 0 under ScaleReward.
            Assert.AreEqual(0L, MlDigsiteRules.ScaleReward(5, 0.1));
            Assert.AreEqual(1L, MlDigsiteRules.ScaleCurrency(5, 0.1));
            Assert.AreEqual(1L, MlDigsiteRules.ScaleCurrency(5, 0.0001));
        }

        [TestMethod]
        public void ScaleCurrency_matches_ScaleReward_once_the_rounded_amount_is_positive()
        {
            Assert.AreEqual(MlDigsiteRules.ScaleReward(5, 0.5), MlDigsiteRules.ScaleCurrency(5, 0.5), "same (banker's) rounding as ScaleReward");
            Assert.AreEqual(3L, MlDigsiteRules.ScaleCurrency(5, 0.6));
            Assert.AreEqual(5L, MlDigsiteRules.ScaleCurrency(5, 1.0));
            Assert.AreEqual(5L, MlDigsiteRules.ScaleCurrency(5, 7.0), "clamped at the full amount");
        }

        [TestMethod]
        public void ScaleCurrency_pays_nothing_when_nothing_is_owed()
        {
            Assert.AreEqual(0L, MlDigsiteRules.ScaleCurrency(5, 0.0));
            Assert.AreEqual(0L, MlDigsiteRules.ScaleCurrency(5, -1.0));
            Assert.AreEqual(0L, MlDigsiteRules.ScaleCurrency(5, double.NaN));
            Assert.AreEqual(0L, MlDigsiteRules.ScaleCurrency(0, 0.5), "a zeroed tunable is still off");
        }

        [TestMethod]
        public void ChestHasContents_is_false_only_for_a_truly_empty_chest()
        {
            Assert.IsFalse(MlDigsiteRules.ChestHasContents(0, 0, 0, 0));
            Assert.IsTrue(MlDigsiteRules.ChestHasContents(1, 0, 0, 0));
            Assert.IsTrue(MlDigsiteRules.ChestHasContents(0, 1, 0, 0));
            Assert.IsTrue(MlDigsiteRules.ChestHasContents(0, 0, 1, 0));
            Assert.IsTrue(MlDigsiteRules.ChestHasContents(0, 0, 0, 1));
        }

        [TestMethod]
        public void BuildChest_uses_the_currency_floor_and_gates_the_cache_line_on_contents()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs");
            var build = PooledLootSourceText.MethodBody(src,
                "private static void BuildChest(MlDigsiteEncounter encounter, MlDigsiteResult result, ChestPlan plan)");

            StringAssert.Contains(build, "MlDigsiteRules.ScaleCurrency(MlDigsiteTunables.ChestDoubloons, fraction)");
            StringAssert.Contains(build, "MlDigsiteRules.ScaleCurrency(MlDigsiteTunables.ChestTradeNotes, fraction)");

            var gate = build.IndexOf("if (!MlDigsiteRules.ChestHasContents(doubloons, notes, loot, keptSiraluun))", StringComparison.Ordinal);
            var line = build.IndexOf("Something buried at the dig gives way", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "the contents gate is missing");
            Assert.IsTrue(line > gate, "the cache line must come after (and behind) the contents gate");
        }

        // ---- 5. flat XP / luminance ----------------------------------------------------------------------

        [TestMethod]
        public void Full_amounts_ship_at_the_owner_ruling()
        {
            Assert.AreEqual(425000000L, MlDigsiteTunables.DefaultFullXp);
            Assert.AreEqual(80000L, MlDigsiteTunables.DefaultFullLuminance);
            Assert.AreEqual(425000000L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_full_xp"].Item);
            Assert.AreEqual(80000L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_full_luminance"].Item);
        }

        [TestMethod]
        public void A_full_clear_pays_the_full_amounts_flat_whatever_the_fraction()
        {
            foreach (var fraction in new[] { 0.0, 0.1, 0.35, 0.99, 1.0, double.NaN })
            {
                var grant = MlDigsiteRules.XpLuminanceFraction(MlDigsiteResult.FullClear, fraction);

                Assert.AreEqual(425000000L, MlDigsiteRules.ScaleReward(425000000, grant), $"fraction {fraction}");
                Assert.AreEqual(80000L, MlDigsiteRules.ScaleReward(80000, grant), $"fraction {fraction}");
            }
        }

        [TestMethod]
        public void Any_other_outcome_keeps_its_fraction_of_the_same_amounts()
        {
            foreach (MlDigsiteResult result in Enum.GetValues(typeof(MlDigsiteResult)))
            {
                if (result == MlDigsiteResult.FullClear)
                    continue;

                var grant = MlDigsiteRules.XpLuminanceFraction(result, 0.35);

                Assert.AreEqual(0.35, grant, 1e-12, $"{result}");
                Assert.AreEqual(148750000L, MlDigsiteRules.ScaleReward(425000000, grant));
                Assert.AreEqual(28000L, MlDigsiteRules.ScaleReward(80000, grant));
                Assert.AreEqual(0.0, MlDigsiteRules.XpLuminanceFraction(result, double.NaN));
                Assert.AreEqual(1.0, MlDigsiteRules.XpLuminanceFraction(result, 5.0), "clamped");
            }
        }

        [TestMethod]
        public void PayParticipants_grants_on_the_flat_rule()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs");
            var pay = PooledLootSourceText.MethodBody(src,
                "private static void PayParticipants(MlDigsiteEncounter encounter, double fraction, MlDigsiteResult result, IReadOnlyList<uint> eligible)");

            StringAssert.Contains(pay, "var grantFraction = MlDigsiteRules.XpLuminanceFraction(result, fraction);");
            StringAssert.Contains(pay, "MlDigsiteRules.ScaleReward(MlDigsiteTunables.FullXp, grantFraction)");
            StringAssert.Contains(pay, "MlDigsiteRules.ScaleReward(MlDigsiteTunables.FullLuminance, grantFraction)");
        }

        // ---- 4. wave clear line --------------------------------------------------------------------------

        [TestMethod]
        public void Wave_clear_is_announced_green_with_the_highest_wave_cleared()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");

            StringAssert.Contains(src,
                "Announce(encounter, MlDigsiteRules.WaveClearedLine(encounter.HighestWaveCleared, breather), ChatMessageType.WorldBroadcast);");
            Assert.IsFalse(src.Contains("WaveClearedLine(encounter.WavesSpawned"), "the count must come from HighestWaveCleared");
        }

        // ---- 3. held caster with no spells ---------------------------------------------------------------

        [TestMethod]
        public void HeldCasterWithoutSpells_is_only_the_idle_shape()
        {
            Assert.IsTrue(MlDigsiteRules.HeldCasterWithoutSpells(holdsHeldCaster: true, hasSpells: false));
            Assert.IsFalse(MlDigsiteRules.HeldCasterWithoutSpells(holdsHeldCaster: true, hasSpells: true));
            Assert.IsFalse(MlDigsiteRules.HeldCasterWithoutSpells(holdsHeldCaster: false, hasSpells: false));
            Assert.IsFalse(MlDigsiteRules.HeldCasterWithoutSpells(holdsHeldCaster: false, hasSpells: true));
        }

        [TestMethod]
        public void TrySpawn_runs_the_held_caster_check_before_the_creature_enters_the_world()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs");
            var spawn = PooledLootSourceText.MethodBody(src,
                "public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)");

            var check = spawn.IndexOf("WarnIfHeldCasterWithoutSpells(encounter, creature, role);", StringComparison.Ordinal);
            var enter = spawn.IndexOf("creature.EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(check >= 0, "the guard is not called");
            Assert.IsTrue(check < enter);

            var guard = PooledLootSourceText.MethodBody(src,
                "private static void WarnIfHeldCasterWithoutSpells(MlDigsiteEncounter encounter, Creature creature, MlDigsiteRole role)");
            StringAssert.Contains(guard, "log.Warn(");
            Assert.IsFalse(guard.Contains("TryDequipObject") || guard.Contains("TryWieldObject"), "the guard warns; it must not silently re-equip");
        }

        [TestMethod]
        public void No_fork_creature_wields_a_fork_caster_without_a_spellbook()
        {
            // The content-side half of the round 15 held-caster guard, over every fork weenie rather than just
            // the digsite roster: Tureia (1003672) and the Drumspeaker of Greenspire (1003255) both shipped
            // wielding Buadren with no spells and idled. Scope: casters authored in Content/sql/weenies
            // (WeenieType 35); a retail caster wielded by a fork creature is not seen by this scan.
            DirectoryInfo weenies = null;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && weenies == null; dir = dir.Parent)
            {
                var candidate = new DirectoryInfo(Path.Combine(dir.FullName, "Content", "sql", "weenies"));

                if (candidate.Exists)
                    weenies = candidate;
            }

            Assert.IsNotNull(weenies, $"Could not find Content/sql/weenies by walking up from {AppContext.BaseDirectory}");

            var files = weenies.GetFiles("*.sql");
            var casterType = new Regex(@"VALUES \((\d+), '[^']*', 35,", RegexOptions.Compiled);
            var casters = new HashSet<string>();

            foreach (var file in files)
            {
                var m = casterType.Match(File.ReadAllText(file.FullName));

                if (m.Success)
                    casters.Add(m.Groups[1].Value);
            }

            Assert.IsTrue(casters.Contains("1002906"), "control: Buadren (1002906) must be found as a caster, or this scan proves nothing");

            var offenders = new List<string>();
            var checkedWielders = 0;

            foreach (var file in files)
            {
                var sql = File.ReadAllText(file.FullName);
                var createList = Regex.Match(sql, @"(?s)INSERT INTO `weenie_properties_create_list`.*?;");

                if (!createList.Success)
                    continue;

                foreach (Match row in Regex.Matches(createList.Value, @"\(\s*\d+,\s*(\d+),\s*(\d+),"))
                {
                    var destination = int.Parse(row.Groups[1].Value);

                    if ((destination & 2) == 0 || !casters.Contains(row.Groups[2].Value))
                        continue;

                    checkedWielders++;

                    if (!sql.Contains("weenie_properties_spell_book"))
                        offenders.Add($"{file.Name} wields caster {row.Groups[2].Value}");
                }
            }

            Assert.IsTrue(checkedWielders > 0, "control: at least one creature wields a fork caster (the Aun shamans do)");
            Assert.AreEqual(0, offenders.Count, "held caster with no spellbook: " + string.Join("; ", offenders));
        }

        [TestMethod]
        public void Drumspeaker_of_Greenspire_wields_a_spear_and_no_caster()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1003255 Drumspeaker of Greenspire.sql");

            StringAssert.Contains(sql, "VALUES (1003255, 2,     348, -1, 0, 0, False)");
            StringAssert.Contains(sql, "/* LightWeapons        Specialized */", "the spear must be a weapon it is skilled in");
            Assert.IsFalse(Regex.IsMatch(sql, @"\(1003255, 2, 1002906,"), "Buadren must not be in its create list: it has no spells");
        }

        [TestMethod]
        public void Tureia_wields_a_battle_axe_and_no_caster()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1003672 Tureia the Underbeat.sql");

            StringAssert.Contains(sql, "VALUES (1003672, 2,     301, -1, 0, 0, False) /* wield */");
            Assert.IsFalse(sql.Contains("1002906"), "Buadren (a Held caster) must not be in Tureia's create list: she has no spells");
            StringAssert.Contains(sql, "/* HeavyWeapons       Specialized */", "the axe must be a weapon she is skilled in");
        }
    }
}
