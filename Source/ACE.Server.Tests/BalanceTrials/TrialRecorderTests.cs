using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.BalanceTrials;
using ACE.Server.Managers.Analytics;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Pins the pure TrialRecorder. Reads no PropertyManager key. Golden values are worked out by hand from the scripted rep below;
    /// no expected value is computed by calling the recorder.
    ///
    /// Scripted rep (start 100.0, end 125.0, duration 25.0). Player events (hit_out, miss_out, consume, proc) and gaps:
    ///   100.0 start | 101.0 A hit | 101.5 A hit | 104.5 B hit | 105.5 B hit | 107.0 C hit | 108.0 C hit | 113.0 E hit | 114.0 E hit
    ///   | 116.0 X hit | 118.0 X hit | 120.0 X miss | 122.0 X hit | 123.0 consume | 123.5 proc | 125.0 end
    /// Gaps: 1, .5, 3 (idle), 1, 1.5, 1, 5 (idle), 1, 2, 2, 2, 2, 1, .5, 1.5. A gap of exactly 2.0 is NOT idle (rule is longer than 2.0).
    /// idle = 3 + 5 = 8.0, engaged = 25 - 8 = 17.0.
    /// Kills: A (trash, first hit 101.0, kill 102.0, ttk 1), B (104.5 -> 106.5, ttk 2), C (107.0 -> 110.0, ttk 3) so trash avg = 2.0;
    /// E (elite, 113.0 -> 115.0) ttk 2.0; X (boss, 116.0 -> 124.0) ttk 8.0.
    /// Hits out: A 50+50, B 40+40, C 30+30, E 100+100, X 200+200+200 = 11 hits, 1040 damage. Miss out 1.
    /// Kill awards: trash 10 xp / 1 lum each, elite 50 / 5, boss 500 / 40. Granted: trash 12 / 2 each, elite 60 / 6, boss 600 / 48
    /// = 696 xp, 60 lum.
    /// </summary>
    [TestClass]
    public class TrialRecorderTests
    {
        private const double Start = 100.0;
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        private static TrialRunFacts Facts() => new TrialRunFacts
        {
            TrialUuid = "uuid", Rep = 1, ScenarioId = "scn", ScenarioHash = "hash", ArchetypeId = "arch", ArchetypeChecksum = "sum",
            StateDigest = "digest", DigestDrift = true, CharacterId = 7, CharLevel = 100, GemLevel = 50, GemLevelPressed = 55, Tier = 3,
            DungeonId = "dun", Family = "fam", Seed = 5, ModsJson = "{}", PredPlayerHitsToKillBoss = 12.5, PredBossHitsToKillPlayer = 9.5, PredBossHitChance = 0.6, XpMultiplier = 1.5, BossXpMultiplier = 2.0, LumMultiplier = 1.25,
        };

        private static (TrialRecorder Rec, List<AnalyticsDatabase.TrialEventRow> Rows) New()
        {
            var rows = new List<AnalyticsDatabase.TrialEventRow>();
            return (new TrialRecorder("runkey", Start, StartUtc, rows.Add), rows);
        }

        private static void Script(TrialRecorder r)
        {
            const DamageType s = DamageType.Slash;
            r.OnHitOut(1, 10, DungeonRole.Trash, s, 50, 101.0);
            r.OnHitOut(1, 10, DungeonRole.Trash, s, 50, 101.5);
            r.OnKill(1, 10, DungeonRole.Trash, 10, 1, 102.0);
            r.OnXpGranted(12); r.OnLumGranted(2);
            r.OnHitOut(2, 11, DungeonRole.Trash, s, 40, 104.5);
            r.OnHitOut(2, 11, DungeonRole.Trash, s, 40, 105.5);
            r.OnKill(2, 11, DungeonRole.Trash, 10, 1, 106.5);
            r.OnXpGranted(12); r.OnLumGranted(2);
            r.OnHitOut(3, 12, DungeonRole.Trash, s, 30, 107.0);
            r.OnHitOut(3, 12, DungeonRole.Trash, s, 30, 108.0);
            r.OnKill(3, 12, DungeonRole.Trash, 10, 1, 110.0);
            r.OnXpGranted(12); r.OnLumGranted(2);
            r.OnHitOut(4, 20, DungeonRole.Elite, s, 100, 113.0);
            r.OnHitOut(4, 20, DungeonRole.Elite, s, 100, 114.0);
            r.OnKill(4, 20, DungeonRole.Elite, 50, 5, 115.0);
            r.OnXpGranted(60); r.OnLumGranted(6);
            r.OnHitOut(5, 30, DungeonRole.Boss, s, 200, 116.0);
            r.OnHitOut(5, 30, DungeonRole.Boss, s, 200, 118.0);
            r.OnMissOut(30, DungeonRole.Boss, CombatType.Melee, 120.0);
            r.OnHitOut(5, 30, DungeonRole.Boss, s, 200, 122.0);
            r.OnConsume(900, 123.0);
            r.OnProc(77, 123.5);
            r.OnKill(5, 30, DungeonRole.Boss, 500, 40, 124.0);
            r.OnXpGranted(600); r.OnLumGranted(48);
        }

        private static AnalyticsDatabase.TrialRunRow Run(string end = TrialEventKinds.EndCleared)
        {
            var (r, _) = New();
            Script(r);
            return r.Finish(end, 125.0, StartUtc.AddSeconds(25), Facts());
        }

        [TestMethod]
        public void Idle_and_engaged_seconds()
        {
            var row = Run();
            Assert.AreEqual(8.0, row.IdleSecs, 1e-9);
            Assert.AreEqual(17.0, row.EngagedSecs, 1e-9);
        }

        [TestMethod]
        public void A_gap_of_exactly_two_seconds_is_not_idle()
        {
            var (r, _) = New();
            r.OnHitOut(1, 10, DungeonRole.Trash, DamageType.Slash, 1, 102.0);
            r.OnHitOut(1, 10, DungeonRole.Trash, DamageType.Slash, 1, 104.0);
            var row = r.Finish(TrialEventKinds.EndTimeout, 106.0, StartUtc, Facts());
            Assert.AreEqual(0.0, row.IdleSecs, 1e-9);
            Assert.AreEqual(6.0, row.EngagedSecs, 1e-9);
        }

        [TestMethod]
        public void Rep_with_no_player_events_is_all_idle_and_engaged_rates_are_null()
        {
            var (r, _) = New();
            r.OnXpGranted(100);
            var row = r.Finish(TrialEventKinds.EndForfeit, 110.0, StartUtc, Facts());
            Assert.AreEqual(10.0, row.IdleSecs, 1e-9);
            Assert.AreEqual(0.0, row.EngagedSecs, 1e-9);
            Assert.AreEqual(100 * 360.0, row.XpPerHour.Value, 1e-6);
            Assert.IsNull(row.XpPerEngagedHour);
            Assert.IsNull(row.LumPerEngagedHour);
        }

        [TestMethod]
        public void Ttk_per_role()
        {
            var row = Run();
            Assert.AreEqual(2.0, row.TtkTrashAvg.Value, 1e-9);
            Assert.AreEqual(2.0, row.TtkEliteAvg.Value, 1e-9);
            Assert.AreEqual(8.0, row.TtkBoss.Value, 1e-9);
            Assert.AreEqual(3, row.KillsTrash);
            Assert.AreEqual(1, row.KillsElite);
            Assert.IsTrue(row.BossKilled);

            var (r, _) = New();
            r.OnHitOut(1, 10, DungeonRole.Trash, DamageType.Slash, 1, 101.0);
            var partial = r.Finish(TrialEventKinds.EndDied, 102.0, StartUtc, Facts());
            Assert.IsNull(partial.TtkTrashAvg);
            Assert.IsNull(partial.TtkEliteAvg);
            Assert.IsNull(partial.TtkBoss);
            Assert.IsFalse(partial.BossKilled);
        }

        [TestMethod]
        public void Per_hour_rates_and_null_on_zero_duration()
        {
            var row = Run();
            // 696 xp over 25 s: 696 * 3600 / 25 = 100224. Lum 60 * 3600 / 25 = 8640.
            Assert.AreEqual(100224.0, row.XpPerHour.Value, 1e-6);
            Assert.AreEqual(8640.0, row.LumPerHour.Value, 1e-6);
            // Engaged 17 s: 696 * 3600 / 17 = 2505600 / 17; 60 * 3600 / 17 = 216000 / 17.
            Assert.AreEqual(2505600.0 / 17.0, row.XpPerEngagedHour.Value, 1e-6);
            Assert.AreEqual(216000.0 / 17.0, row.LumPerEngagedHour.Value, 1e-6);
            Assert.AreEqual(696, row.XpEarned);
            Assert.AreEqual(60, row.LumEarned);

            var (r, _) = New();
            r.OnXpGranted(5);
            var zero = r.Finish(TrialEventKinds.EndForfeit, Start, StartUtc, Facts());
            Assert.IsNull(zero.XpPerHour);
            Assert.IsNull(zero.LumPerHour);
            Assert.IsNull(zero.XpPerEngagedHour);
            Assert.IsNull(zero.LumPerEngagedHour);
        }

        [TestMethod]
        public void Low_water_marks()
        {
            var (r, rows) = New();
            r.OnVitals(0.9, 0.8, 0.7, 101.0);
            r.OnVitals(0.5, 0.3, 0.7, 102.0);
            r.OnVitals(0.2, 0.6, 0.4, 103.0);   // HP crosses below 0.25: vital_low #1
            r.OnVitals(0.1, 0.9, 0.9, 104.0);   // still low: no event
            r.OnVitals(0.6, 1.0, 1.0, 105.0);   // recovered
            r.OnVitals(0.25, 1.0, 1.0, 106.0);  // exactly 0.25 is not below
            r.OnVitals(0.2, 1.0, 1.0, 107.0);   // crosses again: vital_low #2
            var row = r.Finish(TrialEventKinds.EndCleared, 108.0, StartUtc, Facts());

            Assert.AreEqual(0.1, row.HpLowPct.Value, 1e-12);
            Assert.AreEqual(0.3, row.StamLowPct.Value, 1e-12);
            Assert.AreEqual(0.4, row.ManaLowPct.Value, 1e-12);

            var lows = rows.Where(x => x.Kind == TrialEventKinds.VitalLow).ToList();
            Assert.AreEqual(2, lows.Count);
            Assert.AreEqual(3000, lows[0].TMs);
            Assert.AreEqual(200L, lows[0].Amount);  // fraction 0.2 in thousandths
            Assert.AreEqual(7000, lows[1].TMs);

            var (r2, _) = New();
            var none = r2.Finish(TrialEventKinds.EndCleared, 110.0, StartUtc, Facts());
            Assert.IsNull(none.HpLowPct);
            Assert.IsNull(none.StamLowPct);
            Assert.IsNull(none.ManaLowPct);
        }

        [TestMethod]
        public void Kill_rows_carry_their_award()
        {
            var (r, rows) = New();
            Script(r);
            var kills = rows.Where(x => x.Kind == TrialEventKinds.Kill).ToList();
            Assert.AreEqual(5, kills.Count);
            Assert.AreEqual(10L, kills[0].Xp);
            Assert.AreEqual(1L, kills[0].Lum);
            Assert.AreEqual(10u, kills[0].TargetWcid);
            Assert.AreEqual("Trash", kills[0].ActorRole);
            Assert.AreEqual(50L, kills[3].Xp);
            Assert.AreEqual(5L, kills[3].Lum);
            Assert.AreEqual("Elite", kills[3].ActorRole);
            Assert.AreEqual(500L, kills[4].Xp);
            Assert.AreEqual(40L, kills[4].Lum);
            Assert.AreEqual("Boss", kills[4].ActorRole);
            Assert.AreEqual(TrialEventKinds.DirOut, kills[4].Dir);
        }

        [TestMethod]
        public void Counts_hits_misses_evades_resists_both_ways()
        {
            var (r, rows) = New();
            r.OnHitOut(1, 10, DungeonRole.Trash, DamageType.Fire, 7, 101.0);
            r.OnHitOut(1, 10, DungeonRole.Trash, DamageType.Fire, 8, 101.5);
            r.OnHitIn(10, DungeonRole.Trash, DamageType.Cold, 3, 101.6);
            r.OnHitIn(10, DungeonRole.Trash, DamageType.Cold, 4, 101.7);
            r.OnHitIn(10, DungeonRole.Trash, DamageType.Cold, 5, 101.8);
            r.OnMissOut(10, DungeonRole.Trash, CombatType.Magic, 102.0);
            r.OnEvadeIn(10, DungeonRole.Trash, CombatType.Melee, 102.1);
            r.OnEvadeIn(10, DungeonRole.Trash, CombatType.Melee, 102.2);
            r.OnResist(true, 10, DungeonRole.Trash, 42, 102.3);
            r.OnResist(false, 10, DungeonRole.Trash, 43, 102.4);
            r.OnResist(false, 10, DungeonRole.Trash, 44, 102.5);
            r.OnDeath(103.0);
            r.OnConsume(900, 103.5);
            r.OnProc(77, 103.6);
            r.OnProc(78, 103.7);
            var row = r.Finish(TrialEventKinds.EndDied, 104.0, StartUtc, Facts());

            Assert.AreEqual(2, row.HitsOut);
            Assert.AreEqual(15L, row.DmgDealt);
            Assert.AreEqual(3, row.HitsIn);
            Assert.AreEqual(12L, row.DmgTaken);
            Assert.AreEqual(1, row.MissesOut);
            Assert.AreEqual(2, row.EvadedIn);
            Assert.AreEqual(1, row.ResistedOut);
            Assert.AreEqual(2, row.ResistedIn);
            Assert.AreEqual(1, row.Deaths);
            Assert.AreEqual(1, row.ConsumablesUsed);
            Assert.AreEqual(2, row.AbilityProcs);

            Assert.AreEqual(15, rows.Count);
            var resistOut = rows.Single(x => x.Kind == TrialEventKinds.Resist && x.Dir == TrialEventKinds.DirOut);
            Assert.AreEqual(42u, resistOut.SpellId);
            Assert.AreEqual(10u, resistOut.TargetWcid);
            var hitIn = rows.First(x => x.Kind == TrialEventKinds.Hit && x.Dir == TrialEventKinds.DirIn);
            Assert.AreEqual(10u, hitIn.ActorWcid);
            Assert.AreEqual("Cold", hitIn.DamageType);
            Assert.AreEqual("Magic", rows.Single(x => x.Kind == TrialEventKinds.Miss).CombatType);
            Assert.IsNull(rows.First(x => x.Kind == TrialEventKinds.Hit).Crit);
        }

        [TestMethod]
        public void Events_carry_the_run_key_and_ms_offsets()
        {
            var (r, rows) = New();
            r.OnSpawn(10, DungeonRole.Trash, "{\"p\":1}", 100.0);
            r.OnHitOut(1, 10, DungeonRole.Trash, DamageType.Slash, 1, 101.2346);  // 1234.6 ms rounds to 1235
            r.OnHitOut(1, 10, DungeonRole.Trash, DamageType.Slash, 1, 100.0005);  // 0.5 ms rounds half away from zero to 1 (floor would give 0)
            r.OnDeath(99.0);                                                      // before start clamps to 0

            Assert.IsTrue(rows.All(x => x.RunKey == "runkey"));
            Assert.AreEqual(0, rows[0].TMs);
            Assert.AreEqual(TrialEventKinds.Spawn, rows[0].Kind);
            Assert.AreEqual("{\"p\":1}", rows[0].PredJson);
            Assert.AreEqual(1235, rows[1].TMs);
            Assert.AreEqual(1, rows[2].TMs);
            Assert.AreEqual(0, rows[3].TMs);
        }

        [TestMethod]
        public void Finish_fills_every_non_null_column()
        {
            var row = Run();
            var nullableTypes = new HashSet<Type>();
            foreach (var f in typeof(AnalyticsDatabase.TrialRunRow).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var v = f.GetValue(row);
                var nullable = Nullable.GetUnderlyingType(f.FieldType) != null;
                if (!nullable)
                    Assert.IsNotNull(v, f.Name + " is a NOT NULL column and must be filled");
            }

            var want = Facts();
            Assert.AreEqual("runkey", row.RunKey);
            Assert.AreEqual(want.TrialUuid, row.TrialUuid);
            Assert.AreEqual(want.Rep, row.Rep);
            Assert.AreEqual(want.ScenarioId, row.ScenarioId);
            Assert.AreEqual(want.ScenarioHash, row.ScenarioHash);
            Assert.AreEqual(want.ArchetypeId, row.ArchetypeId);
            Assert.AreEqual(want.ArchetypeChecksum, row.ArchetypeChecksum);
            Assert.AreEqual(want.StateDigest, row.StateDigest);
            Assert.AreEqual(want.DigestDrift, row.DigestDrift);
            Assert.AreEqual("manual", row.Driver);
            Assert.AreEqual(1, row.ClientCount);
            Assert.AreEqual(want.CharacterId, row.CharacterId);
            Assert.AreEqual(want.CharLevel, row.CharLevel);
            Assert.AreEqual(want.GemLevel, row.GemLevel);
            Assert.AreEqual(want.GemLevelPressed, row.GemLevelPressed);
            Assert.AreEqual(want.Tier, row.Tier);
            Assert.AreEqual(want.DungeonId, row.DungeonId);
            Assert.AreEqual(want.Family, row.Family);
            Assert.AreEqual(want.Seed, row.Seed);
            Assert.AreEqual(want.ModsJson, row.ModsJson);
            Assert.AreEqual(want.PredPlayerHitsToKillBoss, row.PredPlayerHitsToKillBoss);
            Assert.AreEqual(want.PredBossHitsToKillPlayer, row.PredBossHitsToKillPlayer);
            Assert.AreEqual(want.PredBossHitChance, row.PredBossHitChance);
            Assert.AreEqual(1.5, row.XpMultiplier);
            Assert.AreEqual(2.0, row.BossXpMultiplier);
            Assert.AreEqual(1.25, row.LumMultiplier);
            Assert.AreEqual(55, row.GemLevelPressed.Value);
            Assert.AreEqual(StartUtc, row.StartedUtc);
            Assert.AreEqual(StartUtc.AddSeconds(25), row.EndedUtc);
            Assert.AreEqual(TrialEventKinds.EndCleared, row.EndState);
            Assert.AreEqual(1040L, row.DmgDealt);
            Assert.AreEqual(11, row.HitsOut);
            Assert.AreEqual(1, row.MissesOut);
            Assert.AreEqual(1, row.ConsumablesUsed);
            Assert.AreEqual(1, row.AbilityProcs);
        }
    }
}
