using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.MlDigsite.Mechanics;
using ACE.Server.MlTreasure;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ML digsite round 17 (owner rulings 2026-09-22): realm-wide opening broadcasts (RETIRED OUTRIGHT by the
    /// 2026-09-24 owner ruling below - see section 1), 2 doubloons per map, the Corrupted mob's announced
    /// power gain, 6x Corrupted health, the drum Volley's burning ground, and the interrupt drum's distance /
    /// window / visibility.
    ///
    /// Pure rules and the lock-guarded state objects (MlDigsiteEncounter, MlDigsiteBossMechanicState) are
    /// driven directly with a fake clock. The call sites that need a live world or a Player are pinned against
    /// their source text, the pattern MlDigsiteRozR15Tests already uses, because ACE.Server.Tests cannot
    /// construct a Player or a landblock.
    ///
    /// Order-independent: no PropertyManager read. The only shared state touched is the code defaults loaded
    /// below, the same call MlDigsiteBossMechanicStateTests makes, which no test modifies.
    /// </summary>
    [TestClass]
    public class MlDigsiteRozR17Tests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static readonly DateTime T0 = new DateTime(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc);

        private static uint nextGuid = 0x7D170000;   // static guid range, clear of GuidManager

        private static Position At(ushort realm, uint cell, float x, float y, float z = 0f, bool ephemeral = false, ushort shortInstance = 0)
            => new Position(cell, x, y, z, 0f, 0f, 0f, 1f, Position.InstanceIDFromVars(realm, shortInstance, ephemeral));

        /// <summary>An outdoor Marae Lassel cell (landblock 0x1BB1, inside the 0x0C-0x2E / 0xAC-0xCA box).</summary>
        private const uint MlOutdoorCell = 0x1BB10015;

        private static MlDigsiteEncounter NewEncounter(MlDigsiteType type = MlDigsiteType.CorruptionMeter)
            => new MlDigsiteEncounter(1, 0x50000001, "Digger", type, At(1, MlOutdoorCell, 60f, 110f), T0);

        private static MlDigsiteBossMechanicState NewState()
            => new MlDigsiteBossMechanicState(new MlDigsiteMechanicSet(2, MlDigsiteMechanic.Drums, MlDigsiteMechanic.Volatile, 0.25, 1.30), T0);

        /// <summary>Drains every step due at T0 + <paramref name="second"/>, in queue order.</summary>
        private static void RunSecond(MlDigsiteBossMechanicState state, int second)
        {
            var due = state.TakeDueSteps(T0.AddSeconds(second));

            if (due == null)
                return;

            foreach (var step in due)
                step();
        }

        // ---- 1. realm-wide opening broadcast: retired outright (owner ruling 2026-09-24) --------------------
        //
        // Round 17 briefly sent two realm-wide "[World Event]" opening lines through
        // WorldEventAnnouncer.BroadcastToRealm. OWNER RULING (2026-09-24): "Treasure map events should not
        // have world event announcements or progress updates." AnnounceOpening is gone outright, not merely
        // rescoped back to a radius - every digsite line, including the opening one, now goes through
        // MlDigsiteManager.Announce, which was always radius-scoped.

        [TestMethod]
        public void AnnounceOpening_no_longer_exists_and_no_digsite_line_is_realm_wide()
        {
            Assert.IsNull(typeof(MlDigsiteManager).GetMethod("AnnounceOpening", BindingFlags.NonPublic | BindingFlags.Static),
                "AnnounceOpening must no longer exist");

            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");

            // Code lines only: a comment is free to keep discussing the retired feature by name (and does,
            // in the remarks right above the method this file used to pin), so the check must not trip on
            // prose. Strip any line whose trimmed text opens with a comment marker before scanning.
            var codeOnly = string.Join("\n", src.Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();
                    return !trimmed.StartsWith("//") && !trimmed.StartsWith("///")
                        && !trimmed.StartsWith("*") && !trimmed.StartsWith("/*");
                }));

            Assert.IsFalse(codeOnly.Contains("BroadcastToRealm("), "no digsite code may broadcast realm-wide any more");
            Assert.IsFalse(codeOnly.Contains("\"[World Event]"), "no digsite line may carry the World Event prefix");
        }

        [TestMethod]
        public void The_broadcast_radius_tunable_is_retired_everywhere()
        {
            Assert.IsFalse(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("ml_digsite_broadcast_radius_metres"));
            Assert.IsNull(typeof(MlDigsiteTunables).GetProperty("BroadcastRadiusMetres", BindingFlags.Public | BindingFlags.Static));

            // The audience radius is still read by everything else and stays.
            Assert.AreEqual(60.0, DefaultPropertyManager.DefaultDoubleProperties["ml_digsite_audience_radius_metres"].Item, 1e-9);

            var migration = PooledLootSourceText.Read("Database/Updates/Shard/2026-09-22-02-MlDigsiteRetireBroadcastRadius.sql");
            StringAssert.Contains(migration, "DELETE FROM `config_properties_double` WHERE `key` = 'ml_digsite_broadcast_radius_metres';");
        }

        // ---- 2. doubloons: 2 per map ------------------------------------------------------------------------

        [TestMethod]
        public void Doubloon_defaults_are_2_per_chest_and_none_from_the_dig()
        {
            Assert.AreEqual(2L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_chest_doubloons"].Item);
            Assert.AreEqual(0L, DefaultPropertyManager.DefaultLongProperties["ml_treasure_payout_min"].Item);
            Assert.AreEqual(0L, DefaultPropertyManager.DefaultLongProperties["ml_treasure_payout_max"].Item);
        }

        [TestMethod]
        public void A_full_clear_chest_pays_2_doubloons()
        {
            Assert.AreEqual(2L, MlDigsiteRules.ScaleCurrency(2, 1.0));
        }

        [TestMethod]
        public void Any_partial_clear_chest_pays_at_least_1_doubloon()
        {
            // Every positive fraction a chest can be paid at, from the lowest a fail can reach (the wave-1
            // floor times the fail multiplier) up to just short of a win.
            foreach (var fraction in new[] { 0.0001, 0.1, 0.15 * 0.85, 0.25, 0.33, 0.5, 0.74 })
                Assert.IsTrue(MlDigsiteRules.ScaleCurrency(2, fraction) >= 1, $"fraction {fraction} paid no doubloon");

            Assert.AreEqual(1L, MlDigsiteRules.ScaleCurrency(2, 0.25), "0.5 rounds (to even) to 0 and is floored to 1");
            Assert.AreEqual(1L, MlDigsiteRules.ScaleCurrency(2, 0.87 * 0.85), "the best non-win Waves fraction: 1.479 rounds to 1");
            Assert.AreEqual(2L, MlDigsiteRules.ScaleCurrency(2, 0.75), "1.5 rounds (to even) to 2");
        }

        [TestMethod]
        public void A_zero_dig_roll_creates_no_currency()
        {
            Assert.IsFalse(TreasureMapHandler.PaysCurrency(0));
            Assert.IsFalse(TreasureMapHandler.PaysCurrency(-1));
            Assert.IsTrue(TreasureMapHandler.PaysCurrency(1));
        }

        [TestMethod]
        public void FinishDig_gates_the_currency_object_and_the_coin_line_on_a_paying_roll_but_still_consumes_and_opens()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlTreasure/TreasureMapHandler.cs");
            var body = PooledLootSourceText.MethodBody(src, "private static void FinishDig(WorldObject wo, Player player)");

            var gate = body.IndexOf("if (PaysCurrency(count))", StringComparison.Ordinal);
            var create = body.IndexOf("WorldObjectFactory.CreateNewWorldObject(TreasureCurrencyWcid)", StringComparison.Ordinal);
            var stack = body.IndexOf("currency.SetStackSize(count);", StringComparison.Ordinal);
            var line = body.IndexOf("You unearth {count} treasure coins!", StringComparison.Ordinal);
            var full = body.IndexOf("Your pack is too full to hold the treasure.", StringComparison.Ordinal);
            var start = body.IndexOf("MlDigsite.MlDigsiteManager.TryStart(player, forcedType, forcedMechanicSetId, out var startRefusal,", StringComparison.Ordinal);
            var lastConsume = body.LastIndexOf("player.TryConsumeFromInventoryWithNetworking(wo, 1)", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "the zero-roll gate is missing");
            Assert.IsTrue(create > gate && stack > gate && line > gate && full > gate,
                "the currency object, its stack size, the coin line and the full-pack refusal must all sit behind the gate");
            Assert.IsTrue(lastConsume > line, "the ordinary branch's map consumption must come after (outside) the payout block");
            Assert.IsTrue(start > lastConsume, "the digsite encounter must still open after a zero roll");

            StringAssert.Contains(body, "PropertyManager.GetLong(\"ml_treasure_payout_min\", 0)");
            StringAssert.Contains(body, "PropertyManager.GetLong(\"ml_treasure_payout_max\", 0)");
        }

        // ---- 3. corruption power gain -------------------------------------------------------------------------

        [TestMethod]
        public void Corruption_power_defaults_are_3_percent_every_5_seconds()
        {
            Assert.AreEqual(0.03, DefaultPropertyManager.DefaultDoubleProperties["ml_digsite_corruption_power_per_tick"].Item, 1e-9);
            Assert.AreEqual(5L, DefaultPropertyManager.DefaultLongProperties["ml_digsite_corruption_power_tick_seconds"].Item);
        }

        [TestMethod]
        public void The_first_tick_for_a_Corrupted_mob_only_arms_the_clock_then_one_gain_per_interval()
        {
            var encounter = NewEncounter();
            var interval = TimeSpan.FromSeconds(5);
            const uint corrupted = 0x80001000;

            Assert.IsFalse(encounter.TryClaimCorruptionPowerTick(corrupted, true, T0, interval, out var s0));
            Assert.AreEqual(0, s0);

            Assert.IsFalse(encounter.TryClaimCorruptionPowerTick(corrupted, true, T0.AddSeconds(4), interval, out _), "not yet due");

            Assert.IsTrue(encounter.TryClaimCorruptionPowerTick(corrupted, true, T0.AddSeconds(5), interval, out var s1));
            Assert.AreEqual(1, s1);

            Assert.IsFalse(encounter.TryClaimCorruptionPowerTick(corrupted, true, T0.AddSeconds(6), interval, out _), "one gain per interval");

            Assert.IsTrue(encounter.TryClaimCorruptionPowerTick(corrupted, true, T0.AddSeconds(10), interval, out var s2));
            Assert.AreEqual(2, s2);
            Assert.AreEqual(2, encounter.CorruptionPowerStacks);
        }

        [TestMethod]
        public void The_power_resets_to_zero_when_the_Corrupted_mob_dies_and_the_next_one_starts_fresh()
        {
            var encounter = NewEncounter();
            var interval = TimeSpan.FromSeconds(5);
            const uint first = 0x80001000;
            const uint second = 0x80002000;

            encounter.TryClaimCorruptionPowerTick(first, true, T0, interval, out _);
            encounter.TryClaimCorruptionPowerTick(first, true, T0.AddSeconds(5), interval, out _);
            encounter.TryClaimCorruptionPowerTick(first, true, T0.AddSeconds(10), interval, out _);
            encounter.TryClaimCorruptionPowerTick(first, true, T0.AddSeconds(15), interval, out _);
            Assert.AreEqual(3, encounter.CorruptionPowerStacks);

            // OnCorruptedDied
            Assert.AreEqual(3, encounter.ResetCorruptionPower(), "the dropped stacks are reported so the field is written back");
            Assert.AreEqual(0, encounter.CorruptionPowerStacks);

            // No Corrupted mob alive between kills: nothing gains.
            Assert.IsFalse(encounter.TryClaimCorruptionPowerTick(0, false, T0.AddSeconds(20), interval, out _));

            // The next one arms its own clock and builds from zero.
            Assert.IsFalse(encounter.TryClaimCorruptionPowerTick(second, true, T0.AddSeconds(21), interval, out var armed));
            Assert.AreEqual(0, armed);
            Assert.IsTrue(encounter.TryClaimCorruptionPowerTick(second, true, T0.AddSeconds(26), interval, out var fresh));
            Assert.AreEqual(1, fresh);
        }

        [TestMethod]
        public void A_different_Corrupted_mob_resets_the_stacks_even_without_the_death_hook()
        {
            var encounter = NewEncounter();
            var interval = TimeSpan.FromSeconds(5);

            encounter.TryClaimCorruptionPowerTick(0x80001000, true, T0, interval, out _);
            encounter.TryClaimCorruptionPowerTick(0x80001000, true, T0.AddSeconds(5), interval, out _);
            Assert.AreEqual(1, encounter.CorruptionPowerStacks);

            Assert.IsFalse(encounter.TryClaimCorruptionPowerTick(0x80002000, true, T0.AddSeconds(10), interval, out var stacks));
            Assert.AreEqual(0, stacks);
            Assert.AreEqual(0, encounter.CorruptionPowerStacks);
        }

        [TestMethod]
        public void Nothing_gains_after_the_encounter_ends_or_with_the_gain_switched_off()
        {
            var ended = NewEncounter();
            ended.TryClaimCorruptionPowerTick(0x80001000, true, T0, TimeSpan.FromSeconds(5), out _);
            ended.MarkEnded("test", out _);
            Assert.IsFalse(ended.TryClaimCorruptionPowerTick(0x80001000, true, T0.AddSeconds(60), TimeSpan.FromSeconds(5), out _));

            var off = NewEncounter();
            Assert.IsFalse(off.TryClaimCorruptionPowerTick(0x80001000, true, T0, TimeSpan.Zero, out _));
            Assert.IsFalse(off.TryClaimCorruptionPowerTick(0x80001000, true, T0.AddSeconds(60), TimeSpan.Zero, out _));
        }

        [TestMethod]
        public void The_field_rating_is_recomputed_from_its_base_so_it_grows_linearly_and_a_reset_restores_the_base()
        {
            var encounter = NewEncounter();
            const uint field = 0x80003000;

            // First write records the spawn rating (crowd/wave scaling included); later writes ignore the
            // creature's already-raised current rating.
            Assert.AreEqual(10, encounter.BaseDamageRating(field, 10));
            Assert.AreEqual(10, encounter.BaseDamageRating(field, 16), "the base is recorded once");

            Assert.AreEqual(13, MlDigsiteRules.DamageRatingFor(encounter.BaseDamageRating(field, 13), 1, 0.03));
            Assert.AreEqual(16, MlDigsiteRules.DamageRatingFor(encounter.BaseDamageRating(field, 13), 2, 0.03));
            Assert.AreEqual(10, MlDigsiteRules.DamageRatingFor(encounter.BaseDamageRating(field, 16), 0, 0.03), "stacks 0 writes the base back");
        }

        [TestMethod]
        public void Each_gain_is_announced_exactly_once_with_the_owner_text()
        {
            var encounter = NewEncounter();
            var interval = TimeSpan.FromSeconds(5);
            const uint corrupted = 0x80001000;

            var lines = new List<string>();

            for (var second = 0; second <= 16; second++)
            {
                if (!encounter.TryClaimCorruptionPowerTick(corrupted, true, T0.AddSeconds(second), interval, out var stacks))
                    continue;

                var before = MlDigsiteRules.CorruptionPowerPercent(stacks - 1, 0.03);
                var after = MlDigsiteRules.CorruptionPowerPercent(stacks, 0.03);

                if (MlDigsiteRules.ShouldAnnounceCorruptionGain(before, after))
                    lines.Add(MlDigsiteRules.CorruptionPowerLine(after));
            }

            CollectionAssert.AreEqual(new[]
            {
                "The corruption swells - the dig's creatures now strike 3% harder.",
                "The corruption swells - the dig's creatures now strike 6% harder.",
                "The corruption swells - the dig's creatures now strike 9% harder.",
            }, lines);
        }

        [TestMethod]
        public void At_the_growth_cap_a_further_tick_is_not_announced()
        {
            // The cap is MlDigsiteRules.MaxDamageRatingGrowth = +300%.
            Assert.AreEqual(300L, MlDigsiteRules.MaxDamageRatingGrowth);
            Assert.AreEqual(300, MlDigsiteRules.CorruptionPowerPercent(100, 0.03));
            Assert.AreEqual(300, MlDigsiteRules.CorruptionPowerPercent(101, 0.03));

            Assert.IsTrue(MlDigsiteRules.ShouldAnnounceCorruptionGain(297, 300));
            Assert.IsFalse(MlDigsiteRules.ShouldAnnounceCorruptionGain(300, 300));
        }

        [TestMethod]
        public void The_manager_wires_the_power_gain_its_reset_and_its_removal_at_encounter_end()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");

            var drive = PooledLootSourceText.MethodBody(src, "private static void DriveCorruption(MlDigsiteEncounter encounter, DateTime now)");
            StringAssert.Contains(drive, "DriveCorruptionPower(encounter, now);");

            var power = PooledLootSourceText.MethodBody(src, "private static void DriveCorruptionPower(MlDigsiteEncounter encounter, DateTime now)");
            StringAssert.Contains(power, "Announce(encounter, MlDigsiteRules.CorruptionPowerLine(after), ChatMessageType.WorldBroadcast);");
            StringAssert.Contains(power, "HardenField(encounter, perTick, onlyWhileRunning: true);");

            var died = PooledLootSourceText.MethodBody(src, "private static void OnCorruptedDied(MlDigsiteEncounter encounter, DateTime now)");
            var reset = died.IndexOf("encounter.ResetCorruptionPower()", StringComparison.Ordinal);
            var respawn = died.IndexOf("MlDigsiteSpawner.TrySpawn(encounter, MlDigsiteRole.Priority", StringComparison.Ordinal);
            var won = died.IndexOf("MlDigsiteRules.CorruptionKillsWon(kills, required)", StringComparison.Ordinal);
            Assert.IsTrue(reset >= 0 && reset < won && reset < respawn, "the reset must run first, before the win check and the next spawn");

            var finish = PooledLootSourceText.MethodBody(src, "private static void Finish(MlDigsiteEncounter encounter, MlDigsiteResult result, string reason)");
            StringAssert.Contains(finish, "HardenField(encounter, MlDigsiteTunables.CorruptionPowerPerTick, onlyWhileRunning: false);");
            Assert.IsTrue(finish.IndexOf("ResetCorruptionPower()", StringComparison.Ordinal) < finish.IndexOf("DestroyHeld(encounter);", StringComparison.Ordinal),
                "the bonus is removed before the field is destroyed");

            var harden = PooledLootSourceText.MethodBody(src, "private static void HardenField(MlDigsiteEncounter encounter, double perTick, bool onlyWhileRunning)");
            StringAssert.Contains(harden, "encounter.LiveWaveSnapshot()", "the field only - never the Corrupted mob itself");
            StringAssert.Contains(harden, "MlDigsiteRules.DamageRatingFor(baseRating, encounter.CorruptionPowerStacks, perTick)");

            var reinforce = PooledLootSourceText.MethodBody(src, "private static void SpawnMoreField(MlDigsiteEncounter encounter)");
            StringAssert.Contains(reinforce, "if (encounter.CorruptionPowerStacks > 0)", "reinforcements spawned while the bonus is active get it at once");
        }

        // ---- 4. Corrupted mob health ------------------------------------------------------------------------

        [TestMethod]
        public void Corrupted_health_multiplier_is_6()
        {
            Assert.AreEqual(6.0, DefaultPropertyManager.DefaultDoubleProperties["ml_digsite_corrupted_health_multiplier"].Item, 1e-9);

            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteTunables.cs");
            StringAssert.Contains(src, "PropertyManager.GetDouble(\"ml_digsite_corrupted_health_multiplier\", 6.0)");
        }

        // ---- 5. burning Volley ------------------------------------------------------------------------------

        [TestMethod]
        public void The_shipped_burn_is_150_fire_every_second_for_8_seconds()
        {
            var args = MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultDrums);

            Assert.AreEqual(150.0, args.GetDouble("burndamage", -1), 1e-9);
            Assert.AreEqual(8.0, args.GetDouble("burnseconds", -1), 1e-9);
            Assert.AreEqual(1.0, args.GetDouble("burninterval", -1), 1e-9);

            Assert.AreEqual(MlDigsiteBossMechanicRules.DefaultDrums,
                DefaultPropertyManager.DefaultStringProperties["ml_digsite_bossrush_drums"].Item);
        }

        [TestMethod]
        public void BurnTickCount_is_one_per_interval_and_zero_when_switched_off()
        {
            Assert.AreEqual(8, MlDigsiteBossMechanicRules.BurnTickCount(8, 1));
            Assert.AreEqual(2, MlDigsiteBossMechanicRules.BurnTickCount(8, 3));
            Assert.AreEqual(0, MlDigsiteBossMechanicRules.BurnTickCount(0, 1));
            Assert.AreEqual(0, MlDigsiteBossMechanicRules.BurnTickCount(8, 0));
            Assert.AreEqual(0, MlDigsiteBossMechanicRules.BurnTickCount(double.NaN, 1));
        }

        [TestMethod]
        public void InsideAnyBurn_uses_the_patch_radius_and_the_same_instance()
        {
            var patches = new List<Position> { At(1, MlOutdoorCell, 60f, 110f), At(1, MlOutdoorCell, 80f, 110f) };

            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideAnyBurn(At(1, MlOutdoorCell, 62f, 111f), patches, 4.0), "inside the first patch");
            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideAnyBurn(At(1, MlOutdoorCell, 83f, 110f), patches, 4.0), "inside the second patch");
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideAnyBurn(At(1, MlOutdoorCell, 70f, 110f), patches, 4.0), "between the patches");
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideAnyBurn(At(2, MlOutdoorCell, 60f, 110f), patches, 4.0), "another instance");
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideAnyBurn(null, patches, 4.0));
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideAnyBurn(At(1, MlOutdoorCell, 60f, 110f), patches, 0.0));
        }

        /// <summary>
        /// The acceptance criterion: burning ground DEMONSTRABLY deals damage every second a player stands in
        /// it. Drives the real schedule (DrumCadenceMechanic.ScheduleBurn, the call StartBurn makes) a second
        /// at a time and resolves occupancy with the real predicate BurnTick uses.
        /// </summary>
        [TestMethod]
        public void A_player_standing_in_the_fire_for_the_whole_burn_takes_one_tick_every_second()
        {
            var state = NewState();
            var patches = new List<Position> { At(1, MlOutdoorCell, 60f, 110f) };
            var standing = At(1, MlOutdoorCell, 61f, 110f);

            var hitsBySecond = new Dictionary<int, int>();
            var currentSecond = 0;
            var ended = -1;

            var ticks = DrumCadenceMechanic.ScheduleBurn(state, T0, 8, 1,
                tick =>
                {
                    if (MlDigsiteBossMechanicRules.InsideAnyBurn(standing, patches, 4.0))
                        hitsBySecond[currentSecond] = hitsBySecond.TryGetValue(currentSecond, out var n) ? n + 1 : 1;
                },
                () => ended = currentSecond);

            Assert.AreEqual(8, ticks);

            for (currentSecond = 0; currentSecond <= 12; currentSecond++)
                RunSecond(state, currentSecond);

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, hitsBySecond.Keys.ToArray(), "one tick in each of seconds 1..8, none after");
            Assert.IsTrue(hitsBySecond.Values.All(n => n == 1), "exactly one hit per second");
            Assert.AreEqual(8, ended, "the patch clears at the end of its duration");
        }

        [TestMethod]
        public void Stepping_out_of_the_fire_stops_the_damage_and_stepping_back_in_resumes_it()
        {
            var state = NewState();
            var patches = new List<Position> { At(1, MlOutdoorCell, 60f, 110f) };

            var inside = At(1, MlOutdoorCell, 61f, 110f);
            var outside = At(1, MlOutdoorCell, 70f, 110f);

            var player = inside;
            var hits = new List<int>();

            DrumCadenceMechanic.ScheduleBurn(state, T0, 8, 1,
                tick =>
                {
                    if (MlDigsiteBossMechanicRules.InsideAnyBurn(player, patches, 4.0))
                        hits.Add(tick);
                },
                null);

            for (var second = 1; second <= 8; second++)
            {
                // In for seconds 1-3, out for 4-6, back in for 7-8.
                player = second <= 3 || second >= 7 ? inside : outside;
                RunSecond(state, second);
            }

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 7, 8 }, hits);
        }

        [TestMethod]
        public void Ending_the_encounter_drops_every_remaining_burn_tick()
        {
            var state = NewState();
            var fired = 0;

            DrumCadenceMechanic.ScheduleBurn(state, T0, 8, 1, _ => fired++, null);

            RunSecond(state, 1);
            RunSecond(state, 2);

            state.ClearSteps();   // MlDigsiteBossMechanics.Stop, from the encounter's cleanup

            for (var second = 3; second <= 10; second++)
                RunSecond(state, second);

            Assert.AreEqual(2, fired);
        }

        [TestMethod]
        public void The_Volley_resolve_hands_its_markers_to_the_burn_and_the_burn_deals_fire()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Mechanics/DrumCadenceMechanic.cs");

            var resolve = PooledLootSourceText.MethodBody(src, "private static void Resolve(MlDigsiteMechanicContext ctx, MlDigsiteDrumShape shape, DateTime resolveAt)");
            StringAssert.Contains(resolve, "hit += ctx.RadialHit(mark, radius, damage, DamageType.Bludgeon);", "the initial resolve hit is unchanged");
            StringAssert.Contains(resolve, "StartBurn(ctx, marks, ctx.State.TakeMarkers(), radius, resolveAt);");

            var tick = PooledLootSourceText.MethodBody(src, "private static void BurnTick(MlDigsiteMechanicContext ctx, IReadOnlyList<Position> patches, double radius, double damage, int tick)");
            StringAssert.Contains(tick, "MlDigsiteBossMechanicRules.InsideAnyBurn(player?.Location, patches, radius)");
            StringAssert.Contains(tick, "ctx.Hit(player, damage, DamageType.Fire);");
        }

        // ---- 7. the interrupt drum ---------------------------------------------------------------------------

        [TestMethod]
        public void The_drum_stands_15_m_out_with_a_20_second_window_and_a_2_second_flare()
        {
            var args = MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultInterrupt);

            Assert.AreEqual(20.0, args.GetDouble("window", -1), 1e-9);
            Assert.AreEqual(15.0, args.GetDouble("distance", -1), 1e-9);
            Assert.AreEqual(2.0, args.GetDouble("pulse", -1), 1e-9);
            Assert.AreEqual(0.35, args.GetDouble("damage", -1), 1e-9, "the miss is unchanged");

            Assert.AreEqual(MlDigsiteBossMechanicRules.DefaultInterrupt,
                DefaultPropertyManager.DefaultStringProperties["ml_digsite_bossrush_interrupt"].Item);

            // 15 m survives the tether clamp unchanged at the shipped 40 m tether.
            Assert.AreEqual(15.0, MlDigsiteBossMechanicRules.ClampInterruptDistance(15.0, 40.0), 1e-9);

            // The mechanic's own fallbacks agree with the string.
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Mechanics/InterruptObjectMechanic.cs");
            StringAssert.Contains(src, "private const double DefaultWindow = 20.0;");
            StringAssert.Contains(src, "private const double DefaultDistance = 15.0;");
            StringAssert.Contains(src, "private const double DefaultPulse = 2.0;");
        }

        [TestMethod]
        public void The_drum_flare_is_a_script_its_own_effect_table_carries()
        {
            // PhysicsEffectTable 0x3400002B (the drum's) maps EnchantUpYellow; it does NOT map the
            // RestrictionEffectBlue the drum was stamped with before round 17 (DatLoader read, 2026-09-22).
            Assert.AreEqual(PlayScript.EnchantUpYellow, InterruptObjectMechanic.DrumFlare);
        }

        [TestMethod]
        public void The_drum_flare_repeats_on_its_interval_only_while_a_built_drum_stands_in_an_open_window()
        {
            var state = NewState();

            Assert.IsFalse(state.TryClaimInterruptPulse(T0, 2), "no window open");

            Assert.IsTrue(state.TryOpenInterrupt(T0.AddSeconds(20)));
            Assert.IsFalse(state.TryClaimInterruptPulse(T0, 2), "the drum has not been built yet");

            state.SetInterruptObject(NewDrum());

            Assert.IsTrue(state.TryClaimInterruptPulse(T0.AddSeconds(1), 2), "due at once for a freshly built drum");
            Assert.IsFalse(state.TryClaimInterruptPulse(T0.AddSeconds(2), 2));
            Assert.IsTrue(state.TryClaimInterruptPulse(T0.AddSeconds(3), 2));
            Assert.IsFalse(state.TryClaimInterruptPulse(T0.AddSeconds(4), 2));
            Assert.IsTrue(state.TryClaimInterruptPulse(T0.AddSeconds(5), 2));

            Assert.IsTrue(state.TryCloseInterrupt(out _));
            Assert.IsFalse(state.TryClaimInterruptPulse(T0.AddSeconds(9), 2), "no flare once the window has closed");
        }

        [TestMethod]
        public void Every_rejected_drum_use_is_logged_with_its_reason()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteInterruptObject.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static bool TryHandleUse(WorldObject wo, Player player)");

            Assert.AreEqual(3, Regex.Matches(body, Regex.Escape("LogRejected(wo, player, stamp.Value,")).Count,
                "the ended-encounter, no-matching-window and window-already-closed refusals are each logged");

            StringAssert.Contains(body, "\"no-open-window\"");
            StringAssert.Contains(body, "\"window-already-closed\"");
            StringAssert.Contains(body, "\"encounter-not-live\"");
        }

        [TestMethod]
        public void The_drum_weenie_is_bigger_and_keeps_its_two_load_bearing_rows()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1005950 Aun Signal Drum.sql");

            StringAssert.Contains(sql, "(1005950, 39,   4) /* DefaultScale");
            StringAssert.Contains(sql, "(1005950,  16,       32) /* ItemUseable - Remote */");
            StringAssert.Contains(sql, "(1005950, 54,   3) /* UseRadius");
        }

        private static WorldObject NewDrum()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1005950,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Misc } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Aun Signal Drum" } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }
    }
}
