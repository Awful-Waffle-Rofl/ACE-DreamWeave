using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Gambler's Cache encounter type: its config, the locked lifecycle (GambleState: deepen, take, clear, bust, the
    /// token), the tick outcome table, Confirmation_Choice's Yes / explicit No / timeout split, the per-member payout,
    /// the shipped encounters.json row, the run and point filters, a Place that refuses cleanly, and source-scan pins for
    /// the wiring that needs a live world (the bonus-kill spawn, the payout, the bust test, the registration). Player-free:
    /// the state is driven with tokens and the payout through its seams. Placement into a live world (EnterWorld, the
    /// portals, the ActionChains) is left to the in-game checks. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class GambleEncounterTypeTests
    {
        private static JsonElement Json(string text)
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        private static GambleConfig Parse(string text, out string error)
            => GambleEncounterType.ParseConfig(text == null ? default : Json(text), out error);

        private static string WavesJson(params (int P, int E, int S)[] waves)
            => "{\"waves\":[" + string.Join(",", waves.Select(w => $"{{\"portals\":{{\"min\":{w.P},\"max\":{w.P}}},\"perPortal\":{{\"min\":{w.E},\"max\":{w.E}}},\"seconds\":{w.S}}}")) + "]}";

        private static GambleState NewState() => new GambleState(null, new GambleConfig(), null);

        /// <summary>Takes a fresh state through Deepen and clear for <paramref name="waves"/> waves; returns it Ready(waves) (or Paid at 4).</summary>
        private static GambleState ClearedThrough(int waves)
        {
            var state = NewState();

            for (var k = 1; k <= waves; k++)
            {
                Assert.AreEqual(GambleUseOutcome.Offer, state.TryUse(false, true, out var cleared, out var token));
                Assert.AreEqual(k - 1, cleared);
                Assert.IsTrue(state.TryDeepen(token, 100.0 * k, out var wave));
                Assert.AreEqual(k, wave);
                Assert.IsTrue(state.BeginStream(k, 0));
                Assert.IsTrue(state.TryClearWave(k, out _, out _));
            }

            return state;
        }

        // ---- config ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void An_absent_config_takes_the_shipped_defaults()
        {
            foreach (var text in new[] { null, "{}" })
            {
                var config = Parse(text, out var error);
                Assert.IsNull(error, text);
                Assert.AreEqual(1007980u, config.ChestWcid);
                Assert.AreEqual(8f, config.PortalMinDistance);
                Assert.AreEqual(25f, config.PortalMaxDistance);
                Assert.AreEqual(0.3, config.PortalScaleDungeon);
                Assert.AreEqual(0.6, config.PortalScaleOutdoor);
                Assert.AreEqual(4, config.Waves.Count);

                var rows = config.Waves.Select(w => (w.PortalsMin, w.PortalsMax, w.PerPortalMin, w.PerPortalMax, w.Seconds)).ToArray();
                CollectionAssert.AreEqual(new[] { (1, 1, 2, 3, 60.0), (1, 1, 3, 4, 75.0), (2, 2, 2, 2, 75.0), (2, 2, 3, 3, 105.0) }, rows);
                Assert.AreEqual(17, GambleConfig.MaxPossibleTotal(config.Waves), "1x3 + 1x4 + 2x2 + 2x3");

                // Every wave carries the row's distances and scale, and Defense's stagger.
                Assert.IsTrue(config.Waves.All(w => w.PortalMinDistance == 8f && w.PortalMaxDistance == 25f && w.StaggerMin == 2.0 && w.StaggerMax == 4.0));
                Assert.AreEqual(0.3, config.Waves[0].PortalScale(false));
                Assert.AreEqual(0.6, config.Waves[0].PortalScale(true));
            }
        }

        [TestMethod]
        public void The_placement_view_needs_the_neediest_wave()
        {
            var placement = new GambleConfig().Placement;
            Assert.AreEqual(2, placement.PortalsMin, "waves 3 and 4 need two portals");
            Assert.AreEqual(2, placement.PortalsMax);
            Assert.AreEqual(8f, placement.PortalMinDistance);
            Assert.AreEqual(25f, placement.PortalMaxDistance);
        }

        [TestMethod]
        public void Overrides_apply_and_a_partial_wave_row_keeps_that_waves_defaults()
        {
            var config = Parse("{\"chestWcid\":1234,\"portalMinDistance\":5,\"portalMaxDistance\":30,\"portalScale\":{\"dungeon\":0.5}," +
                               "\"waves\":[{\"seconds\":45},{},{\"perPortal\":{\"max\":3}},{\"portals\":{\"min\":1}}]}", out var error);
            Assert.IsNull(error);
            Assert.AreEqual(1234u, config.ChestWcid);
            Assert.AreEqual(0.5, config.PortalScaleDungeon);
            Assert.AreEqual(0.6, config.PortalScaleOutdoor);
            Assert.AreEqual(45.0, config.Waves[0].Seconds);
            Assert.AreEqual((1, 1, 2, 3), (config.Waves[0].PortalsMin, config.Waves[0].PortalsMax, config.Waves[0].PerPortalMin, config.Waves[0].PerPortalMax));
            Assert.AreEqual(75.0, config.Waves[1].Seconds);
            Assert.AreEqual((2, 3), (config.Waves[2].PerPortalMin, config.Waves[2].PerPortalMax));
            Assert.AreEqual((1, 2), (config.Waves[3].PortalsMin, config.Waves[3].PortalsMax));
            Assert.IsTrue(config.Waves.All(w => w.PortalMinDistance == 5f && w.PortalMaxDistance == 30f && w.PortalScaleDungeon == 0.5));
        }

        [TestMethod]
        public void A_bad_or_unknown_key_is_an_error()
        {
            var bad = new[]
            {
                "[]",
                "{\"wcid\":1}",
                "{\"chestWcid\":0}",
                "{\"chestWcid\":\"x\"}",
                "{\"portalMinDistance\":30,\"portalMaxDistance\":20}",
                "{\"portalMaxDistance\":61}",
                "{\"portalScale\":{\"dungeon\":0}}",
                "{\"portalScale\":{\"dungeon\":0.3,\"cave\":1}}",
                "{\"waves\":{}}",
                "{\"waves\":[{},{},{},{\"extra\":1}]}",
                "{\"waves\":[{},{},{},{\"portals\":{\"min\":1,\"max\":2,\"mid\":1}}]}",
                "{\"waves\":[{},{},{},5]}",
                "{\"waves\":[{\"portals\":{\"min\":0,\"max\":1}},{},{},{}]}",
                "{\"waves\":[{\"portals\":{\"min\":1,\"max\":4}},{},{},{}]}",
                "{\"waves\":[{\"perPortal\":{\"min\":1,\"max\":7}},{},{},{}]}",
                "{\"waves\":[{\"perPortal\":{\"min\":3,\"max\":2}},{},{},{}]}",
                "{\"waves\":[{\"seconds\":9},{},{},{}]}",
                "{\"waves\":[{\"seconds\":601},{},{},{}]}",
                "{\"waves\":[{\"seconds\":\"60\"},{},{},{}]}",
            };

            foreach (var text in bad)
            {
                Assert.IsNull(Parse(text, out var error), text);
                Assert.IsFalse(string.IsNullOrEmpty(error), text);
            }

            Parse("{\"waves\":[{},{},{},{\"bogus\":1}]}", out var unknown);
            Assert.AreEqual("config.waves[3]: unknown key 'bogus'", unknown);
        }

        [TestMethod]
        public void Waves_must_be_exactly_four_rows()
        {
            foreach (var n in new[] { 0, 1, 3, 5 })
            {
                var text = "{\"waves\":[" + string.Join(",", Enumerable.Repeat("{}", n)) + "]}";
                Assert.IsNull(Parse(text, out var error), text);
                Assert.AreEqual("config.waves must be an array of exactly 4 wave rows", error);
            }

            Assert.IsNotNull(Parse("{\"waves\":[{},{},{},{}]}", out _));
        }

        [TestMethod]
        public void A_wave_set_that_could_send_more_than_24_enemies_is_refused()
        {
            // 3x3 + 3x3 + 1x5 + 1x1 = 24: allowed.
            var at = Parse(WavesJson((3, 3, 60), (3, 3, 60), (1, 5, 60), (1, 1, 60)), out var error);
            Assert.IsNull(error);
            Assert.AreEqual(24, GambleConfig.MaxPossibleTotal(at.Waves));

            // 3x3 + 3x3 + 1x6 + 1x1 = 25: refused.
            Assert.IsNull(Parse(WavesJson((3, 3, 60), (3, 3, 60), (1, 6, 60), (1, 1, 60)), out error));
            Assert.AreEqual("config.waves could send up to 25 enemies; at most 24 across the 4 waves", error);

            // The max counts, not the min: perPortal 1..6 on three portals in every wave.
            Assert.IsNull(Parse("{\"waves\":[" + string.Join(",", Enumerable.Repeat("{\"portals\":{\"min\":1,\"max\":3},\"perPortal\":{\"min\":1,\"max\":6}}", 4)) + "]}", out _));
        }

        [TestMethod]
        public void The_shipped_row_parses_to_the_defaults()
        {
            var json = PooledLootSourceText.Read("Content/dungeons/dynamic/encounters.json");

            using var doc = JsonDocument.Parse(json);
            var row = doc.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "gamble");

            Assert.AreEqual("gamble", row.GetProperty("kind").GetString());
            Assert.IsTrue(row.GetProperty("enabled").GetBoolean());
            Assert.AreEqual(1.0, row.GetProperty("weight").GetDouble());
            Assert.AreEqual(1, row.GetProperty("maxPerRun").GetInt32(), "one cache per run");

            var placement = row.GetProperty("placement");
            Assert.AreEqual((3.0, 30.0, 30.0, 30.0), (placement.GetProperty("minClearance").GetDouble(), placement.GetProperty("minEntryWalk").GetDouble(),
                placement.GetProperty("minBossDist").GetDouble(), placement.GetProperty("minSpacing").GetDouble()));
            CollectionAssert.AreEqual(new[] { "dungeon", "outdoor" }, placement.GetProperty("runKinds").EnumerateArray().Select(e => e.GetString()).ToArray());

            var shipped = GambleEncounterType.ParseConfig(row.GetProperty("config").Clone(), out var error);
            Assert.IsNull(error);

            var defaults = new GambleConfig();
            Assert.AreEqual(defaults.ChestWcid, shipped.ChestWcid);
            Assert.AreEqual((defaults.PortalMinDistance, defaults.PortalMaxDistance, defaults.PortalScaleDungeon, defaults.PortalScaleOutdoor),
                (shipped.PortalMinDistance, shipped.PortalMaxDistance, shipped.PortalScaleDungeon, shipped.PortalScaleOutdoor));
            CollectionAssert.AreEqual(defaults.Waves.Select(w => (w.PortalsMin, w.PortalsMax, w.PerPortalMin, w.PerPortalMax, w.Seconds)).ToArray(),
                shipped.Waves.Select(w => (w.PortalsMin, w.PortalsMax, w.PerPortalMin, w.PerPortalMax, w.Seconds)).ToArray());

            // The whole file still loads through the store with the new row.
            var problems = new List<string>();
            var store = ThreadEncounterStore.Parse(json, null, problems);
            Assert.IsTrue(store.Types.Any(t => t.Id == "gamble" && t.Kind == "gamble" && t.MaxPerRun == 1), string.Join("\n", problems));
            // The peddler is the one other capped row (owner rule: one Wandering Peddler per run); every remaining row stays uncapped.
            Assert.IsTrue(store.Types.Any(t => t.Id == "peddler" && t.MaxPerRun == 1), "the peddler row is capped at one per run");
            Assert.IsTrue(store.Types.Where(t => t.Kind != "gamble" && t.Id != "peddler").All(t => t.MaxPerRun == null), "every other shipped row stays uncapped");
        }

        // ---- the state machine ------------------------------------------------------------------------------------

        [TestMethod]
        public void Take_at_zero_pays_with_nothing_cleared_and_the_cache_is_spent()
        {
            var state = NewState();
            Assert.AreEqual(GamblePhase.Ready, state.Phase);
            Assert.AreEqual(GambleUseOutcome.Offer, state.TryUse(false, true, out var cleared, out var token));
            Assert.AreEqual(0, cleared);

            Assert.IsTrue(state.TryTake(token, out var paid));
            Assert.AreEqual(0, paid);
            Assert.AreEqual(GamblePhase.Paid, state.Phase);
            Assert.IsTrue(state.IsTerminal);
            Assert.AreEqual(GambleUseOutcome.Spent, state.TryUse(false, true, out _, out _));
        }

        [TestMethod]
        public void Deepen_runs_a_wave_and_a_clear_returns_to_ready_with_it_banked()
        {
            var state = NewState();
            state.TryUse(false, true, out _, out var token);

            Assert.IsTrue(state.TryDeepen(token, 1000, out var wave));
            Assert.AreEqual(1, wave);
            Assert.AreEqual(GamblePhase.Wave, state.Phase);
            Assert.AreEqual(1, state.CurrentWave);
            Assert.AreEqual(GambleUseOutcome.WaveInProgress, state.TryUse(false, true, out _, out _), "the cache is sealed mid-wave");
            Assert.IsFalse(state.TryTake(state.Token, out _), "Take is refused mid-wave");
            Assert.IsFalse(state.TryDeepen(state.Token, 1001, out _), "Deepen is refused mid-wave");

            Assert.IsTrue(state.BeginStream(1, 3));
            Assert.IsFalse(state.TryClearWave(2, out _, out _), "only the running wave clears");
            Assert.IsTrue(state.TryClearWave(1, out var autoPay, out var cleared));
            Assert.IsFalse(autoPay);
            Assert.AreEqual(1, cleared);
            Assert.AreEqual(GamblePhase.Ready, state.Phase);
            Assert.AreEqual(1, state.WavesCleared);
            Assert.AreEqual(0, state.CurrentWave);

            Assert.AreEqual(GambleUseOutcome.Offer, state.TryUse(false, true, out var banked, out var next));
            Assert.AreEqual(1, banked);
            Assert.IsTrue(state.TryTake(next, out var paid));
            Assert.AreEqual(1, paid);
        }

        [TestMethod]
        public void The_fourth_clear_pays_out_by_itself_and_no_fifth_deepen_exists()
        {
            var state = ClearedThrough(3);
            Assert.AreEqual(GamblePhase.Ready, state.Phase);
            Assert.AreEqual(3, state.WavesCleared);

            state.TryUse(false, true, out _, out var token);
            Assert.IsTrue(state.TryDeepen(token, 5000, out var wave));
            Assert.AreEqual(4, wave);
            Assert.IsTrue(state.BeginStream(4, 6));
            Assert.IsTrue(state.TryClearWave(4, out var autoPay, out var cleared));
            Assert.IsTrue(autoPay);
            Assert.AreEqual(4, cleared);
            Assert.AreEqual(GamblePhase.Paid, state.Phase);
            Assert.AreEqual(GambleEncounterType.MaxWaves, state.WavesCleared);
            Assert.IsFalse(state.TryDeepen(state.Token, 6000, out _));
            Assert.AreEqual(GambleUseOutcome.Spent, state.TryUse(false, true, out _, out _));
        }

        [TestMethod]
        public void A_bust_is_terminal_and_every_terminal_state_is_inert()
        {
            var bust = NewState();
            bust.TryUse(false, true, out _, out var token);
            Assert.IsTrue(bust.TryDeepen(token, 10, out _));
            Assert.IsFalse(bust.TryBust(2), "only the running wave busts");
            Assert.IsTrue(bust.TryBust(1));
            Assert.AreEqual(GamblePhase.Bust, bust.Phase);
            Assert.AreEqual(0, bust.WavesCleared, "nothing was banked");

            var paid = NewState();
            paid.TryUse(false, true, out _, out var t2);
            Assert.IsTrue(paid.TryTake(t2, out _));

            foreach (var state in new[] { bust, paid })
            {
                var phase = state.Phase;
                var tok = state.Token;
                Assert.IsTrue(state.IsTerminal);
                Assert.AreEqual(GambleUseOutcome.Spent, state.TryUse(false, true, out _, out _));
                Assert.IsFalse(state.TryDeepen(tok, 20, out _));
                Assert.IsFalse(state.TryTake(tok, out _));
                Assert.IsFalse(state.TryClearWave(1, out _, out _));
                Assert.IsFalse(state.TryBust(1));
                Assert.IsFalse(state.TryRefund(1, out _));
                Assert.IsFalse(state.BeginStream(1, 3));
                Assert.IsFalse(state.SpawnDone(1, null));
                Assert.IsFalse(state.IsReadyUnder(tok));
                Assert.AreEqual(phase, state.Phase);
                Assert.AreEqual(tok, state.Token, "an inert call never moves the token");
            }
        }

        [TestMethod]
        public void A_use_says_run_over_and_not_inside_before_anything_else()
        {
            var state = NewState();
            Assert.AreEqual(GambleUseOutcome.RunOver, state.TryUse(true, true, out _, out _));
            Assert.AreEqual(GambleUseOutcome.NotInside, state.TryUse(false, false, out _, out _));

            state.TryUse(false, true, out _, out var token);
            state.TryDeepen(token, 1, out _);
            Assert.AreEqual(GambleUseOutcome.NotInside, state.TryUse(false, false, out _, out _), "a visitor is refused mid-wave too");
            Assert.AreEqual(GamblePhase.Wave, state.Phase);
        }

        [TestMethod]
        public void A_stale_or_second_dialog_does_nothing()
        {
            var state = NewState();
            state.TryUse(false, true, out _, out var offered);
            state.TryUse(false, true, out _, out var second);
            Assert.AreEqual(offered, second, "two members offered at once share the token");

            // The first Yes wins; the second member's Deepen (same token) and Take now do nothing.
            Assert.IsTrue(state.TryDeepen(offered, 1, out _));
            Assert.IsFalse(state.TryDeepen(second, 1, out _));
            Assert.IsFalse(state.IsReadyUnder(second), "the No path's Take offer is withdrawn too");

            Assert.IsTrue(state.BeginStream(1, 0));
            Assert.IsTrue(state.TryClearWave(1, out _, out _));
            Assert.AreEqual(GamblePhase.Ready, state.Phase);

            // Ready again, but under a new token: the old dialogs still do nothing.
            Assert.IsFalse(state.TryTake(offered, out _));
            Assert.IsFalse(state.TryDeepen(offered, 2, out _));
            Assert.AreEqual(GamblePhase.Ready, state.Phase);

            state.TryUse(false, true, out _, out var fresh);
            Assert.AreNotEqual(offered, fresh);
            Assert.IsTrue(state.IsReadyUnder(fresh));
            Assert.IsTrue(state.TryTake(fresh, out var paid));
            Assert.AreEqual(1, paid);
        }

        [TestMethod]
        public void A_spawn_for_a_wave_that_ended_touches_nothing()
        {
            var state = NewState();
            state.TryUse(false, true, out _, out var token);
            state.TryDeepen(token, 0, out _);

            Assert.IsFalse(state.BeginStream(2, 4), "not the running wave");
            Assert.IsTrue(state.BeginStream(1, 2));
            Assert.IsFalse(state.BeginStream(1, 5), "a stream is planned once");

            Assert.IsTrue(state.Snapshot(1, 30, out var done, out var elapsed));
            Assert.IsFalse(done);
            Assert.AreEqual(30.0, elapsed);

            Assert.IsTrue(state.SpawnDone(1, null));
            Assert.IsTrue(state.SpawnDone(1, null));
            Assert.IsTrue(state.Snapshot(1, 31, out done, out _));
            Assert.IsTrue(done, "both planned enemies are through");

            Assert.IsTrue(state.TryBust(1));
            Assert.IsFalse(state.SpawnDone(1, null), "a late spawn after the bust");
            Assert.IsFalse(state.Snapshot(1, 40, out _, out _));
        }

        [TestMethod]
        public void A_wave_that_cannot_open_refunds_what_was_banked()
        {
            var state = ClearedThrough(2);
            state.TryUse(false, true, out _, out var token);
            Assert.IsTrue(state.TryDeepen(token, 0, out var wave));
            Assert.IsTrue(state.TryRefund(wave, out var banked));
            Assert.AreEqual(2, banked);
            Assert.AreEqual(GamblePhase.Paid, state.Phase);
        }

        // ---- the tick ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_tick_busts_on_nobody_alive_first_then_clears_or_times_out()
        {
            // (anyAlive, streamDone, enemiesAlive, elapsed, seconds) -> outcome
            var table = new (bool, bool, int, double, double, GambleTickOutcome)[]
            {
                (false, true, 0, 10, 60, GambleTickOutcome.BustNobody),
                (false, false, 3, 70, 60, GambleTickOutcome.BustNobody),
                (true, true, 0, 10, 60, GambleTickOutcome.Cleared),
                (true, true, 0, 60, 60, GambleTickOutcome.Cleared),
                (true, false, 0, 10, 60, GambleTickOutcome.None),
                (true, true, 1, 10, 60, GambleTickOutcome.None),
                (true, true, 1, 60, 60, GambleTickOutcome.BustTimer),
                (true, false, 0, 61, 60, GambleTickOutcome.BustTimer),
            };

            foreach (var (alive, done, enemies, elapsed, seconds, expected) in table)
                Assert.AreEqual(expected, GambleEncounterType.DecideTick(alive, done, enemies, elapsed, seconds), $"{alive} {done} {enemies} {elapsed}/{seconds}");
        }

        [TestMethod]
        public void Alive_means_at_least_one_member_who_is_not_dead()
        {
            Assert.IsTrue(GambleEncounterType.AnyAlive(new[] { true, false }, dead => dead));
            Assert.IsFalse(GambleEncounterType.AnyAlive(new[] { true, true }, dead => dead), "every member dead");
            Assert.IsFalse(GambleEncounterType.AnyAlive(new bool[0], dead => dead), "nobody inside");
            Assert.IsFalse(GambleEncounterType.AnyAlive<bool>(null, dead => dead));
            Assert.IsFalse(GambleEncounterType.AnyAlive(new string[] { null }, _ => false), "a null member is not alive");
        }

        // ---- Confirmation_Choice ----------------------------------------------------------------------------------

        [TestMethod]
        public void Confirmation_Choice_runs_yes_and_explicit_no_but_never_a_timeout()
        {
            var sent = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
            var log = new List<string>();
            var choice = new Confirmation_Choice(new ObjectGuid(0x50000001u), () => log.Add("yes"), () => log.Add("no"), sent);

            Assert.AreEqual(ConfirmationType.Yes_No, choice.ConfirmationType);

            Assert.AreEqual(ConfirmationChoiceAnswer.Yes, choice.Dispatch(true, false, sent.AddSeconds(5)));
            Assert.AreEqual(ConfirmationChoiceAnswer.No, choice.Dispatch(false, false, sent.AddSeconds(5)));
            CollectionAssert.AreEqual(new[] { "yes", "no" }, log);

            log.Clear();
            Assert.AreEqual(ConfirmationChoiceAnswer.Ignored, choice.Dispatch(false, true, sent.AddSeconds(30)), "the manager's timeout");
            Assert.AreEqual(ConfirmationChoiceAnswer.Ignored, choice.Dispatch(true, true, sent.AddSeconds(1)), "a timeout flag wins over a Yes");
            Assert.AreEqual(ConfirmationChoiceAnswer.Ignored, choice.Dispatch(false, false, sent.AddSeconds(29.5)), "the client's automatic No after the abort");
            Assert.AreEqual(ConfirmationChoiceAnswer.Ignored, choice.Dispatch(true, false, sent.AddSeconds(31)));
            Assert.AreEqual(0, log.Count, "a timeout runs nothing");

            Assert.AreEqual(ConfirmationChoiceAnswer.No, choice.Dispatch(false, false, sent.AddSeconds(29.4)), "just inside the window");
            CollectionAssert.AreEqual(new[] { "no" }, log);

            var yesOnly = new Confirmation_Choice(new ObjectGuid(0x50000001u), () => log.Add("take"), null, sent);
            Assert.AreEqual(ConfirmationChoiceAnswer.No, yesOnly.Dispatch(false, false, sent), "a No with no action is still a No");
            CollectionAssert.AreEqual(new[] { "no" }, log);
            Assert.AreEqual(TimeSpan.FromSeconds(29.5), ACE.Server.ThreadDungeons.Confirmation_ThreadGroupStart.LateAfter, "the shared late rule");
        }

        // ---- the payout ------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_payout_grants_once_per_member_inside_with_the_waves_cleared()
        {
            var point = Point(DungeonCell(), 0, 0);
            var run = DungeonRun(new[] { point });
            var type = new GambleEncounterType();
            var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, Type("gamble", kind: "gamble"), Candidate(point), 7), type);
            var state = new GambleState(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), new GambleConfig(), null);
            instance.State = state;

            var savedMembers = GambleEncounterType.MembersInside;
            var savedGrant = GambleEncounterType.GrantPrize;
            var grants = new List<(ThreadDungeonRun Run, string Kind, int Waves)>();

            try
            {
                // Two members inside (the harness cannot build a Player, so the list carries two placeholders).
                GambleEncounterType.MembersInside = _ => new List<Player> { null, null };
                GambleEncounterType.GrantPrize = (r, p, kind, waves) => { grants.Add((r, kind, waves)); return true; };

                GambleEncounterType.PayAndClose(instance, state, 3, "taken");

                Assert.AreEqual(2, grants.Count, "one independent grant per member inside");
                Assert.IsTrue(grants.All(g => ReferenceEquals(g.Run, run) && g.Kind == "gamble" && g.Waves == 3));
                Assert.IsTrue(run.EncounterOutcomes.Any(l => l.EndsWith(": Paid (taken, 3 wave(s) cleared); 2 prize(s) granted", StringComparison.Ordinal)),
                    string.Join("\n", run.EncounterOutcomes));

                grants.Clear();
                GambleEncounterType.GrantPrize = (r, p, kind, waves) => { grants.Add((r, kind, waves)); return grants.Count == 1; };
                GambleEncounterType.PayAndClose(instance, state, 0, "taken");
                Assert.IsTrue(run.EncounterOutcomes.Last().EndsWith("; 1 prize(s) granted", StringComparison.Ordinal), "a refused grant is not counted");
            }
            finally
            {
                GambleEncounterType.MembersInside = savedMembers;
                GambleEncounterType.GrantPrize = savedGrant;
            }
        }

        private static uint nextGuid = 0x7F200000;

        private static WorldObject Stack(uint wcid, string name, int max)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.StackSize, 1 }, { PropertyInt.MaxStackSize, max } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void Two_members_roll_independently_and_both_grow_by_the_same_waves()
        {
            // The prize seams of ThreadEncounterPrize (Roll over an rng, BuildObjects over the create seam): each member's
            // Grant rolls its own pick, and the gamble's wavesCleared grows whatever each one drew.
            var entries = new[]
            {
                new ThreadEncounterPrizeEntry("notes", new uint[] { 20630 }, 5, 1),
                new ThreadEncounterPrizeEntry("scarabs", new uint[] { 7299 }, 4, 1),
            };

            var savedCreate = ThreadEncounterPrize.CreateObject;

            try
            {
                ThreadEncounterPrize.CreateObject = wcid => Stack(wcid, wcid == 20630 ? "Trade Note" : "Diamond Scarab", 1000);

                // Golden seeds: seed 1's first NextDouble is 0.2487 (first entry), seed 2's is 0.7711 (second entry).
                var a = ThreadEncounterPrize.Roll(entries, _ => true, new Random(1));
                var b = ThreadEncounterPrize.Roll(entries, _ => true, new Random(2));
                Assert.AreEqual("notes", a.Entry.Id);
                Assert.AreEqual("scarabs", b.Entry.Id, "the second member's roll is its own");

                const int waves = 3;
                var builtA = ThreadEncounterPrize.BuildObjects(a, waves, new Random(1));
                var builtB = ThreadEncounterPrize.BuildObjects(b, waves, new Random(2));

                Assert.AreEqual(5 + waves * 2, builtA.Sum(o => o.Item.StackSize ?? 1), "5 grows by 2 a wave");
                Assert.AreEqual(4 + waves * 2, builtB.Sum(o => o.Item.StackSize ?? 1), "4 grows by 2 a wave");
                Assert.AreEqual(ThreadEncounterPrize.GambleQuantity(5, waves, false), builtA.Sum(o => o.Item.StackSize ?? 1));
                Assert.AreEqual(ThreadEncounterPrize.GambleQuantity(4, waves, false), builtB.Sum(o => o.Item.StackSize ?? 1));
            }
            finally
            {
                ThreadEncounterPrize.CreateObject = savedCreate;
            }
        }

        // ---- filters and Place ------------------------------------------------------------------------------------

        [TestMethod]
        public void RejectRun_drops_the_type_when_the_runs_prize_is_off()
        {
            var run = DungeonRun(new[] { Point(DungeonCell(), 0, 0) });
            var type = new GambleEncounterType();
            var def = Type("gamble", kind: "gamble");

            Assert.IsTrue(run.EncounterPrizeEnabled);
            Assert.IsNull(type.RejectRun(def, run));

            run.EncounterPrizeEnabled = false;
            Assert.AreEqual("gamble: the encounter prize is disabled for this run", type.RejectRun(def, run));
            Assert.AreEqual("gamble: the encounter prize is disabled for this run", ((IThreadEncounterType)type).RejectRun(def, run), "through the interface");
        }

        [TestMethod]
        public void RejectPointAmong_needs_portal_points_for_the_neediest_wave()
        {
            var type = new GambleEncounterType();
            var def = Type("gamble", kind: "gamble");
            var chest = Candidate(Point(DungeonCell(), 0, 0));
            var near = Candidate(Point(DungeonCell(), 10, 0));
            var near2 = Candidate(Point(DungeonCell(), 0, 12));
            var tooClose = Candidate(Point(DungeonCell(), 3, 0));

            Assert.AreEqual("gamble: fewer than 2 portal point(s) 8 to 25 m away", type.RejectPointAmong(def, chest, new[] { chest, near, tooClose }, false));
            Assert.IsNull(type.RejectPointAmong(def, chest, new[] { chest, near, near2 }, false));

            var oneWaveOne = Type("gamble", kind: "gamble");
            oneWaveOne.Config = Json(WavesJson((1, 2, 60), (1, 2, 60), (1, 2, 60), (1, 2, 60)));
            Assert.IsNull(type.RejectPointAmong(oneWaveOne, chest, new[] { chest, near, tooClose }, false), "one portal is enough when no wave needs two");

            var broken = Type("gamble", kind: "gamble");
            broken.Config = Json("{\"waves\":[]}");
            Assert.AreEqual("gamble: config.waves must be an array of exactly 4 wave rows", type.RejectPointAmong(broken, chest, new[] { chest, near, near2 }, false));
        }

        [TestMethod]
        public void Place_refuses_cleanly_and_creates_nothing()
        {
            var point = Point(DungeonCell(), 0, 0);
            var type = new GambleEncounterType();
            var savedCreate = GambleEncounterType.CreateObject;
            var created = 0;

            try
            {
                GambleEncounterType.CreateObject = _ => { created++; return null; };

                // No plan (the fixture's context carries none): no trash to send.
                var run = DungeonRun(new[] { point, Point(DungeonCell(), 10, 0), Point(DungeonCell(), 0, 12) });
                var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, Type("gamble", kind: "gamble"), Candidate(point), 7), type);
                Assert.IsFalse(type.Place(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), out var failure));
                Assert.AreEqual("the run's plan has no trash to send through the portals", failure);

                // The prize off for the run.
                run.EncounterPrizeEnabled = false;
                Assert.IsFalse(type.Place(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), out failure));
                Assert.AreEqual("the encounter prize is disabled for this run", failure);

                // A broken config.
                var def = Type("gamble", kind: "gamble");
                def.Config = Json("{\"bogus\":1}");
                var broken = run.NewEncounterInstance(new ThreadEncounterPick(2, def, Candidate(point), 7), type);
                Assert.IsFalse(type.Place(new ThreadEncounterPlaceRequest(run, null, broken, run.EncounterContext), out failure));
                Assert.AreEqual("config: unknown key 'bogus'", failure);

                Assert.AreEqual(0, created, "nothing was created");
                Assert.IsNull(instance.State);
            }
            finally
            {
                GambleEncounterType.CreateObject = savedCreate;
            }
        }

        [TestMethod]
        public void OnRunEnded_releases_the_request_and_the_chest_check_is_by_guid()
        {
            var point = Point(DungeonCell(), 0, 0);
            var run = DungeonRun(new[] { point });
            var type = new GambleEncounterType();
            var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, Type("gamble", kind: "gamble"), Candidate(point), 7), type);
            var state = new GambleState(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), new GambleConfig(), null);
            instance.State = state;

            var chest = Stack(1007980, "Gambler's Cache", 1);
            var other = Stack(1002644, "Portal", 1);
            state.ChestGuid = chest.Guid.Full;
            Assert.IsTrue(GambleEncounterType.IsTheChest(state, chest));
            Assert.IsFalse(GambleEncounterType.IsTheChest(state, other), "a portal layer or an enemy is not the chest");
            Assert.IsFalse(GambleEncounterType.IsTheChest(null, chest));

            Assert.IsFalse(GambleEncounterType.RunOver(run, state));
            type.OnRunEnded(instance, "test");
            type.OnRunEnded(instance, "again");
            Assert.IsNull(state.Request);
            Assert.IsTrue(GambleEncounterType.RunOver(run, state), "every queued action is silent now");
        }

        // ---- text --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_player_lines_are_exact()
        {
            Assert.AreEqual("Deepen the Gambler's Cache? Wave 2 of 4 will pour through the portals. Clear it within 75 seconds and the prize may grow; fall or run out of time and everything is lost. (Answer No to take the prize instead.)", GambleEncounterType.DeepenPrompt(2, 75));
            Assert.AreEqual("Take the prize now and close the Gambler's Cache?", GambleEncounterType.TakePrompt);
            Assert.AreEqual("Ana deepens the Gambler's Cache. Wave 1 of 4 pours through the portals! Clear it within 60 seconds!", GambleEncounterType.WaveStartLine("Ana", 1, 60));
            Assert.AreEqual("30 seconds remain to clear the wave!", GambleEncounterType.WarningLine(30));
            Assert.AreEqual("10 seconds remain to clear the wave!", GambleEncounterType.WarningLine(10));
            Assert.AreEqual("Ana takes the Gambler's Cache's prize.", GambleEncounterType.TakeLine("Ana"));
            Assert.AreEqual("You must be at the Gambler's Cache to answer.", GambleEncounterType.NotAtCacheLine);
            Assert.AreEqual("Wave 3 of 4 cleared. The prize may grow. Use the cache to deepen again or take your winnings.", GambleEncounterType.WaveClearedLine(3));
            Assert.AreEqual("The cache is sealed until this wave is cleared.", GambleEncounterType.SealedLine);
            Assert.AreEqual("The wave outlasted you. The Gambler's Cache seals itself and the prize is lost.", GambleEncounterType.BustTimerLine);
            Assert.AreEqual("No one is left to claim it. The Gambler's Cache seals itself and the prize is lost.", GambleEncounterType.BustNobodyLine);
            Assert.AreEqual("Only members of this thread can open the Gambler's Cache.", GambleEncounterType.RefusedLine);
            Assert.AreEqual("gamble", GambleEncounterType.KindName);
            Assert.AreEqual(4, GambleEncounterType.MaxWaves);
        }

        // ---- timer warnings, the stuck rescue, the answer range ---------------------------------------------------

        [TestMethod]
        public void The_time_left_warnings_come_once_each_at_30_and_10_seconds()
        {
            // (lastSent, elapsed, seconds) -> warning
            var table = new (int, double, double, int)[]
            {
                (int.MaxValue, 0, 60, 0),
                (int.MaxValue, 29.9, 60, 0),
                (int.MaxValue, 30, 60, 30),
                (30, 31, 60, 0),
                (30, 50, 60, 10),
                (10, 55, 60, 0),
                (int.MaxValue, 52, 60, 10),
                (int.MaxValue, 60, 60, 0),
                (int.MaxValue, 0, 25, 0),
                (int.MaxValue, 15, 25, 10),
                (int.MaxValue, double.NaN, 60, 0),
            };

            foreach (var (last, elapsed, seconds, expected) in table)
                Assert.AreEqual(expected, GambleEncounterType.WarningDue(last, elapsed, seconds), $"{last} {elapsed}/{seconds}");

            // The state latch: each said once per wave, and a new wave starts over.
            var state = NewState();
            state.TryUse(false, true, out _, out var token);
            state.TryDeepen(token, 0, out _);
            Assert.AreEqual(0, state.TryLatchWarning(1, 10, 60));
            Assert.AreEqual(30, state.TryLatchWarning(1, 31, 60));
            Assert.AreEqual(0, state.TryLatchWarning(1, 32, 60), "30 is said once");
            Assert.AreEqual(10, state.TryLatchWarning(1, 51, 60));
            Assert.AreEqual(0, state.TryLatchWarning(1, 55, 60), "10 is said once");
            Assert.AreEqual(0, state.TryLatchWarning(2, 51, 60), "not the running wave");

            state.BeginStream(1, 0);
            state.TryClearWave(1, out _, out _);
            state.TryUse(false, true, out _, out var next);
            state.TryDeepen(next, 100, out _);
            Assert.AreEqual(30, state.TryLatchWarning(2, 46, 75), "wave 2 warns again");
        }

        [TestMethod]
        public void The_stuck_rescue_fires_after_8_seconds_without_a_1_metre_close()
        {
            const uint Target = 0x50000001u, Other = 0x50000002u;

            // No mark yet: start one, no rescue.
            var (rescue, mark) = GambleEncounterType.StuckStep(null, Target, 20, 30, 100, false);
            Assert.IsFalse(rescue);
            Assert.AreEqual(new GambleStuckMark(Target, 20, 100), mark);

            // Closing at least 1 m restarts the mark.
            (rescue, mark) = GambleEncounterType.StuckStep(mark, Target, 19, 30, 105, false);
            Assert.IsFalse(rescue);
            Assert.AreEqual(new GambleStuckMark(Target, 19, 105), mark);

            // Less than 1 m of progress keeps the old mark until 8 s have passed.
            (rescue, mark) = GambleEncounterType.StuckStep(mark, Target, 18.5, 30, 112.9, false);
            Assert.IsFalse(rescue);
            Assert.AreEqual(new GambleStuckMark(Target, 19, 105), mark, "0.5 m is not a close");

            (rescue, mark) = GambleEncounterType.StuckStep(mark, Target, 18.5, 30, 113, false);
            Assert.IsTrue(rescue, "8 s without a 1 m close");
            Assert.AreEqual(new GambleStuckMark(Target, 18.5, 113), mark, "a fresh mark after the rescue");

            // A new target starts over.
            (rescue, mark) = GambleEncounterType.StuckStep(new GambleStuckMark(Target, 20, 0), Other, 20, 30, 100, false);
            Assert.IsFalse(rescue);
            Assert.AreEqual(new GambleStuckMark(Other, 20, 100), mark);

            // In melee, or already at the chest: nothing to rescue.
            Assert.IsFalse(GambleEncounterType.StuckStep(new GambleStuckMark(Target, 1.5, 0), Target, 1.5, 30, 100, false).Rescue, "in melee");
            Assert.IsFalse(GambleEncounterType.StuckStep(new GambleStuckMark(Target, 20, 0), Target, 20, 1.0, 100, false).Rescue, "already at the chest");
            Assert.IsTrue(GambleEncounterType.StuckStep(new GambleStuckMark(Target, 20, 0), Target, 20, 2.5, 8, false).Rescue);
        }

        [TestMethod]
        public void An_enemy_that_attacked_within_8_seconds_is_never_rescued()
        {
            const uint Target = 0x50000001u;
            var stale = new GambleStuckMark(Target, 20, 0);

            // A ranged or caster enemy holding its range: no close for well over 8 s, but it attacked lately.
            var (rescue, mark) = GambleEncounterType.StuckStep(stale, Target, 20, 30, 100, attackedRecently: true);
            Assert.IsFalse(rescue, "attacked within 8 s -> no rescue");
            Assert.AreEqual(new GambleStuckMark(Target, 20, 100), mark, "and its clock restarts");
            Assert.IsTrue(GambleEncounterType.StuckStep(stale, Target, 20, 30, 100, attackedRecently: false).Rescue, "control: the same enemy not attacking is rescued");

            // The attack clock (Creature.PrevAttackTime against Timers.RunningTime).
            Assert.IsTrue(GambleEncounterType.AttackedRecently(95, 100), "5 s ago");
            Assert.IsTrue(GambleEncounterType.AttackedRecently(92.01, 100));
            Assert.IsFalse(GambleEncounterType.AttackedRecently(92, 100), "8 s ago is not within 8 s");
            Assert.IsTrue(GambleEncounterType.AttackedRecently(101, 100), "a spell wind-up stamps it slightly ahead");
            Assert.IsFalse(GambleEncounterType.AttackedRecently(double.MaxValue - 3, 100), "the NeverAttack sentinel");
            Assert.IsFalse(GambleEncounterType.AttackedRecently(0, 100), "never attacked");
            Assert.IsFalse(GambleEncounterType.AttackedRecently(double.NaN, 100));
        }

        [TestMethod]
        public void AnswerDistance_measures_player_to_chest_and_is_far_with_no_position()
        {
            const uint Cell = 0x0150018Au;
            var chest = new ACE.Entity.Position(Cell, 10, 10, 0, 0, 0, 0, 1, 0u);
            var at14 = new ACE.Entity.Position(Cell, 24, 10, 0, 0, 0, 0, 1, 0u);
            var at16 = new ACE.Entity.Position(Cell, 26, 10, 0, 0, 0, 0, 1, 0u);

            Assert.AreEqual(14.0, GambleEncounterType.AnswerDistance(at14, chest), 1e-4);
            Assert.AreEqual(16.0, GambleEncounterType.AnswerDistance(at16, chest), 1e-4);
            Assert.IsTrue(GambleEncounterType.WithinAnswerRange(GambleEncounterType.AnswerDistance(at14, chest)), "14 m answers");
            Assert.IsFalse(GambleEncounterType.WithinAnswerRange(GambleEncounterType.AnswerDistance(at16, chest)), "16 m does not");
            Assert.AreEqual(double.MaxValue, GambleEncounterType.AnswerDistance(null, chest));
            Assert.AreEqual(double.MaxValue, GambleEncounterType.AnswerDistance(at14, null));
        }

        [TestMethod]
        public void An_answer_counts_only_within_15_metres_of_the_chest()
        {
            Assert.IsTrue(GambleEncounterType.WithinAnswerRange(0));
            Assert.IsTrue(GambleEncounterType.WithinAnswerRange(15));
            Assert.IsFalse(GambleEncounterType.WithinAnswerRange(15.01));
            Assert.IsFalse(GambleEncounterType.WithinAnswerRange(double.MaxValue), "no position");
            Assert.IsFalse(GambleEncounterType.WithinAnswerRange(double.NaN));
        }

        // ---- the per-kind cap, the walk rule, the prize line ------------------------------------------------------

        [TestMethod]
        public void MaxPerRun_caps_a_kind_and_an_absent_key_caps_nothing()
        {
            var points = Enumerable.Range(0, 6).Select(i => Candidate(Point(DungeonCell(0x0100 + i), 50f * i, 0))).ToList();

            var capped = Type("gamble", kind: "gamble");
            capped.MaxPerRun = 1;
            var plan = ThreadEncounterPicker.Pick(Request(new[] { capped }, points, max: 4));
            Assert.AreEqual(1, plan.Picks.Count, string.Join("\n", plan.Dropped));
            Assert.IsTrue(plan.Dropped.All(d => d.Contains("gamble: maxPerRun 1 of kind 'gamble' already placed")), string.Join("\n", plan.Dropped));

            // Two rows of the same kind share the cap.
            var second = Type("gamble_b", kind: "gamble");
            second.MaxPerRun = 1;
            Assert.AreEqual(1, ThreadEncounterPicker.Pick(Request(new[] { capped, second }, points, max: 4)).Picks.Count);

            // No key: unchanged, every slot fills.
            Assert.AreEqual(4, ThreadEncounterPicker.Pick(Request(new[] { Type("free", kind: "fake") }, points, max: 4)).Picks.Count);

            // A cap of 2.
            var two = Type("two", kind: "fake");
            two.MaxPerRun = 2;
            Assert.AreEqual(2, ThreadEncounterPicker.Pick(Request(new[] { two }, points, max: 4)).Picks.Count);

            Assert.IsNull(ThreadEncounterPicker.KindCapReached(Type("x"), new List<ThreadEncounterPick>()));
            Assert.IsNull(ThreadEncounterPicker.KindCapReached(capped, null));
        }

        [TestMethod]
        public void A_maxPerRun_below_one_refuses_the_row()
        {
            var json = "{\"version\":1,\"types\":[{\"id\":\"g\",\"kind\":\"gamble\",\"maxPerRun\":0},{\"id\":\"h\",\"kind\":\"gamble\",\"maxPerRun\":1},{\"id\":\"i\",\"kind\":\"gamble\"}]}";
            var problems = new List<string>();
            var store = ThreadEncounterStore.Parse(json, null, problems);

            CollectionAssert.AreEqual(new[] { "h", "i" }, store.Types.Select(t => t.Id).ToArray());
            Assert.AreEqual(1, store.Types[0].MaxPerRun);
            Assert.IsNull(store.Types[1].MaxPerRun);
            Assert.IsTrue(problems.Any(p => p.Contains("maxPerRun 0 must be at least 1")), string.Join("\n", problems));
        }

        [TestMethod]
        public void The_cache_portal_rule_needs_a_known_walk_within_1_25_and_Defense_is_unchanged()
        {
            var anchor = Point(DungeonCell(), 0, 0);
            var all = new GambleWaveConfig { PortalsMin = 3, PortalsMax = 3 };
            var one = new GambleWaveConfig { PortalsMin = 1, PortalsMax = 1 };
            PortalWaveCandidate At(float x, float? walk) { var p = Point(DungeonCell(), x, 0); return new PortalWaveCandidate(p, ThreadEncounterGeometry.Global(p, false), walk); }

            var unknown = At(10, null);
            var ratio13 = At(12, 12 * 1.3f);
            var ratio12 = At(14, 14 * 1.2f);
            var candidates = new[] { unknown, ratio13, ratio12 };
            var global = ThreadEncounterGeometry.Global(anchor, false);

            List<DungeonSpawnPointDef> Pick(IPortalWaveConfig config, float ratio, bool require) =>
                ThreadEncounterPortalWaves.PickPortals(global, DungeonLb, candidates, false, config, new Random(3), "cache", out _, ratio, require);

            var defense = Pick(all, ThreadEncounterPortalWaves.WalkRatio, false);
            CollectionAssert.AreEquivalent(new[] { unknown.Point, ratio13.Point, ratio12.Point }, defense, "Defense: unknown walk allowed, 1.3 within 1.5");

            var defaults = ThreadEncounterPortalWaves.PickPortals(global, DungeonLb, candidates, false, all, new Random(3), "pillar", out _);
            CollectionAssert.AreEqual(defense, defaults, "the defaults are Defense's rule");

            Assert.IsNull(Pick(all, GambleEncounterType.DungeonWalkRatio, true), "three needed, one qualifies");
            var two = new GambleWaveConfig { PortalsMin = 2, PortalsMax = 2 };
            Assert.IsNull(Pick(two, GambleEncounterType.DungeonWalkRatio, true), "the unknown walk does not count toward two");
            Assert.IsNotNull(Pick(two, GambleEncounterType.DungeonWalkRatio, false), "control: with unknown walks allowed, the unknown and the 1.2 make two");
            var cache = Pick(one, GambleEncounterType.DungeonWalkRatio, true);
            CollectionAssert.AreEqual(new[] { ratio12.Point }, cache, "the cache drops the unknown walk and the 1.3 ratio");

            // Outdoors there is no walk rule at all.
            var outdoor = ThreadEncounterPortalWaves.PickPortals(global, DungeonLb, candidates, true, all, new Random(3), "cache", out _, GambleEncounterType.DungeonWalkRatio, true);
            Assert.AreEqual(3, outdoor.Count);
        }

        [TestMethod]
        public void The_prize_line_says_how_deep_only_for_a_deepened_cache()
        {
            var line = ThreadEncounterPrize.DeliveryText(11, "Trade Note", false);
            Assert.AreEqual("Encounter prize: 11 Trade Note.", ThreadEncounterPrize.WithDeepened(line, 0), "every other encounter's line is unchanged");
            Assert.AreEqual("Encounter prize: 11 Trade Note. (deepened once)", ThreadEncounterPrize.WithDeepened(line, 1));
            Assert.AreEqual("Encounter prize: 11 Trade Note. (deepened 2 times)", ThreadEncounterPrize.WithDeepened(line, 2));
            Assert.AreEqual("Encounter prize: 11 Trade Note. (deepened 3 times)", ThreadEncounterPrize.WithDeepened(line, 3));
            Assert.AreEqual("x", ThreadEncounterPrize.WithDeepened("x", -1));

            // The suffix goes on the prize sentence, before the full-pack sentence.
            var pack = ThreadEncounterPrize.FullPackLine;
            Assert.AreEqual($"Encounter prize: 11 Trade Note. (deepened 3 times) {pack}", ThreadEncounterPrize.WithDeepened(ThreadEncounterPrize.DeliveryText(11, "Trade Note", true), 3));
            Assert.AreEqual($"Encounter prize: 3 bags of salvage (Amethyst x2, Tourmaline). (deepened once) {pack}",
                ThreadEncounterPrize.WithDeepened(ThreadEncounterPrize.SalvageDeliveryText(new[] { "Amethyst", "Tourmaline", "Amethyst" }, true), 1));
            Assert.AreEqual($"Encounter prize: 11 Trade Note. {pack}", ThreadEncounterPrize.WithDeepened(ThreadEncounterPrize.DeliveryText(11, "Trade Note", true), 0), "unchanged at 0");

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPrize.cs");
            var deliver = PooledLootSourceText.MethodBody(src, "private static void Deliver(ThreadDungeonRun run, Player player, ThreadEncounterPrizePick pick, string encounterKind, int bonusWaves)");
            StringAssert.Contains(deliver, "var text = WithDeepened(salvage != null ? SalvageDeliveryText(materials, anyAtFeet) : ItemDeliveryText(pick.Entry, qty, materials.Count, name, anyAtFeet), bonusWaves);");
        }

        // ---- source pins (the wiring that needs a live world) -----------------------------------------------------

        private const string TypeSource = "Source/ACE.Server/ThreadDungeons/GambleEncounterType.cs";

        [TestMethod]
        public void Wave_enemies_are_spawned_as_bonus_kills()
        {
            var src = PooledLootSourceText.Read(TypeSource);
            var spawn = PooledLootSourceText.MethodBody(src, "private static void SpawnEnemy(ThreadEncounterInstance instance, GambleState state, int wave, PortalWaveSpawn spawn)");

            StringAssert.Contains(spawn, "placed = Waves.TrySpawnEnemy(request, spawn.Entry, excludedFromClear: true);");
            Assert.AreEqual(1, CountOf(src, "TrySpawnEnemy("), "the one spawn site");
            Assert.AreEqual(0, CountOf(src, "excludedFromClear: false"));
        }

        [TestMethod]
        public void The_payout_reaches_every_member_inside_with_the_waves_cleared()
        {
            var src = PooledLootSourceText.Read(TypeSource);
            var pay = PooledLootSourceText.MethodBody(src, "internal static void PayAndClose(ThreadEncounterInstance instance, GambleState state, int wavesCleared, string how)");

            StringAssert.Contains(pay, "var granted = GrantToAll(MembersInside(run), winner => GrantPrize(run, winner, KindName, wavesCleared));");
            StringAssert.Contains(src, "internal static Func<ThreadDungeonRun, List<Player>> MembersInside = run => ThreadRunPresence.OnlineMembersInside(run);");
            StringAssert.Contains(src, "internal static Func<ThreadDungeonRun, Player, string, int, bool> GrantPrize = ThreadEncounterPrize.Grant;");
            Assert.AreEqual(1, CountOf(src, "GrantPrize("), "PayAndClose is the only grant");

            // Every payout sits behind a won latch.
            var take = PooledLootSourceText.MethodBody(src, "private static void Take(ThreadEncounterInstance instance, GambleState state, Player player, int token)");
            Assert.IsTrue(take.IndexOf("if (!state.TryTake(token, out var cleared))", StringComparison.Ordinal) < take.IndexOf("PayAndClose(", StringComparison.Ordinal));
            var clear = PooledLootSourceText.MethodBody(src, "private static void ClearWave(ThreadEncounterInstance instance, GambleState state, int wave)");
            Assert.IsTrue(clear.IndexOf("if (!state.TryClearWave(wave, out var autoPay, out var cleared))", StringComparison.Ordinal) < clear.IndexOf("PayAndClose(", StringComparison.Ordinal));
            var bust = PooledLootSourceText.MethodBody(src, "private static void Bust(ThreadEncounterInstance instance, GambleState state, int wave, GambleTickOutcome why, int enemiesLeft)");
            Assert.AreEqual(-1, bust.IndexOf("PayAndClose(", StringComparison.Ordinal), "a bust pays nothing");
        }

        [TestMethod]
        public void Every_payout_passes_the_waves_its_latch_banked()
        {
            var src = PooledLootSourceText.Read(TypeSource);

            var take = PooledLootSourceText.MethodBody(src, "private static void Take(ThreadEncounterInstance instance, GambleState state, Player player, int token)");
            StringAssert.Contains(take, "OnChestQueue(state, 0, () => PayAndClose(instance, state, cleared, \"taken\"),");

            var clear = PooledLootSourceText.MethodBody(src, "private static void ClearWave(ThreadEncounterInstance instance, GambleState state, int wave)");
            StringAssert.Contains(clear, "PayAndClose(instance, state, cleared, \"every wave cleared\");");

            var start = PooledLootSourceText.MethodBody(src, "private static void StartWave(ThreadEncounterInstance instance, GambleState state, int wave)");
            StringAssert.Contains(start, "if (state.TryRefund(wave, out var banked))");
            StringAssert.Contains(start, "PayAndClose(instance, state, banked, \"refund\");");

            Assert.AreEqual(3, CountOf(src, "PayAndClose(instance, state, "), "Take, the fourth clear and the refund are the only payouts");
        }

        [TestMethod]
        public void Deepen_and_Take_answer_only_from_a_member_at_the_cache()
        {
            var src = PooledLootSourceText.Read(TypeSource);

            var gate = PooledLootSourceText.MethodBody(src, "private static bool MayAnswer(ThreadEncounterInstance instance, GambleState state, Player player)");
            StringAssert.Contains(gate, "if (RunOver(run, state) || !ThreadRunPresence.IsMemberInside(run, player))");
            var chestGuard = gate.IndexOf("if (chest == null || chest.IsDestroyed)\n                return false;", StringComparison.Ordinal);
            var measure = gate.IndexOf("var distance = AnswerDistance(player.Location, chest.Location);", StringComparison.Ordinal);
            var range = gate.IndexOf("if (!WithinAnswerRange(distance))", StringComparison.Ordinal);
            var line = gate.IndexOf("Send(player, NotAtCacheLine);", StringComparison.Ordinal);
            Assert.IsTrue(chestGuard >= 0 && chestGuard < measure && measure < range && range < line, gate);

            foreach (var signature in new[]
            {
                "private static void Deepen(ThreadEncounterInstance instance, GambleState state, Player player, int token)",
                "private static void Take(ThreadEncounterInstance instance, GambleState state, Player player, int token)",
            })
            {
                var body = PooledLootSourceText.MethodBody(src, signature);
                var answer = body.IndexOf("if (!MayAnswer(instance, state, player))", StringComparison.Ordinal);
                var latch = body.IndexOf("if (!state.Try", StringComparison.Ordinal);
                Assert.IsTrue(answer >= 0 && answer < latch, signature);
            }
        }

        [TestMethod]
        public void Confirmation_Choice_reads_lateness_with_the_shared_rule_and_runs_on_the_players_queue()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/Entity/Confirmation.cs");

            var classify = PooledLootSourceText.MethodBody(src, "internal static ConfirmationChoiceAnswer Classify(bool response, bool timeout, DateTime sentUtc, DateTime nowUtc)");
            StringAssert.Contains(classify, "if (Confirmation_ThreadGroupStart.IsLate(sentUtc, nowUtc, timeout))");

            var process = src.Substring(src.IndexOf("public class Confirmation_Choice", StringComparison.Ordinal));
            process = PooledLootSourceText.MethodBody(process, "public override void ProcessConfirmation(bool response, bool timeout = false)");
            StringAssert.Contains(process, "player.EnqueueAction(new ActionEventDelegate(() => Run(answer)));");
            Assert.AreEqual(-1, process.IndexOf("OnYes", StringComparison.Ordinal), "no action runs inline on the answering thread");
        }

        [TestMethod]
        public void Both_portal_picks_use_the_cache_walk_rule()
        {
            var src = PooledLootSourceText.Read(TypeSource);
            Assert.AreEqual(2, CountOf(src, "\"cache\", out var refusal, DungeonWalkRatio, requireWalkDistance: true)"), "Place's check and every wave's pick");
            Assert.AreEqual(2, CountOf(src, "ThreadEncounterPortalWaves.PickPortals("));
        }

        [TestMethod]
        public void The_tick_warns_and_rescues()
        {
            var src = PooledLootSourceText.Read(TypeSource);
            var tick = PooledLootSourceText.MethodBody(src, "private static void Tick(ThreadEncounterInstance instance, GambleState state, int wave)");

            StringAssert.Contains(tick, "var warning = state.TryLatchWarning(wave, elapsed, seconds);");
            StringAssert.Contains(tick, "SendAll(run, WarningLine(warning));");
            StringAssert.Contains(tick, "if (ReferenceEquals(current, landblock))\n                    RescueIfStuck(instance, state, enemy, now);");
            Assert.AreEqual(1, CountOf(tick, "RescueIfStuck("), "the one rescue call, behind the landblock guard");

            var rescue = PooledLootSourceText.MethodBody(src, "private static void RescueIfStuck(ThreadEncounterInstance instance, GambleState state, GambleEnemy enemy, double now)");
            StringAssert.Contains(rescue, "var attacked = AttackedRecently(creature.PrevAttackTime, Timers.RunningTime);");
            StringAssert.Contains(rescue, "var (rescue, mark) = StuckStep(enemy.Mark, target.Guid.Full, distance, toChest, now, attacked);");
            StringAssert.Contains(rescue, "creature.FakeTeleport(chestAt);");
        }

        [TestMethod]
        public void The_tick_busts_on_the_living_members_inside()
        {
            var src = PooledLootSourceText.Read(TypeSource);
            var tick = PooledLootSourceText.MethodBody(src, "private static void Tick(ThreadEncounterInstance instance, GambleState state, int wave)");

            StringAssert.Contains(tick, "var anyAlive = AnyAlive(MembersInside(run), p => p.IsDead);");
            StringAssert.Contains(tick, "var outcome = DecideTick(anyAlive, streamDone, alive.Count, elapsed, seconds);");
        }

        [TestMethod]
        public void The_type_is_registered_beside_the_others()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var golem = src.IndexOf("ThreadEncounterTypes.Register(new GolemEncounterType());", StringComparison.Ordinal);
            var gamble = src.IndexOf("ThreadEncounterTypes.Register(new GambleEncounterType());", StringComparison.Ordinal);
            var store = src.IndexOf("var store = ThreadDungeonStore.LoadFromServerConfig();", StringComparison.Ordinal);

            Assert.IsTrue(golem >= 0 && golem < gamble && gamble < store, "registered before the store is published");
            Assert.IsInstanceOfType(new GambleEncounterType(), typeof(IThreadEncounterUsable));
        }

        // ---- group scaling ---------------------------------------------------------------------------------------

        private static readonly DungeonSpawnPointDef[] TwoPortals = { Point(DungeonCell(), 60, 50), Point(DungeonCell(), 40, 50) };

        private static List<DungeonSpawnPlanEntry> ScalingPlan()
            => new[] { (100u, DungeonRole.Trash), (200u, DungeonRole.Elite), (101u, DungeonRole.Trash) }
                .Select(e => new DungeonSpawnPlanEntry(e.Item1, e.Item2, Point(DungeonCell(), 0, 0), upliftLevel: 0, stampLevel: 66)).ToList();

        private static int[] PerPortal(IEnumerable<PortalWaveSpawn> stream, int portals)
        {
            var counts = new int[portals];

            foreach (var s in stream)
                counts[s.Portal]++;

            return counts;
        }

        [TestMethod]
        public void A_solo_wave_draws_exactly_the_stream_it_always_did()
        {
            // Golden streams (portal, delay, wcid), captured by running origin/master 8bb0c7088's own
            // ThreadEncounterPortalWaves.PlanStream (a throwaway probe test) over these portals, this plan and the shipped
            // wave 1 and wave 4 rows. A solo wave (factor 1.0) must reproduce them draw for draw.
            var golden = new Dictionary<(int Wave, int Seed), (int Portal, double Delay, uint Wcid)[]>
            {
                [(1, 1)] = new[] { (0, 2.0, 100u), (0, 4.9340213597444915, 101u) },
                [(1, 2)] = new[] { (0, 2.0, 100u), (0, 4.331997340699656, 101u), (0, 6.550006607803519, 100u) },
                [(4, 1)] = new[]
                {
                    (0, 2.0, 100u), (1, 2.0, 101u), (1, 4.2025329071108875, 101u), (0, 4.9340213597444915, 101u),
                    (1, 6.25980653253375, 100u), (0, 8.249059147317455, 100u),
                },
                [(4, 2)] = new[]
                {
                    (0, 2.0, 100u), (1, 2.0, 100u), (0, 4.331997340699656, 101u), (1, 4.449966307939015, 100u),
                    (0, 6.550006607803519, 100u), (1, 7.980700989244832, 100u),
                },
            };

            var waves = new GambleConfig().Waves;
            var plan = ScalingPlan();

            foreach (var ((wave, seed), expected) in golden)
            {
                var portals = wave == 1 ? TwoPortals.Take(1).ToArray() : TwoPortals;
                var stream = GambleEncounterType.PlanWaveStream(portals, plan, waves[wave - 1], new Random(seed), GambleEncounterType.GroupCountFactor(false, 2.5), waves[wave - 1].Seconds);

                Assert.AreEqual(expected.Length, stream.Count, $"wave {wave} seed {seed}: count");

                for (var i = 0; i < expected.Length; i++)
                {
                    Assert.AreEqual(expected[i].Portal, stream[i].Portal, $"wave {wave} seed {seed} [{i}]: portal");
                    Assert.AreEqual(expected[i].Delay, stream[i].Delay, 1e-12, $"wave {wave} seed {seed} [{i}]: delay");
                    Assert.AreEqual(expected[i].Wcid, stream[i].Entry.Wcid, $"wave {wave} seed {seed} [{i}]: wcid");
                }
            }
        }

        [TestMethod]
        public void The_factor_is_C_actual_for_a_group_and_exactly_one_solo()
        {
            Assert.AreEqual(1.0, GambleEncounterType.GroupCountFactor(false, 2.5), "solo is 1.0 whatever the stamp says");
            Assert.AreEqual(2.5, GambleEncounterType.GroupCountFactor(true, 2.5));
            Assert.AreEqual(1.75, GambleEncounterType.GroupCountFactor(true, 1.75));
            Assert.AreEqual(1.0, GambleEncounterType.GroupCountFactor(true, 0.8), "never below solo");
            Assert.AreEqual(1.0, GambleEncounterType.GroupCountFactor(true, double.NaN));
            Assert.AreEqual(1.0, GambleEncounterType.GroupCountFactor(true, double.PositiveInfinity));

            // The fixture's run is solo and unstamped: 1.0.
            var run = DungeonRun(new[] { Point(DungeonCell(), 0, 0) });
            Assert.IsFalse(run.IsGroup);
            Assert.AreEqual(1.0, GambleEncounterType.GroupCountFactor(run.IsGroup, run.GroupCountActual));
        }

        [TestMethod]
        public void Scaled_counts_round_half_up_and_cap_at_24_per_wave()
        {
            Assert.AreEqual(5, GambleEncounterType.ScaledWaveCount(2, 2.5));
            Assert.AreEqual(8, GambleEncounterType.ScaledWaveCount(3, 2.5), "7.5 rounds up");
            Assert.AreEqual(15, GambleEncounterType.ScaledWaveCount(6, 2.5));
            Assert.AreEqual(8, GambleEncounterType.ScaledWaveCount(6, 1.25), "7.5 rounds up");
            Assert.AreEqual(13, GambleEncounterType.ScaledWaveCount(5, 2.5), "12.5 rounds up (to-even would give 12)");
            Assert.AreEqual(9, GambleEncounterType.ScaledWaveCount(4, 2.125), "8.5 rounds up (to-even would give 8)");
            Assert.AreEqual(1, GambleEncounterType.ScaledWaveCount(1, 1.4));
            Assert.AreEqual(24, GambleEncounterType.ScaledWaveCount(10, 2.5), "25 caps at 24");
            Assert.AreEqual(24, GambleEncounterType.ScaledWaveCount(18, 2.5), "the largest valid wave, 3 portals x 6, caps at 24");
            Assert.AreEqual(17, GambleEncounterType.ScaledWaveCount(17, 1.0));
            Assert.AreEqual(0, GambleEncounterType.ScaledWaveCount(0, 2.5));
            Assert.AreEqual(4, GambleEncounterType.ScaledWaveCount(4, double.NaN), "a broken factor reads as 1");
            Assert.AreEqual(24, GambleEncounterType.MaxEnemiesPerWave);
        }

        [TestMethod]
        public void A_scaled_wave_is_spread_evenly_across_its_portals()
        {
            CollectionAssert.AreEqual(new[] { 8, 7 }, GambleEncounterType.DistributeAcrossPortals(15, 2));
            CollectionAssert.AreEqual(new[] { 3, 2, 2 }, GambleEncounterType.DistributeAcrossPortals(7, 3));
            CollectionAssert.AreEqual(new[] { 3, 3, 2 }, GambleEncounterType.DistributeAcrossPortals(8, 3), "one extra each, never all on the first");
            CollectionAssert.AreEqual(new[] { 8, 8, 8 }, GambleEncounterType.DistributeAcrossPortals(24, 3));
            CollectionAssert.AreEqual(new[] { 5 }, GambleEncounterType.DistributeAcrossPortals(5, 1));
            CollectionAssert.AreEqual(new[] { 0, 0 }, GambleEncounterType.DistributeAcrossPortals(0, 2));
            Assert.AreEqual(0, GambleEncounterType.DistributeAcrossPortals(5, 0).Length);
        }

        [TestMethod]
        public void A_group_wave_at_C_2_5_sends_two_and_a_half_times_the_enemies()
        {
            var waves = new GambleConfig().Waves;
            var plan = ScalingPlan();

            // Wave 1, one portal: seed 1 rolls 2 (the golden stream) -> 5; seed 2 rolls 3 -> 7.5 -> 8.
            Assert.AreEqual(5, GambleEncounterType.PlanWaveStream(TwoPortals.Take(1).ToArray(), plan, waves[0], new Random(1), 2.5, waves[0].Seconds).Count);
            Assert.AreEqual(8, GambleEncounterType.PlanWaveStream(TwoPortals.Take(1).ToArray(), plan, waves[0], new Random(2), 2.5, waves[0].Seconds).Count);

            // Wave 4, two portals of 3: 6 -> 15, spread 8 and 7.
            var four = GambleEncounterType.PlanWaveStream(TwoPortals, plan, waves[3], new Random(1), 2.5, waves[3].Seconds);
            Assert.AreEqual(15, four.Count);
            CollectionAssert.AreEqual(new[] { 8, 7 }, PerPortal(four, 2));
            Assert.IsTrue(four.All(s => s.Entry.Role == DungeonRole.Trash), "only trash comes through");
            CollectionAssert.AreEqual(four.OrderBy(s => s.Delay).Select(s => s.Delay).ToArray(), four.Select(s => s.Delay).ToArray(), "delay order");
            Assert.AreEqual(2.0, four.Where(s => s.Portal == 0).Min(s => s.Delay), "each portal's first enemy 2 s in");

            // The largest valid wave (3 portals x 6) at 2.5: 18 -> 45 -> capped at 24, 8 per portal.
            var three = new[] { Point(DungeonCell(), 60, 50), Point(DungeonCell(), 40, 50), Point(DungeonCell(), 50, 60) };
            var big = new GambleWaveConfig { PortalsMin = 3, PortalsMax = 3, PerPortalMin = 6, PerPortalMax = 6 };
            var capped = GambleEncounterType.PlanWaveStream(three, plan, big, new Random(1), 2.5, big.Seconds);
            Assert.AreEqual(24, capped.Count, "per-wave cap");
            CollectionAssert.AreEqual(new[] { 8, 8, 8 }, PerPortal(capped, 3));

            // No trash, no stream, scaled or not.
            Assert.AreEqual(0, GambleEncounterType.PlanWaveStream(TwoPortals, new List<DungeonSpawnPlanEntry>(), waves[3], new Random(1), 2.5, waves[3].Seconds).Count);
        }

        [TestMethod]
        public void The_factor_is_read_once_at_the_waves_start_and_feeds_its_stream()
        {
            var src = PooledLootSourceText.Read(TypeSource);
            var start = PooledLootSourceText.MethodBody(src, "private static void StartWave(ThreadEncounterInstance instance, GambleState state, int wave)");

            var read = start.IndexOf("var factor = GroupCountFactor(run.IsGroup, run.GroupCountActual);", StringComparison.Ordinal);
            var plan = start.IndexOf("PlanWaveStream(portals, request.Context?.Plan?.Entries, waveConfig, rng, factor, waveConfig.Seconds)", StringComparison.Ordinal);
            Assert.IsTrue(read >= 0 && read < plan, start);
            Assert.AreEqual(1, CountOf(src, "run.GroupCountActual"), "read in one place only");
            Assert.AreEqual(0, CountOf(src, "ThreadEncounterPortalWaves.PlanStream(portals, request"), "the wave no longer calls the unscaled planner directly");
        }

        [TestMethod]
        public void A_sub_solo_group_factor_takes_the_unscaled_path_and_draws_the_solo_golden()
        {
            // GroupCountFactor(true, 0.8) is 1.0, so a group stamped below solo plans exactly the solo golden stream.
            var factor = GambleEncounterType.GroupCountFactor(true, 0.8);
            Assert.AreEqual(1.0, factor);

            var waves = new GambleConfig().Waves;
            var stream = GambleEncounterType.PlanWaveStream(TwoPortals, ScalingPlan(), waves[3], new Random(1), factor, waves[3].Seconds);
            var golden = new[]
            {
                (0, 2.0, 100u), (1, 2.0, 101u), (1, 4.2025329071108875, 101u), (0, 4.9340213597444915, 101u),
                (1, 6.25980653253375, 100u), (0, 8.249059147317455, 100u),
            };

            Assert.AreEqual(golden.Length, stream.Count);

            for (var i = 0; i < golden.Length; i++)
            {
                Assert.AreEqual(golden[i].Item1, stream[i].Portal, $"[{i}] portal");
                Assert.AreEqual(golden[i].Item2, stream[i].Delay, 1e-12, $"[{i}] delay");
                Assert.AreEqual(golden[i].Item3, stream[i].Entry.Wcid, $"[{i}] wcid");
            }
        }

        [TestMethod]
        public void A_group_wave_4_at_C_2_5_draws_its_golden_stream()
        {
            // Captured from this branch at ea03941a6 (before the stagger compression, which does not engage here: the last
            // spawn lands at ~25 s of the 63 s allowed), wave 4, seed 1, factor 2.5, these two portals and this plan.
            var golden = new[]
            {
                (0, 2.0, 100u), (1, 2.0, 101u), (1, 5.408503071129556, 101u), (0, 5.543208244043965, 101u),
                (0, 8.408773446645949, 100u), (1, 9.308059964938117, 100u), (1, 11.629874336360896, 100u),
                (0, 12.29649799889722, 100u), (1, 15.225769730855603, 100u), (0, 15.58140910956143, 100u),
                (0, 18.07746740061672, 100u), (1, 18.813336976251257, 100u), (0, 22.057001695994757, 101u),
                (1, 22.45939383677179, 101u), (0, 25.3667774700405, 100u),
            };

            var waves = new GambleConfig().Waves;
            var stream = GambleEncounterType.PlanWaveStream(TwoPortals, ScalingPlan(), waves[3], new Random(1), 2.5, waves[3].Seconds);

            Assert.AreEqual(golden.Length, stream.Count);

            for (var i = 0; i < golden.Length; i++)
            {
                Assert.AreEqual(golden[i].Item1, stream[i].Portal, $"[{i}] portal");
                Assert.AreEqual(golden[i].Item2, stream[i].Delay, 1e-9, $"[{i}] delay");
                Assert.AreEqual(golden[i].Item3, stream[i].Entry.Wcid, $"[{i}] wcid");
            }
        }

        [TestMethod]
        public void A_large_group_lands_every_spawn_well_inside_the_wave_timer()
        {
            // Wave 1 (one portal, 60 s) at C = 8: 2 or 3 base -> 16 or 24 enemies. Uncompressed the last one would arrive
            // around 2 + 23 x 3 = 71 s, after the timer; compressed it lands by 0.6 x 60 = 36 s.
            var waves = new GambleConfig().Waves;
            var plan = ScalingPlan();
            var one = TwoPortals.Take(1).ToArray();
            var compressedSeen = 0;

            for (var seed = 1; seed <= 50; seed++)
            {
                var stream = GambleEncounterType.PlanWaveStream(one, plan, waves[0], new Random(seed), 8.0, waves[0].Seconds);

                Assert.IsTrue(stream.Count >= 16, $"seed {seed}: scaled");
                Assert.AreEqual(2.0, stream[0].Delay, $"seed {seed}: the first spawn's delay is unchanged");
                Assert.IsTrue(stream.Max(s => s.Delay) < 0.7 * 60, $"seed {seed}: last spawn {stream.Max(s => s.Delay)} s");
                Assert.IsTrue(stream.Max(s => s.Delay) <= GambleEncounterType.LastSpawnFraction * 60 + 1e-9, $"seed {seed}: by 60%");

                // Same draws as the uncompressed plan: the wcids come out in the same order with an endless timer.
                var loose = GambleEncounterType.PlanWaveStream(one, plan, waves[0], new Random(seed), 8.0, double.PositiveInfinity);
                CollectionAssert.AreEqual(loose.Select(s => s.Entry.Wcid).ToArray(), stream.Select(s => s.Entry.Wcid).ToArray(), $"seed {seed}: draws");

                if (loose.Max(s => s.Delay) > 0.6 * 60)
                    compressedSeen++;
            }

            Assert.IsTrue(compressedSeen > 0, "positive control: the uncompressed plan would overrun on some seed");
        }

        [TestMethod]
        public void Stagger_compression_never_widens_and_meets_the_60_percent_line()
        {
            Assert.AreEqual(1.0, GambleEncounterType.StaggerCompression(2.0, 20.0, 60), "34 s of room for a 20 s span: unchanged");
            Assert.AreEqual(0.5, GambleEncounterType.StaggerCompression(2.0, 68.0, 60), 1e-12, "34 / 68");
            Assert.AreEqual(1.0, GambleEncounterType.StaggerCompression(2.0, 0.0, 60), "one spawn: nothing to compress");
            Assert.AreEqual(1.0, GambleEncounterType.StaggerCompression(2.0, 50.0, double.NaN));
            Assert.AreEqual(0.0, GambleEncounterType.StaggerCompression(2.0, 50.0, 3.0), "no room: every spawn at the first delay");
            Assert.AreEqual(0.6, GambleEncounterType.LastSpawnFraction);
        }

        private static int CountOf(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
