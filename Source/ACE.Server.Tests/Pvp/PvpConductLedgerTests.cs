using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The conduct ledger counters (HealingDone, EngagedSeconds), the guarantee that the performance modifier's rating math is unchanged by
    /// them, and the pvp_conduct_* settings seam (compiled defaults equal the registered PropertyManager defaults; the clamps). Reads only the
    /// static Default*Properties dictionaries; no live PropertyManager value is read or written.
    /// </summary>
    [TestClass]
    public class PvpConductLedgerTests
    {
        private const uint A = 0x50000011, B = 0x50000012, C = 0x50000013, D = 0x50000014;

        private static BattlegroundPerformanceLedger Ledger() => new BattlegroundPerformanceLedger(new[] { (A, 0), (B, 0), (C, 1), (D, 1) });

        private static SeatPerformanceSnapshot Snap(BattlegroundPerformanceLedger l, uint id) => l.Snapshot().Single(s => s.CharacterId == id);

        // ================= counters =================

        [TestMethod]
        public void CreditHealing_Accumulates_PerSeat()
        {
            var l = Ledger();

            l.CreditHealing(A, 120);
            l.CreditHealing(A, 30);
            l.CreditHealing(B, 5);

            Assert.AreEqual(150, Snap(l, A).HealingDone);
            Assert.AreEqual(5, Snap(l, B).HealingDone);
            Assert.AreEqual(0, Snap(l, C).HealingDone);
        }

        /// <summary>
        /// PlayerDamageByTeam (Tugak G1's timeout ranking input): two seats on team 0 sum, team 1's lone seat with damage is its own
        /// entry, and a team whose seats dealt nothing is still present at 0.
        /// </summary>
        [TestMethod]
        public void PlayerDamageByTeam_SumsSeatsPerTeam()
        {
            var l = new BattlegroundPerformanceLedger(new[] { (A, 0), (B, 0), (C, 1), (D, 2) });

            l.CreditPlayerDamage(A, 120, null);
            l.CreditPlayerDamage(B, 30, null);
            l.CreditPlayerDamage(A, 7, null);
            l.CreditPlayerDamage(C, 45, null);

            var byTeam = l.PlayerDamageByTeam();

            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, byTeam.Keys.ToList(), "one key per team with a seat");
            Assert.AreEqual(157, byTeam[0], "A's 127 plus B's 30");
            Assert.AreEqual(45, byTeam[1]);
            Assert.AreEqual(0, byTeam[2]);
        }

        [TestMethod]
        public void CreditHealing_ZeroAndUnknownSeat_AreIgnored()
        {
            var l = Ledger();

            l.CreditHealing(A, 0);
            l.CreditHealing(0x5000FFFF, 100);

            Assert.IsTrue(l.Snapshot().All(s => s.HealingDone == 0));
        }

        [TestMethod]
        public void CreditEngagedSeconds_Accumulates_NonPositiveIgnored()
        {
            var l = Ledger();

            l.CreditEngagedSeconds(C, 1);
            l.CreditEngagedSeconds(C, 4);
            l.CreditEngagedSeconds(C, 0);
            l.CreditEngagedSeconds(C, -3);
            l.CreditEngagedSeconds(0x5000FFFF, 9);

            Assert.AreEqual(5, Snap(l, C).EngagedSeconds);
            Assert.AreEqual(0, Snap(l, D).EngagedSeconds);
        }

        /// <summary>The new counters are independent of the old ones: crediting them moves nothing else.</summary>
        [TestMethod]
        public void ConductCounters_TouchNoOtherStat()
        {
            var l = Ledger();

            l.CreditHealing(A, 500);
            l.CreditEngagedSeconds(A, 30);

            Assert.AreEqual(new SeatPerformanceSnapshot(A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 500, 30), Snap(l, A));
        }

        [TestMethod]
        public void Snapshot_TrailingDefaults_AreZero()
        {
            var s = new SeatPerformanceSnapshot(A, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);

            Assert.AreEqual(0, s.HealingDone);
            Assert.AreEqual(0, s.EngagedSeconds);
        }

        // ================= the rating math is unchanged =================

        /// <summary>
        /// Regression: the performance modifier does not read HealingDone or EngagedSeconds. The same role stats with wildly different conduct
        /// counters give identical P and m for every seat, in both King of the Hill and Attack/Defend; and a control proves the inputs do
        /// move the output when a ROLE stat changes.
        /// </summary>
        [TestMethod]
        public void PerformanceModifier_IgnoresTheConductCounters()
        {
            // Cap 100%, so no seat sits on the clamp and every change in P would show in m.
            var dials = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfCapPct = 100 });

            foreach (var mode in new[] { BattlegroundModes.KothModeKey, BattlegroundModes.AttackDefendModeKey })
            {
                var plain = Seats(healing: 0, engaged: 0);
                var loaded = Seats(healing: 100000, engaged: 900);

                var a = BattlegroundPerformanceModifier.Modifiers(mode, plain, dials);
                var b = BattlegroundPerformanceModifier.Modifiers(mode, loaded, dials);

                foreach (var id in a.Keys)
                {
                    Assert.AreEqual(a[id].Score, b[id].Score, $"{mode} 0x{id:X8}: P");
                    Assert.AreEqual(a[id].Modifier, b[id].Modifier, $"{mode} 0x{id:X8}: m");
                }

                var changedRole = Seats(healing: 0, engaged: 0, firstDamage: 9000);
                var c = BattlegroundPerformanceModifier.Modifiers(mode, changedRole, dials);
                Assert.AreNotEqual(a[A].Modifier, c[A].Modifier, $"{mode}: control, a role stat does move m");
            }
        }

        /// <summary>The pinned owner's example still holds exactly with conduct counters on every seat: base 20, +25 / +15 / -15 / -25.</summary>
        [TestMethod]
        public void OwnersExample_StillExact_WithConductCounters()
        {
            static SeatPerformanceSnapshot Koth(uint id, int team, long hill, long dmg, long kills, long healing, long engaged)
                => new SeatPerformanceSnapshot(id, team, dmg, 0, 0, 0, kills, 0, 0, 0, hill, healing, engaged);

            var seats = new[]
            {
                new PerformanceSeat(1, 0, Koth(1, 0, 100, 500, 3, 0, 0), "10.0.0.1", false, true),
                new PerformanceSeat(2, 0, Koth(2, 0, 0, 0, 0, 99999, 999), "10.0.0.2", false, true),
                new PerformanceSeat(3, 1, Koth(3, 1, 100, 500, 3, 0, 0), "10.0.0.3", false, true),
                new PerformanceSeat(4, 1, Koth(4, 1, 0, 0, 0, 99999, 999), "10.0.0.4", false, true),
            };

            var mods = BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, seats, PerformanceDials.From(BattlegroundTunables.Defaults));

            Assert.AreEqual(0.25, mods[1].Modifier, 1e-12);
            Assert.AreEqual(-0.25, mods[2].Modifier, 1e-12, "the healer's 99999 HealingDone does not lift the rating modifier");
        }

        private static List<PerformanceSeat> Seats(long healing, long engaged, long firstDamage = 500)
        {
            static SeatPerformanceSnapshot S(uint id, int team, long dmg, long crystal, long near, long kills, long hill, long healing, long engaged)
                => new SeatPerformanceSnapshot(id, team, dmg, crystal, near, near / 2, kills, kills, 0, 0, hill, healing, engaged);

            return new List<PerformanceSeat>
            {
                new PerformanceSeat(A, 0, S(A, 0, firstDamage, 1000, 0, 2, 60, healing, engaged), "10.0.0.1", false, true),
                new PerformanceSeat(B, 0, S(B, 0, 200, 300, 0, 1, 20, 0, 0), "10.0.0.2", false, true),
                new PerformanceSeat(C, 1, S(C, 1, 300, 0, 400, 1, 40, healing * 2, engaged / 2), "10.0.0.3", false, true),
                new PerformanceSeat(D, 1, S(D, 1, 100, 0, 100, 0, 10, 7, engaged), "10.0.0.4", false, true),
            };
        }

        // ================= the settings seam =================

        private static IEnumerable<(string Key, bool Value)> BoolKeys()
        {
            var d = PvpConductTunables.Defaults;
            yield return ("pvp_conduct_idle_enabled", d.IdleEnabled);
            yield return ("pvp_conduct_floor_enabled", d.FloorEnabled);
            yield return ("pvp_conduct_feed_enabled", d.FeedEnabled);
            yield return ("pvp_conduct_deserter_enabled", d.DeserterEnabled);
        }

        private static IEnumerable<(string Key, long Value)> LongKeys()
        {
            var d = PvpConductTunables.Defaults;
            yield return ("pvp_conduct_idle_warn_seconds", d.IdleWarnSeconds);
            yield return ("pvp_conduct_idle_remove_seconds", d.IdleRemoveSeconds);
            yield return ("pvp_conduct_floor_min_engaged_seconds", d.FloorMinEngagedSeconds);
            yield return ("pvp_conduct_feed_min_deaths", d.FeedMinDeaths);
            yield return ("pvp_conduct_deserter_lockout_1_minutes", d.DeserterLockout1Minutes);
            yield return ("pvp_conduct_deserter_lockout_2_minutes", d.DeserterLockout2Minutes);
            yield return ("pvp_conduct_deserter_lockout_3_minutes", d.DeserterLockout3Minutes);
            yield return ("pvp_conduct_deserter_decay_days", d.DeserterDecayDays);
        }

        private static IEnumerable<(string Key, double Value)> DoubleKeys()
        {
            var d = PvpConductTunables.Defaults;
            yield return ("pvp_conduct_engage_radius", d.EngageRadius);
            yield return ("pvp_conduct_floor_share", d.FloorShare);
            yield return ("pvp_conduct_feed_max_share", d.FeedMaxShare);
        }

        [TestMethod]
        public void Defaults_MatchTheRegisteredDefaults_AllFifteenKeys()
        {
            foreach (var (key, value) in BoolKeys())
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey(key), $"{key} is not registered as a bool");
                Assert.AreEqual(DefaultPropertyManager.DefaultBooleanProperties[key].Item, value, key);
                Assert.IsTrue(value, $"{key}: new settings default ON");
            }

            foreach (var (key, value) in LongKeys())
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey(key), $"{key} is not registered as a long");
                Assert.AreEqual(DefaultPropertyManager.DefaultLongProperties[key].Item, value, key);
            }

            foreach (var (key, value) in DoubleKeys())
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key), $"{key} is not registered as a double");
                Assert.AreEqual(DefaultPropertyManager.DefaultDoubleProperties[key].Item, value, 0.0, key);
            }

            Assert.AreEqual(15, BoolKeys().Count() + LongKeys().Count() + DoubleKeys().Count());

            var registered = DefaultPropertyManager.DefaultBooleanProperties.Keys
                .Concat(DefaultPropertyManager.DefaultLongProperties.Keys)
                .Concat(DefaultPropertyManager.DefaultDoubleProperties.Keys)
                .Concat(DefaultPropertyManager.DefaultStringProperties.Keys)
                .Count(k => k.StartsWith("pvp_conduct_"));
            Assert.AreEqual(15, registered, "every registered pvp_conduct_* key is covered here");
        }

        [TestMethod]
        public void Defaults_AreTheSpecifiedValues()
        {
            Assert.AreEqual(new PvpConductDials(true, 60, 90, 30.0, true, 0.25, 30, true, 4, 0.10, true, 15, 60, 1440, 7), PvpConductTunables.Defaults);
            Assert.AreEqual(PvpConductTunables.Defaults, PvpConductTunables.Clamp(PvpConductTunables.Defaults), "the defaults pass the clamps unchanged");
        }

        [TestMethod]
        public void Clamp_KeepsRemovalAboveWarning()
        {
            var d = PvpConductTunables.Clamp(PvpConductTunables.Defaults with { IdleWarnSeconds = 120, IdleRemoveSeconds = 90 });
            Assert.AreEqual(120, d.IdleWarnSeconds);
            Assert.AreEqual(121, d.IdleRemoveSeconds, "removal is always after the warning");

            var e = PvpConductTunables.Clamp(PvpConductTunables.Defaults with { IdleWarnSeconds = -5, IdleRemoveSeconds = 0 });
            Assert.AreEqual(1, e.IdleWarnSeconds);
            Assert.AreEqual(2, e.IdleRemoveSeconds);
        }

        [TestMethod]
        public void Clamp_RadiusSharesAndCounts()
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1.0 })
                Assert.AreEqual(0.0, PvpConductTunables.Clamp(PvpConductTunables.Defaults with { EngageRadius = bad }).EngageRadius, $"{bad}");

            var d = PvpConductTunables.Clamp(PvpConductTunables.Defaults with { FloorShare = 3.0, FeedMaxShare = double.NaN, FeedMinDeaths = -2, DeserterLockout2Minutes = -1, DeserterDecayDays = -7, FloorMinEngagedSeconds = -9 });

            Assert.AreEqual(1.0, d.FloorShare);
            Assert.AreEqual(0.0, d.FeedMaxShare);
            Assert.AreEqual(0, d.FeedMinDeaths);
            Assert.AreEqual(0, d.DeserterLockout2Minutes);
            Assert.AreEqual(0, d.DeserterDecayDays);
            Assert.AreEqual(0, d.FloorMinEngagedSeconds);
            Assert.IsNull(PvpConductTunables.Clamp(null));
        }
    }
}
