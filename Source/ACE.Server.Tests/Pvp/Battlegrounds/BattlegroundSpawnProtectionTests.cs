using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rules;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Battleground spawn protection (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"): the pure gate and DoT rules, the text, the dial,
    /// and the player side driven through the real Player.CheckPKStatusVsTarget on seeded Players (the PvpPlayerHookTests seeding).
    /// The coordinator half (window starts at confirm, expiry, dial 0, snapshot) lives in BattlegroundCoordinatorTests, where the rig is.
    /// </summary>
    [TestClass]
    public class BattlegroundSpawnProtectionTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        private Func<bool> savedFriendlyFire;
        private Func<DateTime> savedClock;
        private DateTime clockNow;

        [TestInitialize]
        public void Setup()
        {
            savedFriendlyFire = PvpArenaHookSettings.FriendlyFireSource;
            savedClock = PvpArenaHookSettings.UtcNow;
            PvpArenaHookSettings.FriendlyFireSource = () => false;
            clockNow = T0;
            PvpArenaHookSettings.UtcNow = () => clockNow;
            PvpMatchManager.ClearForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.FriendlyFireSource = savedFriendlyFire;
            PvpArenaHookSettings.UtcNow = savedClock;
            PvpMatchManager.ClearForTests();
        }

        private static PvpMatch NewMatch(string mode = "bg_koth") => new PvpMatch(Guid.NewGuid(), mode, new List<PvpTeam>
        {
            new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500), new PvpParticipant(2, 1500) }),
            new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500), new PvpParticipant(4, 1500) })
        }, T0);

        private static PvpPlayerBinding Bind(PvpMatch match, int team, DateTime? protectedUntil = null, bool respawning = false) =>
            new PvpPlayerBinding(match, team, PvpMatchState.Live, true, true, true, true, true, respawning: respawning, protectedUntilUtc: protectedUntil);

        // ================= the pure gate =================

        [TestMethod]
        public void Gate_ProtectedDefender_IsRefusedWithItsOwnDecision_UntilTheWindowEnds()
        {
            var m = NewMatch();
            var until = T0.AddSeconds(3);

            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 1), false, nowUtc: T0), "control: no window, opponents fight");
            Assert.AreEqual(PvpGateDecision.RefuseProtected, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 1, until), false, nowUtc: T0));
            Assert.AreEqual(PvpGateDecision.RefuseProtected, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 1, until), false, nowUtc: T0.AddSeconds(2.999)));
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 1, until), false, nowUtc: until), "the window is over at its end instant");
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 1, until), false, nowUtc: T0.AddSeconds(3.001)));
        }

        [TestMethod]
        public void Gate_ProtectionAlsoRefusesAFriendlyFireHit_AndNeverTheProtectedPlayersOwnAttack()
        {
            var m = NewMatch();
            var until = T0.AddSeconds(3);

            Assert.AreEqual(PvpGateDecision.RefuseProtected, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 0, until), true, nowUtc: T0), "friendly fire on, still protected");
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0, until), Bind(m, 1), false, nowUtc: T0), "the protected player may attack (and that ends it); the gate does not block the attacker side");
        }

        [TestMethod]
        public void Gate_WithoutAClock_AppliesNoWindow()
        {
            var m = NewMatch();

            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 1, T0.AddSeconds(3)), false), "an older call site with no clock behaves as before");
        }

        [TestMethod]
        public void IsSpawnProtected_NeedsAnotherPlayerOfTheSameMatch()
        {
            var m = NewMatch();
            var self = Bind(m, 1, T0.AddSeconds(3));

            Assert.IsFalse(PvpArenaGate.IsSpawnProtected(self, self, T0), "one binding on both sides is a self-cast");
            Assert.IsFalse(PvpArenaGate.IsSpawnProtected(Bind(NewMatch(), 0), Bind(m, 1, T0.AddSeconds(3)), T0), "another match");
            Assert.IsFalse(PvpArenaGate.IsSpawnProtected(null, self, T0));
            Assert.IsTrue(PvpArenaGate.IsSpawnProtected(Bind(m, 0), self, T0));
        }

        // ================= DoT ticks =================

        [TestMethod]
        public void Dot_TickInsideTheWindow_IsSuppressed_AfterItAndForSelfCastsItIsNot()
        {
            var m = NewMatch();
            var until = T0.AddSeconds(3);
            var target = Bind(m, 1, until);

            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(Bind(m, 1), Bind(m, 0), T0), "control: an unprotected Live target ticks");
            Assert.IsTrue(PvpArenaSpellRules.ShouldSuppressDotTick(target, Bind(m, 0), T0.AddSeconds(1)), "a tick landing inside the window");
            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(target, Bind(m, 0), T0.AddSeconds(3.001)), "after the window it ticks");
            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(target, target, T0.AddSeconds(1)), "a self-cast tick is not another player's");
            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(target, Bind(m, 0)), "no clock, no window");
        }

        /// <summary>
        /// The DoT hook point IS available (EnchantmentManager's tick loop, beside the existing arena suppression), but a real tick needs a
        /// live landblock and enchantment registry that no unit test builds, so the rule is pinned above and the call site is pinned here on
        /// source: it must hand the rule the clock whenever the target carries a window.
        /// </summary>
        [TestMethod]
        public void DotTickHook_PassesTheClock_WhenTheTargetHasAWindow()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs");

            var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            // Bound on identifiers, not layout: the rule is called with the two bindings AND a third (clock) argument that is gated on the
            // window still RUNNING (IsSpawnProtected), and the hook's guard admits the protected target through that same test.
            var call = System.Text.RegularExpressions.Regex.Match(flat, @"PvpArenaSpellRules\.ShouldSuppressDotTick\(\s*targetPvpBinding\s*,\s*damagerPvpBinding\s*,\s*(?<clock>[^;]+?)\)\)");
            Assert.IsTrue(call.Success, "the DoT hook calls ShouldSuppressDotTick with a third argument");
            StringAssert.Contains(call.Groups["clock"].Value, "targetSpawnProtected", "the clock is handed over only while the window is running");

            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(flat, @"targetSpawnProtected\s*=[^;]*\.IsSpawnProtected\("), "the guard is the running-window test, not mere presence of a window");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(flat, @"Respawning\s*\|\|[^)]*ProtectedUntilUtc\.HasValue"), "a lapsed window must not widen the guard");
        }

        // ================= the binding =================

        [TestMethod]
        public void WithoutSpawnProtection_ClearsOnlyTheWindow()
        {
            var m = NewMatch();
            var pen = new Position(0x016C0134, 0f, -60f, 0.005f, 0f, 0f, 0f, 1f, 0x80010101);
            var b = new PvpPlayerBinding(m, 1, PvpMatchState.Live, true, true, true, true, true, false, respawnPen: pen, teamFellowship: true, untemplated: true, protectedUntilUtc: T0.AddSeconds(3));

            var cleared = b.WithoutSpawnProtection();

            Assert.IsNull(cleared.ProtectedUntilUtc);
            Assert.IsFalse(cleared.IsSpawnProtected(T0));
            Assert.AreSame(m, cleared.Match);
            Assert.AreEqual(1, cleared.TeamIndex);
            Assert.AreSame(pen, cleared.RespawnPen);
            Assert.IsTrue(cleared.TeamFellowship);
            Assert.IsTrue(cleared.Untemplated);
            Assert.IsFalse(cleared.RestrictSpells);
            Assert.IsTrue(b.IsSpawnProtected(T0), "the original is untouched");
        }

        // ================= text, dial, intent =================

        [TestMethod]
        public void Text_IsTheOwnerWording()
        {
            Assert.AreEqual("You are protected for 3 seconds. Attacking ends it early.", BattlegroundText.SpawnProtectedBody(3));
            Assert.AreEqual("[Battleground] You are protected for 5 seconds. Attacking ends it early.", BattlegroundText.SpawnProtected(5));
            Assert.AreEqual("You are protected for 1 second. Attacking ends it early.", BattlegroundText.SpawnProtectedBody(1));
            Assert.AreEqual("[Battleground] Your spawn protection has ended.", BattlegroundText.SpawnProtectionEnded);
            Assert.AreEqual("[Battleground] Bravo is protected for a moment longer.", BattlegroundText.SpawnProtectedTarget("Bravo"));
        }

        [TestMethod]
        public void Dial_DefaultsToThree_AndANegativeValueClampsToZero()
        {
            Assert.AreEqual(3, BattlegroundTunables.Defaults.SpawnProtectionSeconds);
            Assert.AreEqual(0, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { SpawnProtectionSeconds = -5 }).SpawnProtectionSeconds);
            Assert.AreEqual(0, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { SpawnProtectionSeconds = 0 }).SpawnProtectionSeconds, "0 stays 0: it disables");
            Assert.AreEqual(8, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { SpawnProtectionSeconds = 8 }).SpawnProtectionSeconds);
            Assert.AreEqual(30, BattlegroundTunables.MaxSpawnProtectionSeconds);
            Assert.AreEqual(30, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { SpawnProtectionSeconds = 31 }).SpawnProtectionSeconds, "capped at 30");
            Assert.AreEqual(30, BattlegroundTunables.Clamp(BattlegroundTunables.Defaults with { SpawnProtectionSeconds = int.MaxValue }).SpawnProtectionSeconds);
            Assert.AreEqual(30, BattlegroundTunables.ClampSpawnProtectionSeconds(long.MaxValue), "a huge setting lands on the cap");
            Assert.AreEqual(30, BattlegroundTunables.ClampSpawnProtectionSeconds(4294967299L), "2^32 + 3 would wrap to 3 through an int cast");
            Assert.AreEqual(0, BattlegroundTunables.ClampSpawnProtectionSeconds(long.MinValue));
            Assert.AreEqual(3, BattlegroundTunables.ClampSpawnProtectionSeconds(3));
        }

        [TestMethod]
        public void Intent_Shape()
        {
            var id = Guid.NewGuid();
            var intent = PvpMatchManager.SpawnProtectionEnded(7, id, T0);

            Assert.AreEqual(PvpIntentKind.SpawnProtectionEnded, intent.Kind);
            Assert.AreEqual(7u, intent.CharacterId);
            Assert.AreEqual(id, intent.MatchId);
            Assert.AreEqual(T0, intent.OccurredAtUtc);
        }

        // ================= on seeded Players =================

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static T Seeded<T>(PlayerKillerStatus status) where T : Creature
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(wo, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(wo, "ephemeralPositions", new Dictionary<PositionType, Position>());

            wo.PlayerKillerStatus = status;
            wo.Location = new Position { LandblockId = new LandblockId(0x00900100), PositionX = 50f, PositionY = 50f, RotationW = 1f };

            return wo;
        }

        private static Spell FakeSpell(bool beneficial)
        {
            var spellBase = (ACE.DatLoader.Entity.SpellBase)RuntimeHelpers.GetUninitializedObject(typeof(ACE.DatLoader.Entity.SpellBase));
            var bitfield = typeof(ACE.DatLoader.Entity.SpellBase).GetField("<Bitfield>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(bitfield, "SpellBase.Bitfield's backing field was not found by reflection - has it been renamed?");
            bitfield.SetValue(spellBase, beneficial ? (uint)SpellFlags.Beneficial : 0u);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;
            return spell;
        }

        /// <summary>An attacker on team 0 and a defender on team 1 of one Live match, the defender protected for 3 s from T0.</summary>
        private static (Player Attacker, Player Defender, PvpMatch Match) Pair()
        {
            var m = NewMatch();
            var a = Seeded<Player>(PlayerKillerStatus.PK);
            var d = Seeded<Player>(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(m, 0));
            d.SetPvpBindingForTests(Bind(m, 1, T0.AddSeconds(3)));

            return (a, d, m);
        }

        [TestMethod]
        public void SeededPlayer_MeleeAndMissile_AreRefusedWhileProtected()
        {
            var (a, d, _) = Pair();

            // Melee and missile both ask the gate with no spell (Player.DamageTarget), so one call shape covers the pair.
            var result = a.CheckPKStatusVsTarget(d, null);

            Assert.IsNotNull(result, "a weapon hit on a protected player is refused");
            Assert.IsTrue(PvpArenaHookSettings.IsSpawnProtectedRefusal(result), "with the dedicated refusal, which the call sites send nothing for");
            Assert.AreNotEqual(WeenieErrorWithString.YouFailToAffect_NotSamePKType, result[0], "never the misleading not-the-same-PK-type line");
            Assert.AreNotEqual(WeenieErrorWithString._FailsToAffectYou_NotSamePKType, result[1]);
        }

        [TestMethod]
        public void SeededPlayer_HarmfulSpell_IsRefusedWhileProtected_ABeneficialOneIsNot()
        {
            var (a, d, m) = Pair();
            var mate = Seeded<Player>(PlayerKillerStatus.PK);
            mate.SetPvpBindingForTests(Bind(m, 1, T0.AddSeconds(3)));

            Assert.IsNotNull(a.CheckPKStatusVsTarget(d, FakeSpell(beneficial: false)), "a harmful spell is refused");
            Assert.IsNull(a.CheckPKStatusVsTarget(mate, FakeSpell(beneficial: true)), "control: a beneficial spell is not a harmful action, the gate leaves it alone");
        }

        [TestMethod]
        public void SeededPlayer_ProjectileImpactInsideTheWindow_IsRefused_AfterItLands()
        {
            var (a, d, _) = Pair();
            var bolt = FakeSpell(beneficial: false);

            // SpellProjectile asks ProjectileSource.CheckPKStatusVsTarget(target, Spell) at IMPACT: same call, later clock.
            clockNow = T0.AddSeconds(2.5);
            Assert.IsNotNull(a.CheckPKStatusVsTarget(d, bolt), "a bolt cast before the respawn that lands at 2.5 s is refused");

            clockNow = T0.AddSeconds(3.5);
            Assert.IsNull(a.CheckPKStatusVsTarget(d, bolt), "the same bolt after the window lands");
        }

        [TestMethod]
        public void SeededPlayer_AnAttackByTheProtectedPlayer_EndsTheirProtection_AndReportsIt()
        {
            var (a, d, m) = Pair();

            Assert.IsNotNull(a.CheckPKStatusVsTarget(d, null), "fixture: protected");

            Assert.IsNull(d.CheckPKStatusVsTarget(a, null), "the protected player's own hit goes through the gate");
            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc, "and ends the window at once");
            Assert.IsNull(a.CheckPKStatusVsTarget(d, null), "so the next hit on them lands");

            Assert.IsTrue(PvpMatchManager.TryDequeue(out var intent), "the coordinator is told");
            Assert.AreEqual(PvpIntentKind.SpawnProtectionEnded, intent.Kind);
            Assert.AreEqual(m.MatchId, intent.MatchId);
            Assert.IsFalse(PvpMatchManager.TryDequeue(out _), "once");
        }

        [TestMethod]
        public void SeededPlayer_AHarmfulSpellOnAMonster_AlsoEndsIt_ABeneficialSpellOnAnAllyDoesNot()
        {
            var (_, d, m) = Pair();
            var ally = Seeded<Player>(PlayerKillerStatus.PK);
            ally.SetPvpBindingForTests(Bind(m, 1));
            var monster = Seeded<Creature>(PlayerKillerStatus.NPK);

            d.CheckPKStatusVsTarget(ally, FakeSpell(beneficial: true));
            d.CheckPKStatusVsTarget(d, FakeSpell(beneficial: true));
            Assert.IsNotNull(d.PvpBinding.ProtectedUntilUtc, "beneficial ally and self spells leave the window alone");
            Assert.IsFalse(PvpMatchManager.TryDequeue(out _));

            d.CheckPKStatusVsTarget(monster, FakeSpell(beneficial: false));
            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc, "a harmful cast on anyone, a monster included, ends it");
        }

        [TestMethod]
        public void SeededPlayer_ALapsedWindow_EndsSilently_NoReport()
        {
            var (a, d, _) = Pair();

            clockNow = T0.AddSeconds(10);
            d.CheckPKStatusVsTarget(a, null);

            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc, "cleared");
            Assert.IsFalse(PvpMatchManager.TryDequeue(out _), "a natural expiry is not reported or announced");
        }

        [TestMethod]
        public void SeededPlayer_AnArenaPlayer_IsNeverProtected()
        {
            var m = NewMatch("1v1");
            var a = Seeded<Player>(PlayerKillerStatus.PK);
            var d = Seeded<Player>(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(m, 0));
            d.SetPvpBindingForTests(Bind(m, 1));

            Assert.IsNull(a.CheckPKStatusVsTarget(d, null), "an arena binding carries no window, so the gate has nothing to refuse");
            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc);
        }

        // ================= review fixes =================

        /// <summary>F1: a harmful cast on oneself used to hit the early return above the hook, so it never ended the window.</summary>
        [TestMethod]
        public void SeededPlayer_AHarmfulSelfCast_EndsIt_ABeneficialSelfCastDoesNot()
        {
            var (_, d, _) = Pair();

            Assert.IsNull(d.CheckPKStatusVsTarget(d, FakeSpell(beneficial: true)));
            Assert.IsNotNull(d.PvpBinding.ProtectedUntilUtc, "a beneficial self-spell reaches the hook and leaves the window alone");

            Assert.IsNull(d.CheckPKStatusVsTarget(d, FakeSpell(beneficial: false)));
            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc, "a harmful cast with target == this ends it all the same");
            Assert.IsTrue(PvpMatchManager.TryDequeue(out var intent));
            Assert.AreEqual(PvpIntentKind.SpawnProtectionEnded, intent.Kind);
        }

        [TestMethod]
        public void SeededPlayer_AHarmfulCastWithNoTarget_EndsIt()
        {
            var (_, d, _) = Pair();

            d.CheckPKStatusVsTarget(null, FakeSpell(beneficial: false));

            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc);
        }

        /// <summary>
        /// F2: an early end on the player's thread must survive a coordinator republish rebuilt from the seat, which still carries the
        /// window until the intent drains. The player strips the window it already ended; a later, fresh window is kept.
        /// </summary>
        [TestMethod]
        public void SeededPlayer_ARepublishCarryingTheEndedWindow_DoesNotBringItBack()
        {
            var (a, d, m) = Pair();
            var ended = d.PvpBinding.ProtectedUntilUtc.Value;

            d.CheckPKStatusVsTarget(a, null);
            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc, "fixture: ended early");

            Assert.IsTrue(d.ReplacePvpBinding(Bind(m, 1, ended)), "the republish is accepted ...");
            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc, "... but carries no window: it was already ended");
            Assert.IsNull(a.CheckPKStatusVsTarget(d, null), "so a hit on them lands");

            Assert.IsTrue(d.ReplacePvpBinding(Bind(m, 1, T0.AddSeconds(20))));
            Assert.AreEqual(T0.AddSeconds(20), d.PvpBinding.ProtectedUntilUtc, "a fresh window from the next respawn is NOT stripped");
        }

        [TestMethod]
        public void SeededPlayer_ReplacePvpBinding_StillRefusesAnotherMatchOrNoBinding()
        {
            var (_, d, _) = Pair();

            Assert.IsFalse(d.ReplacePvpBinding(Bind(NewMatch(), 1)), "another match");
            Assert.IsFalse(d.ReplacePvpBinding(null));
            Assert.IsFalse(Seeded<Player>(PlayerKillerStatus.PK).ReplacePvpBinding(Bind(NewMatch(), 1)), "not in a match");
        }

        /// <summary>F3: the refusal line is throttled per target, not globally, and the map stays bounded.</summary>
        [TestMethod]
        public void Notice_IsThrottledPerTarget()
        {
            var a = Seeded<Player>(PlayerKillerStatus.PK);

            Assert.IsTrue(a.ShouldSendPvpSpawnProtectedNotice(101, T0), "first line for target 101");
            Assert.IsFalse(a.ShouldSendPvpSpawnProtectedNotice(101, T0.AddSeconds(1)), "101 again inside 3 s");
            Assert.IsFalse(a.ShouldSendPvpSpawnProtectedNotice(101, T0.AddSeconds(2.9)));
            Assert.IsTrue(a.ShouldSendPvpSpawnProtectedNotice(202, T0.AddSeconds(1)), "a different target is not held back by 101");
            Assert.IsFalse(a.ShouldSendPvpSpawnProtectedNotice(202, T0.AddSeconds(2)));
            Assert.IsTrue(a.ShouldSendPvpSpawnProtectedNotice(101, T0.AddSeconds(3)), "101 speaks again after 3 s");
        }

        [TestMethod]
        public void Notice_Throttle_StaysBounded_AndStillThrottlesTheLatest()
        {
            var a = Seeded<Player>(PlayerKillerStatus.PK);

            for (uint id = 1; id <= 40; id++)
                Assert.IsTrue(a.ShouldSendPvpSpawnProtectedNotice(id, T0), $"first line for {id}");

            Assert.IsFalse(a.ShouldSendPvpSpawnProtectedNotice(40, T0.AddSeconds(1)), "the most recent target is still throttled after eviction churn");
            Assert.IsTrue(a.ShouldSendPvpSpawnProtectedNotice(40, T0.AddSeconds(4)));
        }

        /// <summary>F3: every call site that reports a PK error recognises the dedicated refusal, so none of them sends the PK-type line.</summary>
        [TestMethod]
        public void PkErrorSendSites_RecogniseTheDedicatedRefusal()
        {
            foreach (var file in new[] { "Player_Combat.cs", "Player_Magic.cs", "SpellProjectile.cs" })
            {
                var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/" + file);

                StringAssert.Contains(text, "IsSpawnProtectedRefusal(", file);
            }
        }

        // ================= spawn-room protection =================

        private const uint RoomCellA = 0x003C01D1u, RoomCellB = 0x003C01D7u, OutsideCell = 0x003C01DDu;

        /// <summary>The defenders' room as the Mines declare it: two cells, x 55..75, y -85..-75, feet between z -7 and 0.</summary>
        private static readonly SpawnRoomArea Room = new SpawnRoomArea(
            new HashSet<uint> { RoomCellA, RoomCellB }, new[] { new SpawnRoomRect(55f, 75f, -85f, -75f) }, -7f, 0f);

        private static Position Pos(uint cell, float x, float y, float z) =>
            new Position { LandblockId = new LandblockId(cell), PositionX = x, PositionY = y, PositionZ = z, RotationW = 1f };

        private static readonly Position InRoomA = Pos(RoomCellA, 60f, -80f, -6f);
        private static readonly Position InRoomB = Pos(RoomCellB, 70f, -80f, -6f);
        private static readonly Position Outside = Pos(OutsideCell, 80f, -80f, -6f);

        private static PvpPlayerBinding RoomBind(PvpMatch match, int team, long stamp = 77, int seconds = 3, SpawnRoomArea room = null) =>
            new PvpPlayerBinding(match, team, PvpMatchState.Live, true, true, true, true, true, spawnRoom: room ?? Room, protectedInRoom: true, protectionSeconds: seconds, protectionStamp: stamp);

        private static void MoveTo(Player p, Position at) => p.Location = new Position(at);

        /// <summary>A room-protected defender standing in the room, and an attacker.</summary>
        private static (Player Attacker, Player Defender, PvpMatch Match) RoomPair()
        {
            var m = NewMatch("bg_ad");
            var a = Seeded<Player>(PlayerKillerStatus.PK);
            var d = Seeded<Player>(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(m, 0));
            d.SetPvpBindingForTests(RoomBind(m, 1));
            MoveTo(d, InRoomA);

            return (a, d, m);
        }

        [TestMethod]
        public void Room_Gate_ProtectsWhileInTheRoom_HoweverLate_AndNotOutsideIt()
        {
            var m = NewMatch("bg_ad");
            var target = RoomBind(m, 1);

            Assert.AreEqual(PvpGateDecision.RefuseProtected, PvpArenaGate.Evaluate(Bind(m, 0), target, false, nowUtc: T0, defenderAt: InRoomA));
            Assert.AreEqual(PvpGateDecision.RefuseProtected, PvpArenaGate.Evaluate(Bind(m, 0), target, false, nowUtc: T0.AddHours(3), defenderAt: InRoomB), "no clock expiry in the room");
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), target, false, nowUtc: T0, defenderAt: Outside), "read from outside before the swap: not protected");
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), target, false, nowUtc: T0), "no cell given: not in the room");
        }

        [TestMethod]
        public void Room_Dot_IsSuppressedInTheRoom_AndNotOutside()
        {
            var m = NewMatch("bg_ad");
            var target = RoomBind(m, 1);

            Assert.IsTrue(PvpArenaSpellRules.ShouldSuppressDotTick(target, Bind(m, 0), T0.AddMinutes(5), InRoomA));
            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(target, Bind(m, 0), T0.AddMinutes(5), Outside));
        }

        /// <summary>The refusal applies through the real player gate while in the room, with the dedicated refusal and however much time has passed.</summary>
        [TestMethod]
        public void Room_SeededPlayer_IsRefusedWhileInTheRoom_AtAnyTime()
        {
            var (a, d, _) = RoomPair();

            clockNow = T0.AddMinutes(10);

            var result = a.CheckPKStatusVsTarget(d, null);
            Assert.IsNotNull(result, "refused in the room with no clock expiry");
            Assert.IsTrue(PvpArenaHookSettings.IsSpawnProtectedRefusal(result));
        }

        [TestMethod]
        public void Room_LeavingStartsTheWindow_FromTheMomentTheyLeft()
        {
            var (a, d, _) = RoomPair();

            clockNow = T0.AddSeconds(100);
            d.CheckPvpSpawnRoomExit();
            Assert.IsTrue(d.PvpBinding.ProtectedInRoom, "still in the room: nothing changes");
            Assert.IsNull(d.PvpBinding.ProtectedUntilUtc);

            MoveTo(d, Outside);
            d.CheckPvpSpawnRoomExit();

            Assert.IsFalse(d.PvpBinding.ProtectedInRoom);
            Assert.AreEqual(T0.AddSeconds(103), d.PvpBinding.ProtectedUntilUtc, "N seconds counted from the moment they left");

            clockNow = T0.AddSeconds(102.9);
            Assert.IsNotNull(a.CheckPKStatusVsTarget(d, null), "still protected just inside the window");
            clockNow = T0.AddSeconds(103.1);
            Assert.IsNull(a.CheckPKStatusVsTarget(d, null), "and not after it");
        }

        [TestMethod]
        public void Room_ReEnteringDoesNotRestoreIt()
        {
            var (a, d, _) = RoomPair();

            MoveTo(d, Outside);
            d.CheckPvpSpawnRoomExit();

            clockNow = T0.AddSeconds(10);
            MoveTo(d, InRoomA);
            d.CheckPvpSpawnRoomExit();

            Assert.IsFalse(d.PvpBinding.ProtectedInRoom, "room mode is gone for good");
            Assert.IsNull(a.CheckPKStatusVsTarget(d, null), "back in the room after the window: a hit lands");
        }

        [TestMethod]
        public void Room_AttackingInTheRoom_EndsItAtOnce_AndReportsIt()
        {
            var (a, d, m) = RoomPair();

            Assert.IsNotNull(a.CheckPKStatusVsTarget(d, null), "fixture: protected");
            Assert.IsNull(d.CheckPKStatusVsTarget(a, null), "their own hit goes through");

            Assert.IsFalse(d.PvpBinding.HasSpawnProtection, "ended while still in the room");
            Assert.IsNull(a.CheckPKStatusVsTarget(d, null), "so the next hit on them lands");
            Assert.IsTrue(PvpMatchManager.TryDequeue(out var intent));
            Assert.AreEqual(PvpIntentKind.SpawnProtectionEnded, intent.Kind);
            Assert.AreEqual(m.MatchId, intent.MatchId);
        }

        /// <summary>
        /// A republish rebuilt from the seat is still in room mode (the seat only learns of the exit or the early end later). The player
        /// converts it to the exit window, or strips it when ended, and never restores room mode.
        /// </summary>
        [TestMethod]
        public void Room_Republish_CannotBringBackAnExitedOrEndedGrant_ButAFreshGrantIsKept()
        {
            var (a, d, m) = RoomPair();

            MoveTo(d, Outside);
            d.CheckPvpSpawnRoomExit();
            var until = d.PvpBinding.ProtectedUntilUtc.Value;

            MoveTo(d, InRoomA);
            Assert.IsTrue(d.ReplacePvpBinding(RoomBind(m, 1)), "the stale republish (same stamp, room mode) is accepted ...");
            Assert.IsFalse(d.PvpBinding.ProtectedInRoom, "... but does not restore room mode");
            Assert.AreEqual(until, d.PvpBinding.ProtectedUntilUtc, "it carries the exit window");

            Assert.IsTrue(d.ReplacePvpBinding(RoomBind(m, 1, stamp: 78)));
            Assert.IsTrue(d.PvpBinding.ProtectedInRoom, "a fresh grant (new stamp) is kept");

            d.CheckPKStatusVsTarget(a, null);
            Assert.IsFalse(d.PvpBinding.HasSpawnProtection, "fixture: ended early");
            Assert.IsTrue(d.ReplacePvpBinding(RoomBind(m, 1, stamp: 78)));
            Assert.IsFalse(d.PvpBinding.HasSpawnProtection, "a stale republish of the ended grant is stripped");
        }

        [TestMethod]
        public void Room_NoRoomCells_IsThePlainWindowExactly()
        {
            var m = NewMatch("bg_koth");
            var plain = new PvpPlayerBinding(m, 1, PvpMatchState.Live, true, true, true, true, true, protectedUntilUtc: T0.AddSeconds(3), protectionSeconds: 3);

            Assert.IsFalse(plain.ProtectedInRoom);
            Assert.IsTrue(plain.IsSpawnProtected(T0.AddSeconds(2.9), Outside), "the cell is irrelevant to a window");
            Assert.IsFalse(plain.IsSpawnProtected(T0.AddSeconds(3.1), InRoomA));

            var a = Seeded<Player>(PlayerKillerStatus.PK);
            var d = Seeded<Player>(PlayerKillerStatus.PK);
            a.SetPvpBindingForTests(Bind(m, 0));
            d.SetPvpBindingForTests(plain);
            d.CheckPvpSpawnRoomExit();

            Assert.AreSame(plain, d.PvpBinding, "the exit check never touches a window binding");
        }

        // ---- the position is client-reported: the cell alone is never trusted ----

        /// <summary>A reported cell that is in the room with coordinates outside the footprint (or far above it) is not in the room.</summary>
        [TestMethod]
        public void Room_ACellInTheRoomWithCoordinatesOutsideTheFootprint_IsNotInTheRoom()
        {
            var lyingXy = Pos(RoomCellA, 500f, 500f, -6f);
            var lyingZ = Pos(RoomCellA, 60f, -80f, 30f);

            Assert.IsTrue(Room.Contains(InRoomA) && Room.Contains(InRoomB), "control: honest positions are in");
            Assert.IsFalse(Room.Contains(lyingXy), "cell right, x/y outside");
            Assert.IsFalse(Room.Contains(lyingZ), "cell and x/y right, z outside the band");
            Assert.IsFalse(Room.Contains(Pos(OutsideCell, 60f, -80f, -6f)), "coordinates right, cell wrong");
            Assert.IsFalse(Room.Contains(null));
            Assert.IsFalse(Room.Contains(Pos(RoomCellA, 60f, -80f, float.NaN)), "a NaN z must not slip past the band");

            var m = NewMatch("bg_ad");
            var target = RoomBind(m, 1);

            Assert.AreEqual(PvpGateDecision.RefuseProtected, PvpArenaGate.Evaluate(Bind(m, 0), target, false, nowUtc: T0, defenderAt: InRoomA), "control");
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), target, false, nowUtc: T0, defenderAt: lyingXy), "the gate");
            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(target, Bind(m, 0), T0, lyingXy), "the DoT hook");
            Assert.IsFalse(target.IsSpawnProtected(T0, lyingZ));
        }

        [TestMethod]
        public void Room_SeededPlayer_WithAnInRoomCellButOutsideCoordinates_IsUnprotected_AndTheExitStartsTheWindow()
        {
            var (a, d, _) = RoomPair();

            Assert.IsNotNull(a.CheckPKStatusVsTarget(d, null), "control: honestly in the room");

            MoveTo(d, Pos(RoomCellA, 500f, 500f, -6f));

            Assert.IsNull(a.CheckPKStatusVsTarget(d, null), "the gate refuses nothing for a client that lies about its cell");

            d.CheckPvpSpawnRoomExit();
            Assert.IsFalse(d.PvpBinding.ProtectedInRoom, "the exit check treats it as outside, the same function");
            Assert.AreEqual(T0.AddSeconds(3), d.PvpBinding.ProtectedUntilUtc);
        }

        /// <summary>
        /// An early end must never be overwritten by a racing exit, or a stale republish converts the ended grant into a window. Hammers
        /// republish, exit and end against each other from the same room-mode start; the end must always win.
        /// </summary>
        [TestMethod]
        public void Room_RepublishExitAndEndRacing_NeverLeaveAnEndedGrantProtected()
        {
            for (var round = 0; round < 300; round++)
            {
                var m = NewMatch("bg_ad");
                var d = Seeded<Player>(PlayerKillerStatus.PK);

                d.SetPvpBindingForTests(RoomBind(m, 1));
                MoveTo(d, Outside); // already out: the exit check has work to do from the first call

                using var start = new Barrier(3);
                var stop = false;

                var republish = new Thread(() =>
                {
                    start.SignalAndWait();

                    while (!Volatile.Read(ref stop))
                        d.ReplacePvpBinding(RoomBind(m, 1));
                });

                var exit = new Thread(() =>
                {
                    start.SignalAndWait();

                    while (!Volatile.Read(ref stop))
                        d.CheckPvpSpawnRoomExit();
                });

                var end = new Thread(() =>
                {
                    start.SignalAndWait();

                    for (var i = 0; i < 50; i++)
                        d.EndPvpSpawnProtection();

                    Volatile.Write(ref stop, true);
                });

                republish.Start();
                exit.Start();
                end.Start();
                republish.Join();
                exit.Join();
                end.Join();

                // One more republish after everything stopped: the player must still refuse to bring an ended grant back.
                d.ReplacePvpBinding(RoomBind(m, 1));

                Assert.IsFalse(d.PvpBinding.HasSpawnProtection, $"round {round}: an ended grant came back (room {d.PvpBinding.ProtectedInRoom}, until {d.PvpBinding.ProtectedUntilUtc})");
            }
        }

        /// <summary>
        /// Deterministic interleaving A, no threads: the clock seam runs the early end in the middle of the exit check, after the exit has
        /// read the room-mode binding. The end must win: no protection after the exit check, and none after a stale republish.
        /// </summary>
        [TestMethod]
        public void Room_AnEndLandingInsideTheExitCheck_Wins_AndAStaleRepublishCannotRevive()
        {
            var (_, d, m) = RoomPair();
            MoveTo(d, Outside);

            var fired = false;
            PvpArenaHookSettings.UtcNow = () =>
            {
                if (!fired)
                {
                    fired = true;
                    d.EndPvpSpawnProtection();
                }

                return T0;
            };

            d.CheckPvpSpawnRoomExit();

            Assert.IsTrue(fired, "fixture: the end ran inside the exit check");
            Assert.IsFalse(d.PvpBinding.HasSpawnProtection, "the exit check did not revive an ended grant");

            Assert.IsTrue(d.ReplacePvpBinding(RoomBind(m, 1)));
            Assert.IsFalse(d.PvpBinding.HasSpawnProtection, "and neither did a stale republish");
        }

        /// <summary>
        /// Deterministic interleaving B: a republish lands between the exit's progress write and its binding swap, so the swap loses and
        /// retries on a clock that has moved a second on. The retry must reuse the recorded exit instant, not restart the window.
        /// </summary>
        [TestMethod]
        public void Room_ARetriedExit_KeepsTheFirstExitInstant()
        {
            var (_, d, m) = RoomPair();
            MoveTo(d, Outside);

            var calls = 0;
            PvpArenaHookSettings.UtcNow = () =>
            {
                var now = T0.AddSeconds(calls);

                if (calls++ == 0)
                    d.ReplacePvpBinding(RoomBind(m, 1)); // a fresh binding object: the exit's swap loses and retries

                return now;
            };

            d.CheckPvpSpawnRoomExit();

            Assert.IsTrue(calls >= 1);
            Assert.AreEqual(T0.AddSeconds(3), d.PvpBinding.ProtectedUntilUtc, "the window started at the first exit instant");
        }

        /// <summary>The exit conversion survives a stream of stale republishes (they are in room mode, same stamp) and re-entering.</summary>
        [TestMethod]
        public void Room_RepublishesRacingTheExit_NeverRestoreRoomMode()
        {
            for (var round = 0; round < 200; round++)
            {
                var m = NewMatch("bg_ad");
                var d = Seeded<Player>(PlayerKillerStatus.PK);

                d.SetPvpBindingForTests(RoomBind(m, 1));
                MoveTo(d, Outside);

                using var start = new Barrier(2);

                var republish = new Thread(() =>
                {
                    start.SignalAndWait();

                    for (var i = 0; i < 200; i++)
                        d.ReplacePvpBinding(RoomBind(m, 1));
                });

                var exit = new Thread(() =>
                {
                    start.SignalAndWait();

                    for (var i = 0; i < 200; i++)
                        d.CheckPvpSpawnRoomExit();
                });

                republish.Start();
                exit.Start();
                republish.Join();
                exit.Join();

                d.ReplacePvpBinding(RoomBind(m, 1));
                Assert.IsFalse(d.PvpBinding.ProtectedInRoom, $"round {round}: room mode came back after the player left");
                Assert.AreEqual(T0.AddSeconds(3), d.PvpBinding.ProtectedUntilUtc, $"round {round}: one exit window, started at the exit");
            }
        }

        [TestMethod]
        public void Room_Text_IsTheOwnerWording()
        {
            Assert.AreEqual("You are protected while in your spawn room and for 3 seconds after you leave. Attacking ends it.", BattlegroundText.SpawnProtectedRoomBody(3));
            Assert.AreEqual("[Battleground] You are protected while in your spawn room and for 1 second after you leave. Attacking ends it.", BattlegroundText.SpawnProtectedRoom(1));
        }

        [TestMethod]
        public void Room_TickHook_RunsAfterThePositionCommit()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Tick.cs");

            // Slice UpdatePlayerPosition's body: from its declaration to the next member declaration at the same indent.
            var start = text.IndexOf("public bool UpdatePlayerPosition(", StringComparison.Ordinal);
            Assert.IsTrue(start > 0, "UpdatePlayerPosition was found");

            var next = System.Text.RegularExpressions.Regex.Match(text.Substring(start + 10), @"(?m)^        (public|private|internal|protected)\b");
            var body = next.Success ? text.Substring(start, next.Index + 10) : text.Substring(start);

            var commit = body.IndexOf("Location = newPosition;", StringComparison.Ordinal);
            var hookMatch = System.Text.RegularExpressions.Regex.Match(body, @"(?m)^\s*CheckPvpSpawnRoomExit\(\);");

            Assert.IsTrue(commit > 0, "the position commit is inside UpdatePlayerPosition");
            Assert.IsTrue(hookMatch.Success, "the exit check is a live statement inside UpdatePlayerPosition, not a comment and not another method");
            Assert.IsTrue(hookMatch.Index > commit, "and it runs after the commit, so it reads the NEW position");
        }

        // ================= wiring on source =================

        [TestMethod]
        public void AttackStartHandlers_EndProtection_BeforeTheyDoAnythingElse()
        {
            var melee = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Melee.cs");
            var missile = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Missile.cs");

            Assert.IsTrue(melee.IndexOf("EndPvpSpawnProtection();", StringComparison.Ordinal) > melee.IndexOf("public void HandleActionTargetedMeleeAttack(", StringComparison.Ordinal));
            Assert.IsTrue(missile.IndexOf("EndPvpSpawnProtection();", StringComparison.Ordinal) > missile.IndexOf("public void HandleActionTargetedMissileAttack(", StringComparison.Ordinal));
            Assert.IsTrue(melee.IndexOf("EndPvpSpawnProtection();", StringComparison.Ordinal) < melee.IndexOf("log.Warn($\"{Name}.HandleActionTargetedMeleeAttack(", StringComparison.Ordinal));
            Assert.IsTrue(missile.IndexOf("EndPvpSpawnProtection();", StringComparison.Ordinal) < missile.IndexOf("log.Warn($\"{Name}.HandleActionTargetedMissileAttack(", StringComparison.Ordinal));
        }
    }
}