using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The engaged-defender reduction driven through its PRODUCTION defender source (Creature.DefenderSampleSource as shipped, reading the
    /// crystal's landblock player list) and the real engagement stamp (Player.OnPvpDamageRecorded), plus the diagnostics this PR adds: the
    /// per-candidate standing and tally, the dial description in the formation log, and the throttled per-crystal hit line.
    ///
    /// <para/>
    /// The owner's 2026-10-14 stage report (full crystal damage with "dr_per 2 / dr_cap 4 / dr_radius 300 / dr_window_s 30") is reproduced
    /// here with the dials stage actually held (radius 5, window 10: only per and cap had been changed, and kill_range was set to 300), next to
    /// the dials the owner intended, on the same fixture: the code reduces with the intended dials and does not with stage's.
    /// </summary>
    [TestClass]
    public class AttackDefendDefenderDiagnosticsTests
    {
        private const uint Instance = 0x8001_B4BF;

        private const int Attackers = CrystalWinCondition.AttackerTeam;

        private const int Defenders = CrystalWinCondition.DefenderTeam;

        private static readonly DateTime Now = new DateTime(2026, 10, 14, 18, 5, 0, DateTimeKind.Utc);

        /// <summary>What the owner meant to set (Discord report).</summary>
        private static readonly CrystalDefenderReductionDials Intended = new CrystalDefenderReductionDials(2.0, 4.0, 300.0, 30.0);

        /// <summary>What stage's ace_shard.config_properties_double held for the four keys on 2026-10-15.</summary>
        private static readonly CrystalDefenderReductionDials StageHeld = new CrystalDefenderReductionDials(2.0, 4.0, 5.0, 10.0);

        private DateTime clock;

        private Func<DateTime> savedClock;

        private Func<Creature, IEnumerable<DefenderSample>> savedSamples;

        private Action<string> savedLog;

        private readonly List<string> lines = new List<string>();

        [TestInitialize]
        public void Setup()
        {
            clock = Now;
            savedClock = PvpArenaHookSettings.UtcNow;
            savedSamples = Creature.DefenderSampleSource;
            savedLog = Creature.DefenderReductionLog;
            PvpArenaHookSettings.UtcNow = () => clock;
            Creature.DefenderReductionLog = lines.Add;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.UtcNow = savedClock;
            Creature.DefenderSampleSource = savedSamples;
            Creature.DefenderReductionLog = savedLog;
        }

        // ================= fixtures =================

        private static Position At(float x, float y, float z = -6f, uint instance = Instance) => new Position(0x003C01EE, x, y, z, 0f, 0f, 0f, 1f, instance);

        private static readonly Position CrystalAt = At(110f, -70f);

        private static PvpMatch NewMatch(CrystalDefenderReductionDials dials)
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(Attackers, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(Defenders, new List<PvpParticipant> { new PvpParticipant(2, 1500) }),
            };

            return new PvpMatch(Guid.NewGuid(), "bg_ad", teams, Now) { DefenderReduction = dials };
        }

        private static PvpPlayerBinding Bound(PvpMatch match, int team) => new PvpPlayerBinding(match, team, PvpMatchState.Live, false, false, false, false, false);

        private static BattlegroundObjectiveTag Tag(PvpMatch match) => new BattlegroundObjectiveTag(match.MatchId, 0, Defenders);

        private static void SetField(object target, Type owner, string name, object value)
        {
            var field = owner.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{owner.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }

        /// <summary>A seeded Player with a working Health vital, position store and binding, standing at <paramref name="at"/>.</summary>
        private static Player LivePlayer(string name, PvpPlayerBinding binding, Position at)
        {
            var p = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            SetField(p, typeof(WorldObject), "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetField(p, typeof(WorldObject), "BiotaDatabaseLock", new ReaderWriterLockSlim());
            p.SetPvpBindingForTests(binding);
            p.Biota.PropertiesAttribute2nd = new Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute2nd, ACE.Entity.Models.PropertiesAttribute2nd>();
            p.Biota.PropertiesPosition = new Dictionary<ACE.Entity.Enum.Properties.PositionType, ACE.Entity.Models.PropertiesPosition>();
            p.Biota.PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string>();

            var vitals = new Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute2nd, ACE.Server.WorldObjects.Entity.CreatureVital>
            {
                { ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth, new ACE.Server.WorldObjects.Entity.CreatureVital(p, ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth) },
            };
            SetField(p, typeof(Creature), "Vitals", vitals);

            foreach (var store in new[] { "positionCache", "ephemeralPositions" })
            {
                var field = typeof(WorldObject).GetField(store, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, $"WorldObject.{store} was not found by reflection - has it been renamed?");
                field.SetValue(p, Activator.CreateInstance(field.FieldType));
            }

            p.Health.Current = 100;
            p.Location = new Position(at);
            p.Name = name;
            return p;
        }

        /// <summary>
        /// A landblock shell carrying only its live player list, the one field the production sample source reads. No constructor runs (a
        /// real Landblock loads cells from the dats); the list is the same private List&lt;Player&gt; ProcessPendingWorldObjectAdditionsAndRemovals fills.
        /// </summary>
        private static Landblock LandblockWith(params Player[] players)
        {
            var landblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));
            SetField(landblock, typeof(Landblock), "players", new List<Player>(players));
            Assert.AreEqual(players.Length, landblock.PlayersOnLandblockThread.Count, "fixture: the landblock lists exactly these players");
            return landblock;
        }

        /// <summary>A tagged 1000-point crystal at the Great Hall site, on <paramref name="landblock"/>.</summary>
        private static Creature Crystal(PvpMatch match, Landblock landblock)
        {
            var crystal = TestCreatures.CreateDefender(maxHealth: 990);
            Assert.IsTrue(crystal.SetBattlegroundObjective(Tag(match)));
            crystal.Location = new Position(CrystalAt);
            crystal.CurrentLandblock = landblock;
            return crystal;
        }

        /// <summary>
        /// The owner's scenario on one landblock: an attacker and a defender of the same Live match, the defender standing
        /// <paramref name="defenderMetres"/> due south of the crystal, and two idle players (a teammate of each) farther off.
        /// </summary>
        private (PvpMatch Match, Creature Crystal, Player Attacker, Player Defender) Scenario(CrystalDefenderReductionDials dials, float defenderMetres)
        {
            var match = NewMatch(dials);
            var attacker = LivePlayer("Roz", Bound(match, Attackers), At(110f, -68f));
            var defender = LivePlayer("Rawzz", Bound(match, Defenders), At(110f, -70f - defenderMetres));
            var idleAttacker = LivePlayer("Shoff", Bound(match, Attackers), At(60f, -20f));
            var idleDefender = LivePlayer("Arema Crier", Bound(match, Defenders), At(30f, -20f));
            var crystal = Crystal(match, LandblockWith(attacker, defender, idleAttacker, idleDefender));

            return (match, crystal, attacker, defender);
        }

        // ================= the production sample source, end to end =================

        /// <summary>The shipped DefenderSampleSource reads every player on the crystal's landblock, through Player.ToDefenderSample.</summary>
        [TestMethod]
        public void TheProductionSampleSource_ReadsTheCrystalsLandblockPlayers()
        {
            var s = Scenario(Intended, 4f);

            var samples = savedSamples(s.Crystal).ToList();

            Assert.AreEqual(4, samples.Count, "one sample per player on the landblock");
            Assert.AreEqual(2, samples.Count(x => x.Binding.TeamIndex == Defenders));
            Assert.IsTrue(samples.All(x => x.Alive), "every fixture player is alive");
        }

        /// <summary>
        /// The owner's intended dials, the real stamp and the real source: a defender who landed damage on the attacker 3 s ago, 4 m from the
        /// crystal, cuts a 400-point hit to 5% (per 2 counts as 1, cap 4 counts as 0.95). Control: the same fixture before the defender lands
        /// anything takes the full hit, and the diagnostics line says why (no stamp).
        /// </summary>
        [TestMethod]
        public void IntendedDials_AnEngagedDefenderOnTheLandblock_CutsTheHitToFivePercent()
        {
            var s = Scenario(Intended, 4f);
            Creature.DefenderSampleSource = savedSamples;

            Assert.AreEqual(400u, s.Crystal.TakeDamage(s.Attacker, DamageType.Slash, 400f), "control: before any PvP damage lands, nobody is engaged");
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "engaged defenders 0 of 4 candidate(s)");
            StringAssert.Contains(lines[0], "wrong team 2, not alive 0, out of radius 0, no stamp 2, stale stamp 0");

            // the defender lands 50 points on the attacker: the real DamageHistory hook stamps both
            Player.OnPvpDamageRecorded(s.Attacker, s.Defender, 50);
            Assert.IsNotNull(s.Defender.PvpEngagement, "fixture: the defender is stamped by dealing damage");

            clock = Now.AddSeconds(3);
            var dealt = s.Crystal.TakeDamage(s.Attacker, DamageType.Slash, 400f);

            Assert.AreEqual(20u, dealt, "one engaged defender with per 2 (counts as 1) and cap 4 (counts as 0.95): 5% of 400");
        }

        /// <summary>
        /// The same scenario with the dials stage actually held (radius 5, window 10). A defender 8 m from the crystal is out of radius, and
        /// one 3 m away whose last landed hit was 15 s ago is stale: both hits land in full. This is the owner's report, and it is the dials,
        /// not the code: the intended-dials test above, on the same fixture, reduces.
        /// </summary>
        [TestMethod]
        public void StageDials_ADefenderEightMetresOut_OrFifteenSecondsIdle_IsNotEngaged()
        {
            var far = Scenario(StageHeld, 8f);
            Creature.DefenderSampleSource = savedSamples;
            Player.OnPvpDamageRecorded(far.Attacker, far.Defender, 50);

            Assert.AreEqual(400u, far.Crystal.TakeDamage(far.Attacker, DamageType.Slash, 400f), "8 m with a 5 m radius: not engaged");
            StringAssert.Contains(lines.Last(), "out of radius 2", "the engaged defender at 8 m and the idle one across the hall");
            StringAssert.Contains(lines.Last(), "nearest defender 8.0 m");
            StringAssert.Contains(lines.Last(), "radius 5 m, window 10 s");

            var idle = Scenario(StageHeld, 3f);
            Player.OnPvpDamageRecorded(idle.Attacker, idle.Defender, 50);
            clock = Now.AddSeconds(15);

            Assert.AreEqual(400u, idle.Crystal.TakeDamage(idle.Attacker, DamageType.Slash, 400f), "last landed hit 15 s ago with a 10 s window: not engaged");
            StringAssert.Contains(lines.Last(), "out of radius 1, no stamp 0, stale stamp 1");
            StringAssert.Contains(lines.Last(), "freshest stamp 15.0 s ago");

            // control on the stage dials: within 5 m and 10 s the same rule reduces
            var close = Scenario(StageHeld, 3f);
            Player.OnPvpDamageRecorded(close.Attacker, close.Defender, 50);

            Assert.AreEqual(20u, close.Crystal.TakeDamage(close.Attacker, DamageType.Slash, 400f), "3 m and 0 s: engaged, 5% lands");
        }

        // ================= the standing and the tally =================

        [TestMethod]
        public void Classify_NamesTheFirstFailingCondition_AndMatchesIsEngagedDefender()
        {
            var match = NewMatch(StageHeld);
            var tag = Tag(match);
            var stamp = new PvpEngagementStamp(match.MatchId, Now.AddSeconds(-2));

            var cases = new (DefenderSample Sample, DefenderStanding Expected)[]
            {
                (new DefenderSample(Bound(match, Defenders), true, At(110f, -72f), stamp), DefenderStanding.Engaged),
                (new DefenderSample(Bound(match, Attackers), true, At(110f, -72f), stamp), DefenderStanding.WrongTeam),
                (new DefenderSample(null, true, At(110f, -72f), stamp), DefenderStanding.WrongTeam),
                (new DefenderSample(Bound(NewMatch(StageHeld), Defenders), true, At(110f, -72f), stamp), DefenderStanding.WrongTeam),
                (new DefenderSample(Bound(match, Defenders), false, At(110f, -72f), stamp), DefenderStanding.NotAlive),
                (new DefenderSample(new PvpPlayerBinding(match, Defenders, PvpMatchState.Live, false, false, false, false, false, respawning: true), true, At(110f, -72f), stamp), DefenderStanding.NotAlive),
                (new DefenderSample(Bound(match, Defenders), true, At(110f, -76f), stamp), DefenderStanding.OutOfRadius),
                (new DefenderSample(Bound(match, Defenders), true, At(110f, -72f, instance: Instance + 1), stamp), DefenderStanding.OutOfRadius),
                (new DefenderSample(Bound(match, Defenders), true, null, stamp), DefenderStanding.OutOfRadius),
                (new DefenderSample(Bound(match, Defenders), true, At(110f, -72f), null), DefenderStanding.NoStamp),
                (new DefenderSample(Bound(match, Defenders), true, At(110f, -72f), new PvpEngagementStamp(Guid.NewGuid(), Now)), DefenderStanding.NoStamp),
                (new DefenderSample(Bound(match, Defenders), true, At(110f, -72f), new PvpEngagementStamp(match.MatchId, Now.AddSeconds(-11))), DefenderStanding.StaleStamp),
                (new DefenderSample(Bound(match, Defenders), true, At(110f, -72f), new PvpEngagementStamp(match.MatchId, Now.AddSeconds(1))), DefenderStanding.StaleStamp),
            };

            foreach (var (sample, expected) in cases)
            {
                var standing = CrystalDefenderReduction.Classify(tag, CrystalAt, StageHeld, sample, Now, out _, out _);

                Assert.AreEqual(expected, standing, $"{sample}");
                Assert.AreEqual(expected == DefenderStanding.Engaged, CrystalDefenderReduction.IsEngagedDefender(tag, CrystalAt, StageHeld, sample, Now), $"IsEngagedDefender agrees: {sample}");
            }

            Assert.AreEqual(DefenderStanding.RuleOff, CrystalDefenderReduction.Classify(tag, CrystalAt, StageHeld with { Radius = 0 }, cases[0].Sample, Now, out _, out _));
            Assert.AreEqual(DefenderStanding.RuleOff, CrystalDefenderReduction.Classify(tag, CrystalAt, StageHeld with { WindowSeconds = double.NaN }, cases[0].Sample, Now, out _, out _));

            var samples = cases.Select(c => c.Sample).ToList();
            var tally = CrystalDefenderReduction.Tally(tag, CrystalAt, StageHeld, samples, Now);

            Assert.AreEqual(new DefenderTally(1, 0, 3, 2, 3, 2, 2, 2.0, -1.0), tally, "per-reason counts; nearest 2 m; the tally keeps the future stamp's raw age (-1 s) as the freshest");
            Assert.AreEqual(samples.Count, tally.Candidates);
            Assert.AreEqual(CrystalDefenderReduction.CountEngaged(tag, CrystalAt, StageHeld, samples, Now), tally.Engaged, "Tally and CountEngaged agree");

            // the printed line clamps that negative age to 0 (it is only printed when nobody is engaged)
            var unengaged = samples.Where(s => CrystalDefenderReduction.Classify(tag, CrystalAt, StageHeld, s, Now, out _, out _) != DefenderStanding.Engaged).ToList();
            var resolution = CrystalDefenderReduction.Resolve(tag, Bound(match, Attackers), CrystalAt, unengaged, Now, 100f);
            Assert.AreEqual(-1.0, resolution.Tally.FreshestStampSeconds, 1e-9, "fixture: the future stamp is still the freshest");
            var line = CrystalDefenderReduction.FormatHitLine(tag, "0x1", "Roz", StageHeld, 100f, resolution, 0);
            StringAssert.Contains(line, "freshest stamp 0.0 s ago");
            Assert.IsFalse(line.Contains("-1.0"), line);
        }

        [TestMethod]
        public void DescribeDials_ShowsRawValues_AndWhatTheRuleUses()
        {
            Assert.AreEqual("per 2 (counts as 1), cap 4 (counts as 0.95), radius 300 m, window 30 s", CrystalDefenderReduction.DescribeDials(Intended));
            Assert.AreEqual("per 0.125, cap 0.5, radius 5 m, window 10 s", CrystalDefenderReduction.DescribeDials(CrystalDefenderReductionDials.From(BattlegroundTunables.Defaults)));
            Assert.AreEqual("per 0, cap 0.5, radius -1 (off), window 10 s", CrystalDefenderReduction.DescribeDials(new CrystalDefenderReductionDials(0.0, 0.5, -1.0, 10.0)));
            Assert.AreEqual("per -0.1 (counts as 0), cap 0.5, radius 5 m, window 10 s", CrystalDefenderReduction.DescribeDials(new CrystalDefenderReductionDials(-0.1, 0.5, 5.0, 10.0)));
            Assert.AreEqual("per 0.125, cap 0.5, radius 0 (off), window 0 (off)", CrystalDefenderReduction.DescribeDials(new CrystalDefenderReductionDials(0.125, 0.5, 0.0, 0.0)), "a radius or window of exactly 0 is off too, though sanitizing leaves it unchanged");
            Assert.AreEqual("off", CrystalDefenderReduction.DescribeDials(null));
        }

        // ================= the throttled per-crystal line =================

        [TestMethod]
        public void TheHitLine_IsThrottledPerCrystal_ToOneEveryFiveSeconds()
        {
            var a = Scenario(Intended, 4f);
            var b = Scenario(Intended, 4f);
            Creature.DefenderSampleSource = savedSamples;

            a.Crystal.TakeDamage(a.Attacker, DamageType.Slash, 10f);
            a.Crystal.TakeDamage(a.Attacker, DamageType.Slash, 10f);
            b.Crystal.TakeDamage(b.Attacker, DamageType.Slash, 10f);
            Assert.AreEqual(2, lines.Count, "one line for each crystal; the second hit on crystal a is throttled");

            clock = Now.AddSeconds(4.9);
            a.Crystal.TakeDamage(a.Attacker, DamageType.Slash, 10f);
            Assert.AreEqual(2, lines.Count, "4.9 s later: still throttled");

            clock = Now.AddSeconds(5);
            a.Crystal.TakeDamage(a.Attacker, DamageType.Slash, 10f);
            Assert.AreEqual(3, lines.Count, "5 s later: logs again");
            StringAssert.Contains(lines[2], "(+2 earlier hit(s) on this crystal not logged)");

            // a hit the rule does not consider (a defender's own, refused by the gate anyway) is never logged nor counted
            clock = Now.AddSeconds(20);
            a.Crystal.TakeDamage(a.Defender, DamageType.Slash, 10f);
            Assert.AreEqual(3, lines.Count);
        }

        [TestMethod]
        public void TheHitLine_ReportsRolledLandedMultiplierAndDials()
        {
            var match = NewMatch(Intended);
            var tag = Tag(match);
            var engaged = new DefenderSample(Bound(match, Defenders), true, At(110f, -72f), new PvpEngagementStamp(match.MatchId, Now));
            var resolution = CrystalDefenderReduction.Resolve(tag, Bound(match, Attackers), CrystalAt, new[] { engaged }, Now, 400f);

            Assert.IsTrue(resolution.Qualified);
            Assert.AreEqual(20f, resolution.Amount, 1e-3f);

            var line = CrystalDefenderReduction.FormatHitLine(tag, "0x80001234", "Roz", Intended, 400f, resolution, 0);

            Assert.AreEqual($"[PVP] crystal 0 of match {match.MatchId} (0x80001234): Roz hit 400 -> 20 (x0.05); engaged defenders 1 of 1 candidate(s); dials per 2 (counts as 1), cap 4 (counts as 0.95), radius 300 m, window 30 s", line);

            var unqualified = CrystalDefenderReduction.Resolve(tag, Bound(match, Defenders), CrystalAt, new[] { engaged }, Now, 400f);
            Assert.AreEqual(new CrystalHitResolution(false, 400f, 1.0, default), unqualified, "a defender's hit is not the rule's: unchanged, no tally");
        }

        // ================= the formation log =================

        private static string RepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "could not find the repo root by walking up from the test directory");

            return File.ReadAllText(Path.Combine(new[] { dir.FullName, "Source", "ACE.Server" }.Concat(relative).ToArray())).Replace("\r\n", "\n");
        }

        /// <summary>The formation log line prints the snapshot the match will use, AFTER it is set on the match.</summary>
        [TestMethod]
        public void TheFormationLog_PrintsTheSnapshottedDials()
        {
            var text = RepoFile("Pvp", "PvpMatchCoordinator.cs");
            var start = text.IndexOf("private AttackDefendPlan BindAttackDefendPlan(MatchRuntime m, BattlegroundDials d, bool sidesSwapped)", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "BindAttackDefendPlan not found");
            var body = text.Substring(start, text.IndexOf("\n        }\n", start, StringComparison.Ordinal) - start);

            var set = body.IndexOf("m.Match.DefenderReduction = CrystalDefenderReductionDials.From(d);", StringComparison.Ordinal);
            var logged = body.IndexOf("engaged-defender reduction {CrystalDefenderReduction.DescribeDials(m.Match.DefenderReduction)}", StringComparison.Ordinal);

            Assert.IsTrue(set >= 0, "the snapshot is set on the match");
            Assert.IsTrue(logged > set, "the formation log line prints the dials the match carries, after they are set");
        }
    }
}
