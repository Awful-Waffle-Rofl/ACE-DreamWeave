using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The player-side half of arena overtime (Docs/Pvp/DESIGN.md "Overtime"): the healing factor folded into
    /// PvpRules.ResolveHealingMultiplier, the H1/H2 overtime refusal (PvpRules.TryBlockOvertimeHeal), and the OT1-OT4
    /// damage ramp (PvpRules.ApplyOvertimeRamp). The coordinator half (entry, republish, hard end, messaging) is in
    /// PvpMatchCoordinatorTests.
    ///
    /// Exercised through PvpRules' entry points on reflection-seeded Players carrying bindings set with
    /// SetPvpBindingForTests; a real kit, potion or hit cannot be driven in this host (no world database, no dat), so
    /// the kit/potion refusal keeping the item is pinned by SOURCE-ORDER checks that the refusal returns before the
    /// code that spends the item (matched on CODE only, comments stripped).
    ///
    /// SEEDING: no PropertyManager key is read. PvpRuleTunables.DialSource and PvpTunables.DialSource are swapped
    /// for counting sources, PvpRules.UtcNowSource for a fixed, counting clock, and PvpRules.MaxHealthSource for a
    /// constant (seeded Players have no vitals). Every seam is saved and restored per test.
    /// </summary>
    [TestClass]
    public class PvpArenaOvertimeRulesTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

        private Func<PvpRuleDials> savedRuleDials;
        private Func<PvpArenaDials> savedArenaDials;
        private Func<DateTime> savedNow;
        private Func<Creature, double> savedMaxHealth;
        private Action<PvpChokePoint, double, double> savedObserver;
        private Func<Player, Player, bool> savedArenaScope;

        private int ruleDialReads, arenaDialReads, clockReads;
        private List<(PvpChokePoint Point, double Before, double After)> observed;

        [TestInitialize]
        public void Setup()
        {
            savedRuleDials = PvpRuleTunables.DialSource;
            savedArenaDials = PvpTunables.DialSource;
            savedNow = PvpRules.UtcNowSource;
            savedMaxHealth = PvpRules.MaxHealthSource;
            savedObserver = PvpRules.Observer;
            savedArenaScope = PvpClassifier.ArenaScopeSource;

            ruleDialReads = arenaDialReads = clockReads = 0;
            observed = new List<(PvpChokePoint, double, double)>();

            UseRuleDials(PvpRuleTunables.Defaults);
            PvpTunables.DialSource = () => { arenaDialReads++; return PvpTunables.Defaults; };
            PvpRules.UtcNowSource = () => { clockReads++; return Now; };
            PvpRules.MaxHealthSource = c => 1000;
            PvpRules.Observer = (p, b, a) => observed.Add((p, b, a));
            PvpClassifier.ArenaScopeSource = PvpClassifier.DefaultArenaScope;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpRuleTunables.DialSource = savedRuleDials;
            PvpTunables.DialSource = savedArenaDials;
            PvpRules.UtcNowSource = savedNow;
            PvpRules.MaxHealthSource = savedMaxHealth;
            PvpRules.Observer = savedObserver;
            PvpClassifier.ArenaScopeSource = savedArenaScope;
        }

        // ================= fixtures =================

        private void UseRuleDials(PvpRuleDials dials) => PvpRuleTunables.DialSource = () => { ruleDialReads++; return dials; };

        private static Player Seeded(PlayerKillerStatus status = PlayerKillerStatus.NPK)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            player.PlayerKillerStatus = status;

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static PvpMatch NewMatch()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), ArenaMapCatalog.OneVOneKey, teams, Now);
        }

        private static PvpPlayerBinding Regulation(PvpMatch match, int team) =>
            new PvpPlayerBinding(match, team, PvpMatchState.Live, false, false, false, false, false);

        private static PvpPlayerBinding Overtime(PvpMatch match, int team, double healingMod = 0.0, double ramp = 0.0, double secondsIn = 0) =>
            new PvpPlayerBinding(match, team, PvpMatchState.Live, false, false, false, false, false, true, Now.AddSeconds(-secondsIn), healingMod, ramp);

        private static Player Bound(PvpPlayerBinding binding)
        {
            var player = Seeded();
            player.SetPvpBindingForTests(binding);
            return player;
        }

        /// <summary>Two players bound to the same Live match, both in overtime with the given snapshot.</summary>
        private static (Player Attacker, Player Defender) OvertimePair(double ramp, double secondsIn, double healingMod = 0.0)
        {
            var match = NewMatch();
            return (Bound(Overtime(match, 0, healingMod, ramp, secondsIn)), Bound(Overtime(match, 1, healingMod, ramp, secondsIn)));
        }

        // ================= healing factor =================

        [TestMethod]
        public void HealOnAnOvertimePlayer_IsZero_TheSameHealInRegulation_IsUnchanged()
        {
            var match = NewMatch();
            var inOvertime = Bound(Overtime(match, 0));
            var inRegulation = Bound(Regulation(match, 1));

            Assert.AreEqual(0.0f, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, inOvertime, 500.0f), "overtime with the shipped healing mod 0: the heal lands for 0");
            Assert.AreEqual(500.0f, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, inRegulation, 500.0f), "the same heal before overtime is untouched");

            foreach (var point in new[] { PvpChokePoint.HL1, PvpChokePoint.HL3 })
                Assert.AreEqual(0, PvpRules.ApplyHealingMod(point, inOvertime, 300), $"{point}: int boost");

            Assert.AreEqual(0u, PvpRules.ApplyHealingMod(PvpChokePoint.HL5, inOvertime, 400u), "HL5: a Drain's Health gain");
            Assert.AreEqual(0u, PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5F, inOvertime, 400u), "HL5F: a fellow's share");
        }

        [TestMethod]
        public void OvertimeFactor_MultipliesWithPvpHealingMod()
        {
            UseRuleDials(PvpRuleTunables.Defaults with { HealingMod = 0.5 });

            var player = Bound(Overtime(NewMatch(), 0, healingMod: 0.5));

            Assert.AreEqual(0.25, PvpRules.ResolveHealingMultiplier(player), 1e-12);
            Assert.AreEqual(125.0, PvpRules.ApplyHealingMod(PvpChokePoint.HL2, player, 500.0), 1e-12);
        }

        [TestMethod]
        public void OvertimeFactor_AppliesWithThePvpRulesMasterSwitchOff()
        {
            UseRuleDials(PvpRuleTunables.Defaults with { Enabled = false, HealingMod = 0.5 });

            Assert.AreEqual(0.0, PvpRules.ResolveHealingMultiplier(Bound(Overtime(NewMatch(), 0))), "overtime is an arena rule, not a pvp_rules lever");
            Assert.AreEqual(1.0, PvpRules.ResolveHealingMultiplier(Bound(Regulation(NewMatch(), 0))), "with the switch off pvp_healing_mod itself still does nothing");
        }

        [TestMethod]
        public void OvertimeHealingFactor_BadValuesAreTheIdentity_AndOnlyLiveOvertimeCounts()
        {
            var match = NewMatch();

            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.5 })
                Assert.AreEqual(1.0, PvpRules.OvertimeHealingFactor(Overtime(match, 0, healingMod: bad)), $"{bad}: identity");

            Assert.AreEqual(0.0, PvpRules.OvertimeHealingFactor(Overtime(match, 0, healingMod: 0.0)));
            Assert.AreEqual(1.0, PvpRules.OvertimeHealingFactor(null));
            Assert.AreEqual(1.0, PvpRules.OvertimeHealingFactor(Regulation(match, 0)));

            var resolving = new PvpPlayerBinding(match, 0, PvpMatchState.Resolving, false, false, false, false, false, true, Now, 0.0, 0.0);
            Assert.AreEqual(1.0, PvpRules.OvertimeHealingFactor(resolving), "only a LIVE binding in overtime counts");
        }

        // ================= H1 / H2 refusal =================

        [TestMethod]
        public void TryBlockOvertimeHeal_RefusesOnlyAtAFactorOfZero()
        {
            var match = NewMatch();

            Assert.AreEqual(PvpConsumableBlock.Overtime, PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H1, Bound(Overtime(match, 0))));
            Assert.AreEqual(PvpChokePoint.H1, observed.Single().Point, "the refusal is reported at the gate that made it");

            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H2, Bound(Regulation(match, 0))), "regulation: the kit is used as normal");
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H2, Bound(Overtime(match, 0, healingMod: 0.5))), "a reduced heal is scaled at HL2/HL3, not refused");
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H2, Bound(Overtime(match, 0, healingMod: double.NaN))), "NaN is the identity");
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H2, Seeded()), "no binding");
            Assert.AreEqual(PvpConsumableBlock.None, PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H2, null));

            Assert.AreEqual(0, ruleDialReads + arenaDialReads + clockReads, "the refusal reads nothing but the binding");
        }

        /// <summary>
        /// The kit refusal keeps the kit: in Healer.cs the overtime check is gated on a HEALTH kit, keyed on the
        /// healed target, and returns BEFORE DoHealing (which is what decrements the uses and consumes the kit).
        /// </summary>
        [TestMethod]
        public void H1Overtime_IsPinned_BeforeDoHealing_AndReturns()
        {
            var path = SourcePath("ACE.Server", "WorldObjects", "Healer.cs");
            var lines = CodeLines(path);

            var check = IndexOfCode(lines, "if (PvpRules.HealsHealth(BoosterEnum) && PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H1, target) == PvpConsumableBlock.Overtime)");
            var doHealing = IndexOfCode(lines, "DoHealing(healer, target, missingVital);");

            Assert.IsTrue(check >= 0, "the H1 overtime check is missing");
            Assert.IsTrue(check < doHealing, "the H1 overtime check must precede DoHealing");
            CollectionAssert.AreEqual(
                new[] { "{", "healer.IsBusy = false;", "healer.Session?.Network.EnqueueSend(new GameMessageSystemChat(Pvp.PvpArenaText.OvertimeHealRefused, ChatMessageType.Broadcast));", "healer.SendUseDoneEvent();", "return;", "}" },
                lines.Skip(check + 1).Take(6).ToArray(),
                "the refusal tells the healer why and RETURNS, so the kit is never spent");
        }

        /// <summary>
        /// The potion refusal keeps the potion: in Player_Use.cs's H2 wrapper the overtime check returns BEFORE
        /// action() (the item's own apply-and-consume), and Food passes healsHealth for a positive Health boost only.
        /// </summary>
        [TestMethod]
        public void H2Overtime_IsPinned_BeforeTheConsumableAction_AndFoodPassesHealsHealth()
        {
            var lines = CodeLines(SourcePath("ACE.Server", "WorldObjects", "Player_Use.cs"));

            var check = IndexOfCode(lines, "if (healsHealth && PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H2, this) == PvpConsumableBlock.Overtime)");
            var action = IndexOfCode(lines, "action();");

            Assert.IsTrue(check >= 0, "the H2 overtime check is missing");
            Assert.IsTrue(check < action, "the H2 overtime check must precede action()");
            CollectionAssert.AreEqual(
                new[] { "{", "Session?.Network.EnqueueSend(new GameMessageSystemChat(Pvp.PvpArenaText.OvertimeHealRefused, ChatMessageType.Broadcast));", "return;", "}" },
                lines.Skip(check + 1).Take(4).ToArray());

            var food = CodeLines(SourcePath("ACE.Server", "WorldObjects", "Food.cs"));
            Assert.IsTrue(IndexOfCode(food, "player.ApplyConsumable(motionCommand, () => ApplyConsumable(player), healsHealth: PvpRules.HealsHealth(BoosterEnum) && BoostValue > 0);") >= 0,
                "Food must flag a positive Health boost so H2 can refuse it");
        }

        // ================= damage ramp =================

        [TestMethod]
        public void OvertimeRampMultiplier_StepsPerWholeMinute()
        {
            Assert.AreEqual(1.0, PvpRules.OvertimeRampMultiplier(0.1, 0));
            Assert.AreEqual(1.0, PvpRules.OvertimeRampMultiplier(0.1, 59.999), "no step before a whole minute");
            Assert.AreEqual(1.1, PvpRules.OvertimeRampMultiplier(0.1, 60), 1e-12);
            Assert.AreEqual(1.1, PvpRules.OvertimeRampMultiplier(0.1, 119), 1e-12);
            Assert.AreEqual(1.2, PvpRules.OvertimeRampMultiplier(0.1, 120), 1e-12);
            Assert.AreEqual(1.5, PvpRules.OvertimeRampMultiplier(0.25, 150), 1e-12);

            foreach (var bad in new[] { 0.0, -0.1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.AreEqual(1.0, PvpRules.OvertimeRampMultiplier(bad, 600), $"ramp {bad} is off");

            Assert.AreEqual(1.0, PvpRules.OvertimeRampMultiplier(0.1, -120), "negative elapsed counts as 0 minutes");
            Assert.AreEqual(1.0, PvpRules.OvertimeRampMultiplier(double.MaxValue, 1e300), "a non-finite product falls back to the identity");
        }

        /// <summary>
        /// The ramp multiplies the CAPPED hit: an 800 hit capped at 500 (half of 1000 max health) at C1, then ramped
        /// 2 minutes into overtime at 0.1 per minute, lands as 600 - not 800 x 1.2 = 960 capped back to 500.
        /// </summary>
        [TestMethod]
        public void CappedHit_TimesRamp()
        {
            UseRuleDials(PvpRuleTunables.Defaults with { DamageCap = 0, DamageCapMaxHealthFraction = 0.5 });
            var (a, d) = OvertimePair(ramp: 0.1, secondsIn: 125);

            var capped = PvpRules.ApplyDamageCap(PvpChokePoint.C1, a, d, 800.0f);
            Assert.AreEqual(500.0f, capped);

            var ramped = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT1, a, d, capped);
            Assert.AreEqual(600.0f, ramped, 1e-3f);
            Assert.AreEqual(PvpChokePoint.OT1, observed.Last().Point);

        }

        [TestMethod]
        public void Ramp_IntegerOverload_Floors()
        {
            var (a, d) = OvertimePair(ramp: 0.25, secondsIn: 60);

            Assert.AreEqual(125u, PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, a, d, 100u), "100 x 1.25 = 125");
            Assert.AreEqual(13u, PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, a, d, 11u), "11 x 1.25 = 13.75, floored to 13");
        }

        /// <summary>
        /// Open-world PvP never ramps and reads NOTHING: two PKs with no binding (and a regulation arena pair, and a
        /// pair in DIFFERENT matches) hand the hit back with no setting read and no clock read. An arena overtime pair
        /// with the ramp at 0 reads nothing beyond the binding either.
        /// </summary>
        [TestMethod]
        public void Ramp_OpenWorldAndRampZero_AreUnchanged_WithZeroReads()
        {
            var openA = Seeded(PlayerKillerStatus.PK);
            var openD = Seeded(PlayerKillerStatus.PK);

            var match = NewMatch();
            var regA = Bound(Regulation(match, 0));
            var regD = Bound(Regulation(match, 1));

            var otherA = Bound(Overtime(NewMatch(), 0, ramp: 0.5, secondsIn: 300));
            var otherD = Bound(Overtime(NewMatch(), 1, ramp: 0.5, secondsIn: 300));

            var (zeroA, zeroD) = OvertimePair(ramp: 0.0, secondsIn: 300);

            foreach (var point in new[] { PvpChokePoint.OT1, PvpChokePoint.OT2, PvpChokePoint.OT3 })
            {
                Assert.AreEqual(500.0f, PvpRules.ApplyOvertimeRamp(point, openA, openD, 500.0f), $"{point}: open world");
                Assert.AreEqual(500.0f, PvpRules.ApplyOvertimeRamp(point, regA, regD, 500.0f), $"{point}: arena, regulation");
                Assert.AreEqual(500.0f, PvpRules.ApplyOvertimeRamp(point, otherA, otherD, 500.0f), $"{point}: two different matches is not an arena pair");
                Assert.AreEqual(500.0f, PvpRules.ApplyOvertimeRamp(point, zeroA, zeroD, 500.0f), $"{point}: ramp 0");
            }

            Assert.AreEqual(500u, PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, openA, openD, 500u));

            Assert.AreEqual(0, ruleDialReads, "no pvp_rules setting is read");
            Assert.AreEqual(0, arenaDialReads, "no pvp_arena setting is read");
            Assert.AreEqual(0, clockReads, "not even the clock is read");
            Assert.AreEqual(0, observed.Count);
        }

        /// <summary>
        /// Code review on #1433: OT4 must not inflate the caster's Drain gain through the cloak proc's gain
        /// recompute. Runs the SAME functions HandleCastSpell_Transfer runs, in the order it runs them now (pinned by
        /// PvpDamageCapTests: the cloak recompute via DrainGainFromLoss and HL5 BEFORE OT4 at the Health write):
        /// a 200 drain on a cloaked defender (flat 100 reduction), ramp x1.5, overtime heal factor 0.5. The caster
        /// gains the NO-RAMP gain x 0.5 = 50, while the victim still loses the ramped 150. The old order (ramp
        /// first) would have paid 100. The transfer method itself needs the client dat (Spell), so it cannot be
        /// driven whole here.
        /// </summary>
        [TestMethod]
        public void Ot4_DoesNotInflateTheCasterGain_ThroughTheCloakRecompute()
        {
            var (caster, victim) = OvertimePair(ramp: 0.5, secondsIn: 60, healingMod: 0.5);
            const uint cappedLoss = 200;
            const float lossPercent = 0.0f, boostMod = 1.0f;

            // production order: cloak on the unramped loss, gain recomputed from it, HL5 on the caster, then OT4
            var cloaked = Cloak.GetReducedAmount(caster, victim, cappedLoss);
            Assert.AreEqual(100u, cloaked, "the PvP cloak proc takes a flat 100 (pvp_cloak_damage_reduction default)");

            var noRampGain = PvpRules.DrainGainFromLoss(cloaked, lossPercent, boostMod);
            var casterGain = PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5, caster, noRampGain);
            var victimLoss = PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, caster, victim, cloaked);

            Assert.AreEqual(50u, casterGain, "the caster's gain is the no-ramp gain (100) x the overtime heal factor 0.5");
            Assert.AreEqual(150u, victimLoss, "the victim's loss still reflects the x1.5 ramp");

            // control: the order the review flagged (ramp before the cloak recompute) pays the caster MORE
            var buggyGain = PvpRules.ApplyHealingModToRecipient(PvpChokePoint.HL5, caster,
                PvpRules.DrainGainFromLoss(Cloak.GetReducedAmount(caster, victim, PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT4, caster, victim, cappedLoss)), lossPercent, boostMod));
            Assert.AreEqual(100u, buggyGain, "the ramp-before-cloak order inflates the gain; this is what the fix removes");
        }

        [TestMethod]
        public void Ramp_SelfAndNonPositive_AreUnchanged()
        {
            var (a, d) = OvertimePair(ramp: 0.5, secondsIn: 300);

            Assert.AreEqual(500.0f, PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT3, a, a, 500.0f), "self is not PvP");
            Assert.AreEqual(0.0f, PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT1, a, d, 0.0f));
            Assert.AreEqual(-5.0f, PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT1, a, d, -5.0f));
            Assert.AreEqual(1750.0f, PvpRules.ApplyOvertimeRamp(PvpChokePoint.OT1, a, d, 500.0f), "control: the same pair DOES ramp a positive hit (1 + 0.5 x 5 minutes = x3.5)");
        }

        // ================= helpers =================

        private static string SourcePath(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.sln")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.sln by walking up from {AppContext.BaseDirectory}");

            var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            Assert.IsTrue(File.Exists(path), $"missing {path}");
            return path;
        }

        /// <summary>Each line with any // comment stripped and trimmed, so a pin matches code, never a comment.</summary>
        private static string[] CodeLines(string path) => File.ReadAllLines(path).Select(raw =>
        {
            var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
            return (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
        }).Where(l => l.Length > 0).ToArray();

        private static int IndexOfCode(string[] lines, string code)
        {
            var hits = Enumerable.Range(0, lines.Length).Where(i => lines[i] == code).ToList();
            Assert.IsTrue(hits.Count <= 1, $"`{code}` appears {hits.Count} times");
            return hits.Count == 1 ? hits[0] : -1;
        }
    }
}
