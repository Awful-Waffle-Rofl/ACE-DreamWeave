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
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The melee-swing and missile-shot half of the conduct activity stamp (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): an attempt at an
    /// enemy of the same Live match counts even when it misses. The helper is driven on reflection-seeded Players (no database, no dat); the
    /// two call sites (Player.Attack, Player.LaunchMissile) cannot be driven by this host, so they are pinned by source scans bound to the
    /// method bodies.
    ///
    /// SEEDING: no PropertyManager key is read. The player-side clock (PvpArenaHookSettings.UtcNow) is swapped per test and restored in cleanup.
    /// </summary>
    [TestClass]
    public class PvpAttackAttemptStampTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        private const uint A = 0x50000021, B = 0x50000022, C = 0x50000023;

        private const uint Instance = 0x0001_0400;

        private Func<DateTime> savedClock;

        [TestInitialize]
        public void Setup()
        {
            savedClock = PvpArenaHookSettings.UtcNow;
            PvpArenaHookSettings.UtcNow = () => Now;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.UtcNow = savedClock;
        }

        private static PvpMatch NewMatch()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(A, 1500), new PvpParticipant(C, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(B, 1500) }),
            };

            return new PvpMatch(Guid.NewGuid(), "bg_koth", teams, Now);
        }

        private static PvpPlayerBinding Bound(PvpMatch match, int team, PvpMatchState state = PvpMatchState.Live)
            => new PvpPlayerBinding(match, team, state, false, false, false, false, false);

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

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

        /// <summary>
        /// Tugak Brawl spell-only (pvp_arena_ffa_spell_only): a swing or shot the gate refuses is not conduct activity. Without the
        /// IsPhysicalRefused line in StampPvpAttackAttempt the key-on half stamps and fails.
        /// </summary>
        [TestMethod]
        public void Attempt_OnAnFfaEnemy_StampsOnlyWhenTheSpellOnlyKeyIsOff()
        {
            var savedSpellOnly = PvpArenaHookSettings.FfaSpellOnlySource;

            try
            {
                var teams = new List<PvpTeam>
                {
                    new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(A, 1500) }),
                    new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(B, 1500) }),
                };
                var match = new PvpMatch(Guid.NewGuid(), ArenaMapCatalog.FfaKey, teams, Now);

                PvpArenaHookSettings.FfaSpellOnlySource = () => true;
                var refused = SeededPlayer(A, Bound(match, 0));
                refused.StampPvpAttackAttempt(SeededPlayer(B, Bound(match, 1)));
                Assert.IsNull(refused.PvpActivity, "key on: the gate refuses the swing, so it is not activity");

                PvpArenaHookSettings.FfaSpellOnlySource = () => false;
                var allowed = SeededPlayer(A, Bound(match, 0));
                allowed.StampPvpAttackAttempt(SeededPlayer(B, Bound(match, 1)));
                Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), allowed.PvpActivity, "key off: the old rule, the swing stamps");
            }
            finally
            {
                PvpArenaHookSettings.FfaSpellOnlySource = savedSpellOnly;
            }
        }

        [TestMethod]
        public void Attempt_OnAnEnemyOfTheSameLiveMatch_Stamps()
        {
            var match = NewMatch();
            var attacker = SeededPlayer(A, Bound(match, 0));
            var enemy = SeededPlayer(B, Bound(match, 1));

            attacker.StampPvpAttackAttempt(enemy);

            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), attacker.PvpActivity, "a swing or shot at an enemy");
            Assert.IsNull(enemy.PvpActivity, "being swung at is not activity");
        }

        [TestMethod]
        public void Attempt_OnTeammate_NoMatch_NonPlayer_Self_OrCountdown_DoesNotStamp()
        {
            var match = NewMatch();

            var mateAttacker = SeededPlayer(A, Bound(match, 0));
            mateAttacker.StampPvpAttackAttempt(SeededPlayer(C, Bound(match, 0)));
            Assert.IsNull(mateAttacker.PvpActivity, "a teammate");

            var otherMatch = SeededPlayer(A, Bound(match, 0));
            otherMatch.StampPvpAttackAttempt(SeededPlayer(B, Bound(NewMatch(), 1)));
            Assert.IsNull(otherMatch.PvpActivity, "a player of another match");

            var noMatchTarget = SeededPlayer(A, Bound(match, 0));
            noMatchTarget.StampPvpAttackAttempt(SeededPlayer(B, null));
            Assert.IsNull(noMatchTarget.PvpActivity, "a target bound to no match");

            var unbound = SeededPlayer(A, null);
            unbound.StampPvpAttackAttempt(SeededPlayer(B, Bound(match, 1)));
            Assert.IsNull(unbound.PvpActivity, "an attacker bound to no match");

            var monsterAttacker = SeededPlayer(A, Bound(match, 0));
            var monster = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
            monsterAttacker.StampPvpAttackAttempt(monster);
            monsterAttacker.StampPvpAttackAttempt(null);
            Assert.IsNull(monsterAttacker.PvpActivity, "a non-player target (or none)");

            var selfAttacker = SeededPlayer(A, Bound(match, 0));
            selfAttacker.StampPvpAttackAttempt(selfAttacker);
            Assert.IsNull(selfAttacker.PvpActivity, "self");

            var countdown = SeededPlayer(A, Bound(match, 0, PvpMatchState.Countdown));
            countdown.StampPvpAttackAttempt(SeededPlayer(B, Bound(match, 1, PvpMatchState.Countdown)));
            Assert.IsNull(countdown.PvpActivity, "before the match is Live");
        }

        /// <summary>
        /// Gate parity: a pen-waiting enemy (Respawning) and a spawn-protected enemy are refused by the damage gate, so a swing or shot at one is
        /// not activity either. Control: the same attacker stamps once the window has lapsed.
        /// </summary>
        [TestMethod]
        public void Attempt_OnARespawningOrSpawnProtectedEnemy_DoesNotStamp()
        {
            var match = NewMatch();

            var penAttacker = SeededPlayer(A, Bound(match, 0));
            penAttacker.StampPvpAttackAttempt(SeededPlayer(B, new PvpPlayerBinding(match, 1, PvpMatchState.Live, false, false, false, false, false, respawning: true)));
            Assert.IsNull(penAttacker.PvpActivity, "an enemy waiting in the pen");

            var protectedBinding = new PvpPlayerBinding(match, 1, PvpMatchState.Live, false, false, false, false, false, protectedUntilUtc: Now.AddSeconds(3));
            var protectedAttacker = SeededPlayer(A, Bound(match, 0));
            protectedAttacker.StampPvpAttackAttempt(SeededPlayer(B, protectedBinding));
            Assert.IsNull(protectedAttacker.PvpActivity, "a spawn-protected enemy");

            var lapsedBinding = new PvpPlayerBinding(match, 1, PvpMatchState.Live, false, false, false, false, false, protectedUntilUtc: Now.AddSeconds(-1));
            var lapsedAttacker = SeededPlayer(A, Bound(match, 0));
            lapsedAttacker.StampPvpAttackAttempt(SeededPlayer(B, lapsedBinding));
            Assert.AreEqual(new PvpActivityStamp(match.MatchId, Now), lapsedAttacker.PvpActivity, "control: a lapsed window stamps");
        }
        /// <summary>
        /// Player.Attack (one call per executed swing) calls the stamp once, at the top level of the method body (not nested in a block and
        /// not the statement of an unbraced if/else/loop), after the point of no return (Attacking = true), past every refusal return.
        /// Matched on code only, inside the method body.
        /// </summary>
        [TestMethod]
        public void MeleeSwingSite_IsWired_OnceAtTopLevelAfterThePointOfNoReturn()
        {
            var body = MethodBody("ACE.Server/WorldObjects/Player_Melee.cs", "public void Attack(WorldObject target, int attackSequence, bool subsequent = false)");

            Assert.AreEqual(1, body.Count(l => l == "StampPvpAttackAttempt(target);"), "exactly one stamp call in Attack");

            var stamp = Array.IndexOf(body, "StampPvpAttackAttempt(target);");
            var commit = Array.IndexOf(body, "Attacking = true;");
            var swing = Array.FindIndex(body, l => l.Contains("DoSwingMotion(target"));
            var lastReturn = Enumerable.Range(0, stamp).Where(i => body[i] == "return;").DefaultIfEmpty(-1).Max();

            Assert.IsTrue(swing >= 0 && commit > swing && stamp > commit, "the stamp follows the swing motion and `Attacking = true`");
            Assert.IsTrue(lastReturn < commit, "no refusal return sits between the point of no return and the stamp");

            var depths = Depths(body);
            Assert.AreEqual(depths[commit], depths[stamp], "the stamp is at the same brace depth as `Attacking = true;` (top level of the body)");
            AssertNotControlled(body, stamp, allowedGuard: null);
        }

        /// <summary>
        /// Player.LaunchMissile (one call per shot, repeats included) calls the stamp once, inside the same `actionChain.AddAction(this, () =>`
        /// block as LaunchProjectile (not the later out-of-ammo block), after LaunchProjectile, behind nothing but a null check on the result.
        /// </summary>
        [TestMethod]
        public void MissileLaunchSite_IsWired_OnceInsideTheLaunchBlock()
        {
            var body = MethodBody("ACE.Server/WorldObjects/Player_Missile.cs", "public void LaunchMissile(WorldObject target, int attackSequence, MotionStance stance, bool subsequent = false)");

            Assert.AreEqual(1, body.Count(l => l == "StampPvpAttackAttempt(target);"), "exactly one stamp call in LaunchMissile");

            var stamp = Array.IndexOf(body, "StampPvpAttackAttempt(target);");
            var launch = Array.FindIndex(body, l => l.StartsWith("var projectile = LaunchProjectile(", StringComparison.Ordinal));

            Assert.IsTrue(launch >= 0 && stamp > launch, "the stamp follows LaunchProjectile");

            var depths = Depths(body);
            var blockStart = Array.FindLastIndex(body, launch, l => l == "actionChain.AddAction(this, () =>");
            Assert.IsTrue(blockStart >= 0, "LaunchProjectile sits in an `actionChain.AddAction(this, () =>` block");

            var blockEnd = Enumerable.Range(blockStart + 1, body.Length - blockStart - 1).First(i => depths[i] == depths[blockStart] && body[i].StartsWith("}", StringComparison.Ordinal));
            Assert.IsTrue(stamp < blockEnd, "the stamp is inside the launch block, not after it");
            Assert.AreEqual(depths[launch], depths[stamp], "the stamp is at the launch statement's own brace depth");

            var outOfAmmo = Array.FindIndex(body, launch, l => l == "return;");
            Assert.IsTrue(outOfAmmo > stamp, "the stamp precedes the out-of-ammo return");

            AssertNotControlled(body, stamp, allowedGuard: "if (projectile != null)");
        }

        /// <summary>
        /// Absence: the stamp is never called from the damage gate's own path (DamageTarget already stamps there) or the spell cast paths.
        /// The definition file (Player_BattlegroundEngagement.cs) is exempt.
        /// </summary>
        [TestMethod]
        public void StampCall_IsAbsentFromDamageTargetAndSpellPaths()
        {
            var root = FindSourceRoot();

            foreach (var file in new[] { "ACE.Server/WorldObjects/Player_Combat.cs", "ACE.Server/WorldObjects/Player_Magic.cs", "ACE.Server/WorldObjects/SpellProjectile.cs" })
            {
                var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(File.Exists(path), $"missing {path}");

                Assert.IsFalse(File.ReadAllLines(path).Select(CodeOnly).Any(l => l.Contains("StampPvpAttackAttempt")), $"{file} must not call StampPvpAttackAttempt");
            }
        }
        // ================= helpers =================

        private static string CodeOnly(string raw)
        {
            var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
            return (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();
        }

        /// <summary>The comment-stripped, trimmed code lines of one method, from its signature line to its brace-matched close.</summary>
        private static string[] MethodBody(string file, string signature)
        {
            var path = Path.Combine(FindSourceRoot(), file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var lines = File.ReadAllLines(path).Select(CodeOnly).ToArray();
            var start = Array.IndexOf(lines, signature);
            Assert.AreNotEqual(-1, start, $"{file}: signature `{signature}` not found");

            var depth = 0;
            var opened = false;

            for (var i = start; i < lines.Length; i++)
            {
                foreach (var ch in lines[i])
                {
                    if (ch == '{') { depth++; opened = true; }
                    else if (ch == '}') depth--;
                }

                if (opened && depth == 0)
                    return lines.Skip(start).Take(i - start + 1).ToArray();
            }

            Assert.Fail($"{file}: no closing brace for `{signature}`");
            return null;
        }

        /// <summary>The brace depth in force at the START of each line.</summary>
        private static int[] Depths(string[] lines)
        {
            var result = new int[lines.Length];
            var depth = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                result[i] = depth;
                depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
            }

            return result;
        }

        /// <summary>Fails when the statement at index is the body of an unbraced if/else/for/while/foreach, unless allowedGuard is given, in which case that guard must be the statement's header.</summary>
        private static void AssertNotControlled(string[] body, int index, string allowedGuard)
        {
            var prev = Enumerable.Range(0, index).Reverse().Select(i => body[i]).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            var isControl = new[] { "if (", "else", "for (", "foreach (", "while (" }.Any(k => prev.StartsWith(k, StringComparison.Ordinal));

            if (allowedGuard != null)
            {
                Assert.AreEqual(allowedGuard, prev, "the stamp must be guarded by exactly that statement");
                return;
            }

            Assert.IsFalse(isControl, $"the stamp must not be the unbraced body of `{prev}`");
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
