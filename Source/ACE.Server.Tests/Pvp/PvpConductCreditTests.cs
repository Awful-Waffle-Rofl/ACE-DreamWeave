using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The player-side conduct signals (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): the heal credit (HealingDone and the activity stamp),
    /// the support-cast stamp, the damage-gate stamp on hostile attempts, and the damage hook's activity stamp. Driven on reflection-seeded
    /// Players (no database, no dat). The heal sites themselves need a Player with live vitals and skills, which this host cannot build, so
    /// "the credit is the health the write ADDED" is proved in two halves: the real Creature.UpdateVitalDelta returns only what it added (an
    /// overheal is clamped), and source pins show both sites pass that return value, after the write.
    ///
    /// SEEDING: no PropertyManager key is read. The player-side clock (PvpArenaHookSettings.UtcNow) and the friendly-fire seam
    /// (PvpArenaHookSettings.FriendlyFireSource) are swapped per test and restored in cleanup.
    /// </summary>
    [TestClass]
    public class PvpConductCreditTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        private const uint A = 0x50000011, B = 0x50000012, C = 0x50000013;

        private const uint Instance = 0x0001_0400;

        private Func<DateTime> savedClock;

        private Func<bool> savedFriendlyFire;

        [TestInitialize]
        public void Setup()
        {
            savedClock = PvpArenaHookSettings.UtcNow;
            savedFriendlyFire = PvpArenaHookSettings.FriendlyFireSource;
            PvpArenaHookSettings.UtcNow = () => Now;
            PvpArenaHookSettings.FriendlyFireSource = () => false;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.UtcNow = savedClock;
            PvpArenaHookSettings.FriendlyFireSource = savedFriendlyFire;
        }

        // ================= fixtures =================

        /// <summary>A two-team match with A and C on team 0 and B on team 1, carrying a ledger.</summary>
        private static PvpMatch NewMatch(string mode = "bg_koth")
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(A, 1500), new PvpParticipant(C, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(B, 1500) }),
            };

            var match = new PvpMatch(Guid.NewGuid(), mode, teams, Now);
            match.Performance = BattlegroundPerformanceLedger.ForTeams(teams);
            return match;
        }

        private static PvpPlayerBinding Bound(PvpMatch match, int team, PvpMatchState state = PvpMatchState.Live, bool respawning = false)
            => new PvpPlayerBinding(match, team, state, false, false, false, false, false, respawning: respawning);

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        /// <summary>A seeded Player with a guid, a binding and a working position store (the damage gate reads the target's Location).</summary>
        private static Player SeededPlayer(uint id, PvpPlayerBinding binding)
        {
            var p = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            SetInherited(p, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(p, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(p, "<Guid>k__BackingField", new ObjectGuid(id));

            p.Biota.PropertiesPosition = new Dictionary<ACE.Entity.Enum.Properties.PositionType, ACE.Entity.Models.PropertiesPosition>();

            foreach (var name in new[] { "positionCache", "ephemeralPositions" })
            {
                var store = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(store, $"WorldObject.{name} was not found by reflection - has it been renamed?");
                store.SetValue(p, Activator.CreateInstance(store.FieldType));
            }

            p.Location = new Position(0x016C0001, 10f, 10f, 0f, 0f, 0f, 0f, 1f, Instance);
            p.SetPvpBindingForTests(binding);
            return p;
        }

        private static SeatPerformanceSnapshot Perf(PvpMatch match, uint id) => match.Performance.Snapshot().Single(s => s.CharacterId == id);

        // ================= the heal rule (pure) =================

        [TestMethod]
        public void DecideHeal_Teammate_Self_Enemy_Zero()
        {
            var match = NewMatch();

            Assert.AreEqual(PvpHealCredit.ActivityAndHealing, PvpConductRules.DecideHeal(Bound(match, 0), Bound(match, 0), false, 50), "teammate");
            Assert.AreEqual(PvpHealCredit.None, PvpConductRules.DecideHeal(Bound(match, 0), Bound(match, 0), true, 50), "self: nothing, like a self-buff");
            Assert.AreEqual(PvpHealCredit.None, PvpConductRules.DecideHeal(Bound(match, 0), Bound(match, 1), false, 50), "enemy");
            Assert.AreEqual(PvpHealCredit.None, PvpConductRules.DecideHeal(Bound(match, 0), Bound(match, 0), false, 0), "nothing restored");
            Assert.AreEqual(PvpHealCredit.None, PvpConductRules.DecideHeal(Bound(match, 0), Bound(NewMatch(), 0), false, 50), "another match, same team index");
            Assert.AreEqual(PvpHealCredit.None, PvpConductRules.DecideHeal(Bound(match, 0, PvpMatchState.Countdown), Bound(match, 0, PvpMatchState.Countdown), false, 50), "before Live");
            Assert.AreEqual(PvpHealCredit.None, PvpConductRules.DecideHeal(Bound(match, 0), Bound(match, 0, respawning: true), false, 50), "healing someone in the pen");
            Assert.AreEqual(PvpHealCredit.None, PvpConductRules.DecideHeal(null, null, true, 50), "unbound self-heal");
            Assert.AreEqual(PvpHealCredit.None, PvpConductRules.DecideHeal(null, Bound(match, 0), false, 50), "unbound healer");
        }

        // ================= the heal credit through the player hook =================

        /// <summary>
        /// Positive control: a teammate heal credits the healer exactly the health restored, and stamps the healer (not the healed). Negative
        /// controls in the same match: a heal of an ENEMY credits nothing and stamps nothing; a self-heal neither stamps nor credits HealingDone (like a self-buff).
        /// </summary>
        [TestMethod]
        public void TeammateHeal_CreditsTheRestoredAmount_EnemyAndSelfDoNot()
        {
            var match = NewMatch();
            var healer = SeededPlayer(A, Bound(match, 0));
            var mate = SeededPlayer(C, Bound(match, 0));
            var enemy = SeededPlayer(B, Bound(match, 1));

            Player.OnPvpHealApplied(healer, mate, 137);

            Assert.AreEqual(137, Perf(match, A).HealingDone, "the teammate heal is credited with the health restored");
            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), healer.PvpActivity, "and stamps the healer");
            Assert.IsNull(mate.PvpActivity, "being healed is not activity");

            var enemyHealer = SeededPlayer(A, Bound(match, 0));
            Player.OnPvpHealApplied(enemyHealer, enemy, 500);

            Assert.AreEqual(137, Perf(match, A).HealingDone, "an enemy heal adds nothing");
            Assert.IsNull(enemyHealer.PvpActivity, "and stamps nothing");

            var selfHealer = SeededPlayer(C, Bound(match, 0));
            Player.OnPvpHealApplied(selfHealer, selfHealer, 300);

            Assert.AreEqual(0, Perf(match, C).HealingDone, "a self-heal earns no HealingDone");
            Assert.IsNull(selfHealer.PvpActivity, "and is not activity");
        }

        [TestMethod]
        public void Heal_OfZero_OrNull_IsNothing()
        {
            var match = NewMatch();
            var healer = SeededPlayer(A, Bound(match, 0));
            var mate = SeededPlayer(C, Bound(match, 0));

            Player.OnPvpHealApplied(healer, mate, 0);
            Player.OnPvpHealApplied(null, mate, 50);
            Player.OnPvpHealApplied(healer, null, 50);

            Assert.AreEqual(0, Perf(match, A).HealingDone);
            Assert.IsNull(healer.PvpActivity, "a heal that restored nothing (the target was full) is not activity");
        }

        [TestMethod]
        public void Heal_InAMatchWithNoLedger_StampsButCreditsNothing()
        {
            var match = NewMatch("1v1");
            match.Performance = null;
            var healer = SeededPlayer(A, Bound(match, 0));
            var mate = SeededPlayer(C, Bound(match, 0));

            Player.OnPvpHealApplied(healer, mate, 40);

            Assert.IsNotNull(healer.PvpActivity, "the stamp does not need a ledger");
        }

        /// <summary>
        /// Overheal, first half: the real vital write (Creature.UpdateVitalDelta) returns the health it ADDED, clamped at the maximum, never the
        /// amount asked for. A 50-point heal on a creature at 90 of 100 returns 10.
        /// </summary>
        [TestMethod]
        public void Overheal_TheVitalWriteReturnsOnlyWhatItAdded()
        {
            var c = TestCreatures.CreateDefender(maxHealth: 50, endurance: 100);
            Assert.AreEqual(100u, c.Health.MaxValue, "fixture: 50 + 100/2");

            c.Health.Current = 90;

            Assert.AreEqual(10, c.UpdateVitalDelta(c.Health, 50), "only the missing 10 is restored");
            Assert.AreEqual(100u, c.Health.Current);
            Assert.AreEqual(0, c.UpdateVitalDelta(c.Health, 50), "at full health nothing is restored");
        }

        /// <summary>
        /// Overheal, second half: both heal sites pass the WRITE'S RETURN VALUE to the credit, after the write. Matched on code only. Replacing
        /// `actualHealAmount` with `healAmount` (the attempted amount) in Healer.cs fails this.
        /// </summary>
        [TestMethod]
        public void HealSites_CreditTheWritesReturnValue_AfterTheWrite()
        {
            var root = FindSourceRoot();

            AssertOrder(root, "ACE.Server/WorldObjects/Healer.cs",
                "var actualHealAmount = (uint)target.UpdateVitalDelta(vital, healAmount);",
                "Player.OnPvpHealApplied(healer, target, actualHealAmount);");

            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "boost = targetCreature.UpdateVitalDelta(targetCreature.Health, tryBoost);",
                "Player.OnPvpHealApplied(conductHealer, conductHealed, (uint)boost);");

            AssertOrder(root, "ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "if (boost > 0 && this is Player conductHealer && targetCreature is Player conductHealed)",
                "Player.OnPvpHealApplied(conductHealer, conductHealed, (uint)boost);");
        }

        // ================= the support-cast stamp =================

        [TestMethod]
        public void SupportCast_OnATeammate_Stamps_SelfAndEnemyDoNot()
        {
            var match = NewMatch();
            var caster = SeededPlayer(A, Bound(match, 0));

            Player.OnPvpSupportCast(caster, SeededPlayer(C, Bound(match, 0)));
            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), caster.PvpActivity, "a buff on a teammate");

            var selfBuffer = SeededPlayer(A, Bound(match, 0));
            Player.OnPvpSupportCast(selfBuffer, selfBuffer);
            Assert.IsNull(selfBuffer.PvpActivity, "a self-buff");

            var enemyBuffer = SeededPlayer(A, Bound(match, 0));
            Player.OnPvpSupportCast(enemyBuffer, SeededPlayer(B, Bound(match, 1)));
            Assert.IsNull(enemyBuffer.PvpActivity, "a buff on an enemy");

            Assert.AreEqual(0, Perf(match, A).HealingDone, "a buff is never HealingDone");
        }

        // ================= the damage gate: hostile attempts =================

        /// <summary>
        /// A clean allow of a hostile action on an enemy stamps the attacker (misses count: the stamp is at the gate, before any roll). A
        /// teammate allowed under friendly fire does not stamp; a refusal (another match) does not stamp.
        /// </summary>
        [TestMethod]
        public void Gate_AllowOnAnEnemy_Stamps_TeammateAndRefusalDoNot()
        {
            var match = NewMatch();
            var attacker = SeededPlayer(A, Bound(match, 0));
            var enemy = SeededPlayer(B, Bound(match, 1));

            Assert.IsTrue(attacker.TryPvpArenaGate(enemy, null, out var allowed));
            Assert.IsNull(allowed, "fixture: allowed");
            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), attacker.PvpActivity, "a hostile attempt on an enemy");
            Assert.IsNull(enemy.PvpActivity, "being attacked is not, until damage lands");

            PvpArenaHookSettings.FriendlyFireSource = () => true;
            var ffAttacker = SeededPlayer(A, Bound(match, 0));

            Assert.IsTrue(ffAttacker.TryPvpArenaGate(SeededPlayer(C, Bound(match, 0)), null, out var ffResult));
            Assert.IsNull(ffResult, "fixture: friendly fire allows it");
            Assert.IsNull(ffAttacker.PvpActivity, "hitting a teammate is not activity");

            var stranger = SeededPlayer(A, Bound(match, 0));
            Assert.IsTrue(stranger.TryPvpArenaGate(SeededPlayer(B, Bound(NewMatch(), 1)), null, out var refused));
            Assert.IsNotNull(refused, "fixture: another match is refused");
            Assert.IsNull(stranger.PvpActivity, "a refusal is not activity");
        }

        [TestMethod]
        public void Gate_BeforeLive_DoesNotStamp()
        {
            var match = NewMatch("ffa");
            var attacker = SeededPlayer(A, Bound(match, 0, PvpMatchState.Countdown));

            attacker.TryPvpArenaGate(SeededPlayer(B, Bound(match, 1, PvpMatchState.Countdown)), null, out _);

            Assert.IsNull(attacker.PvpActivity);
        }

        // ================= the damage hook =================

        [TestMethod]
        public void EnemyDamage_StampsBothDealerAndTaker_TeammateDamageNeither()
        {
            var match = NewMatch();
            var attacker = SeededPlayer(A, Bound(match, 0));
            var victim = SeededPlayer(B, Bound(match, 1));

            Player.OnPvpDamageRecorded(victim, attacker, 25);

            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), attacker.PvpActivity, "dealt");
            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), victim.PvpActivity, "taken");

            var a2 = SeededPlayer(A, Bound(match, 0));
            var mate = SeededPlayer(C, Bound(match, 0));
            Player.OnPvpDamageRecorded(mate, a2, 25);

            Assert.IsNull(a2.PvpActivity, "teammate damage");
            Assert.IsNull(mate.PvpActivity, "teammate damage");
        }

        /// <summary>
        /// A hit the victim never takes (lifestone protection) stamps NOBODY, attacker included; the same hit once the protection is gone
        /// stamps both (the positive control).
        /// </summary>
        [TestMethod]
        public void EnemyDamage_OnAProtectedVictim_StampsNobody()
        {
            var match = NewMatch();
            var attacker = SeededPlayer(A, Bound(match, 0));
            var victim = SeededPlayer(B, Bound(match, 1));

            victim.UnderLifestoneProtection = true;
            Player.OnPvpDamageRecorded(victim, attacker, 25);

            Assert.IsNull(attacker.PvpActivity, "a refused write is not the attacker's activity");
            Assert.IsNull(victim.PvpActivity, "nor the protected victim's");

            victim.UnderLifestoneProtection = false;
            Player.OnPvpDamageRecorded(victim, attacker, 25);

            Assert.IsNotNull(attacker.PvpActivity, "control: the same hit on an unprotected victim stamps");
            Assert.IsNotNull(victim.PvpActivity);
        }

        /// <summary>The damage stamp does not depend on the ledger: a match with no ledger still stamps both players.</summary>
        [TestMethod]
        public void EnemyDamage_StampsWithoutALedger()
        {
            var match = NewMatch("2v2");
            match.Performance = null;
            var attacker = SeededPlayer(A, Bound(match, 0));
            var victim = SeededPlayer(B, Bound(match, 1));

            Player.OnPvpDamageRecorded(victim, attacker, 25);

            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), attacker.PvpActivity);
            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), victim.PvpActivity);
        }

        // ================= crystal hits =================

        private static Creature Crystal(PvpMatch match)
        {
            var crystal = TestCreatures.CreateDefender(maxHealth: 990);
            Assert.IsTrue(crystal.SetBattlegroundObjective(new BattlegroundObjectiveTag(match.MatchId, 0, CrystalWinCondition.DefenderTeam)), "fixture: the crystal is tagged");
            return crystal;
        }

        /// <summary>An attacker's hit on a crystal of their own Live match stamps the attacker; the kill chip (credited to the killer) does not.</summary>
        [TestMethod]
        public void CrystalHit_StampsTheAttacker_TheKillChipDoesNot()
        {
            var match = NewMatch("bg_ad");
            var attacker = SeededPlayer(A, Bound(match, CrystalWinCondition.AttackerTeam));

            Player.OnPvpDamageRecorded(Crystal(match), attacker, 40);

            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), attacker.PvpActivity, "a crystal hit is activity");

            var chipper = SeededPlayer(A, Bound(match, CrystalWinCondition.AttackerTeam));
            var chipped = Crystal(match);
            chipped.BattlegroundKillChipActive = true;

            Player.OnPvpDamageRecorded(chipped, chipper, 40);

            Assert.IsNull(chipper.PvpActivity, "the kill chip is not the killer's hit");
        }

        [TestMethod]
        public void CrystalHit_ByADefender_OrAnotherMatch_DoesNotStamp()
        {
            var match = NewMatch("bg_ad");

            var defender = SeededPlayer(B, Bound(match, CrystalWinCondition.DefenderTeam));
            Player.OnPvpDamageRecorded(Crystal(match), defender, 40);
            Assert.IsNull(defender.PvpActivity, "a defender hitting their own crystal");

            var stranger = SeededPlayer(A, Bound(NewMatch("bg_ad"), CrystalWinCondition.AttackerTeam));
            Player.OnPvpDamageRecorded(Crystal(match), stranger, 40);
            Assert.IsNull(stranger.PvpActivity, "an attacker of another match");
        }

        /// <summary>
        /// The support-cast site (no unit host can drive a real enchantment cast): HandleCastSpell's Enchantment branch calls the stamp after
        /// the enchantment is created, gated to a beneficial, non-equip cast by a player. Matched on code only.
        /// </summary>
        [TestMethod]
        public void SupportCastSite_IsWired_AfterTheEnchantment()
        {
            var root = FindSourceRoot();
            const string file = "ACE.Server/WorldObjects/WorldObject_Magic.cs";
            const string gate = "if (!equip && spell.IsBeneficial && this is Player supportCaster)";
            const string call = "Player.OnPvpSupportCast(supportCaster, targetCreature ?? target);";

            AssertOrder(root, file, "CreateEnchantment(targetCreature ?? target, this, weapon, spell, equip, isWeaponSpell: isWeaponSpell, procSourced: enchantProcSourced);", gate);
            AssertOrder(root, file, gate, call);

            var lines = File.ReadAllLines(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))).Select(CodeOnly).ToArray();
            var gateAt = Array.IndexOf(lines, gate);
            var next = lines.Skip(gateAt + 1).First(l => l.Length > 0);
            Assert.AreEqual(call, next, "the call is the gate's own statement");
        }

        // ================= helpers =================

        private static string CodeOnly(string raw)
        {
            var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
            return (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
        }

        private static void AssertOrder(string root, string file, string first, string second)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var lines = File.ReadAllLines(path).Select(CodeOnly).ToArray();

            var a = Enumerable.Range(0, lines.Length).Where(i => lines[i] == first).ToList();
            var b = Enumerable.Range(0, lines.Length).Where(i => lines[i] == second).ToList();

            Assert.AreEqual(1, a.Count, $"{file}: expected exactly one code line `{first}`");
            Assert.AreEqual(1, b.Count, $"{file}: expected exactly one code line `{second}`");
            Assert.IsTrue(a[0] < b[0], $"{file}: `{first}` (line {a[0] + 1}) must come before `{second}` (line {b[0] + 1})");
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
