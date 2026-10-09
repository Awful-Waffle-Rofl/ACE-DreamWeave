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
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Step 3 of Docs/Pvp/BATTLEGROUNDS.md: the player side of the respawn flow. The pure rules (gate, DoT, cast
    /// refusal, death destination, backstop, pen return, respawn guard) and, on a seeded Player (the
    /// PvpPlayerHookTests fixture: GetUninitializedObject plus only inherited WorldObject state), the gate wiring,
    /// the cast predicate and the lifestone clear a respawn performs.
    /// </summary>
    [TestClass]
    public class BattlegroundPlayerSideTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private Func<bool> savedFriendlyFire;

        [TestInitialize]
        public void Setup()
        {
            savedFriendlyFire = PvpArenaHookSettings.FriendlyFireSource;
            PvpArenaHookSettings.FriendlyFireSource = () => false;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.FriendlyFireSource = savedFriendlyFire;
        }

        private static PvpMatch NewMatch()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500), new PvpParticipant(2, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500), new PvpParticipant(4, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), "bg_koth", teams, Now);
        }

        private const uint Instance = 0x80010101;

        private static Position Pen() => new Position(0x016C0134, 0f, -60f, 0.005f, 0f, 0f, -0.707107f, 0.707107f, Instance);

        private static PvpPlayerBinding Bind(PvpMatch match, int team, PvpMatchState state = PvpMatchState.Live, bool respawning = false, Position pen = null) =>
            new PvpPlayerBinding(match, team, state, true, true, true, true, true, respawnPen: pen ?? Pen(), respawning: respawning);

        // ================= the damage gate =================

        [TestMethod]
        public void Gate_RespawningDefender_Refused()
        {
            var m = NewMatch();

            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 1), false), "control: opponents in a Live match");
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 1, respawning: true), false));
        }

        [TestMethod]
        public void Gate_RespawningAttacker_Refused()
        {
            var m = NewMatch();

            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Bind(m, 0, respawning: true), Bind(m, 1), false));
        }

        [TestMethod]
        public void Gate_RespawningTeammate_FriendlyFireOn_StillRefused()
        {
            var m = NewMatch();

            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 0), true), "control: friendly fire on");
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Bind(m, 0), Bind(m, 0, respawning: true), true));
        }

        // ================= DoT suppression =================

        [TestMethod]
        public void Dot_RespawningTarget_IsSuppressed_WhereLiveWouldTick()
        {
            var m = NewMatch();

            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(Bind(m, 1), Bind(m, 0)), "control: Live opponents tick");
            Assert.IsTrue(PvpArenaSpellRules.ShouldSuppressDotTick(Bind(m, 1, respawning: true), Bind(m, 0)));
            Assert.IsTrue(PvpArenaSpellRules.ShouldSuppressDotTick(Bind(m, 1), Bind(m, 0, respawning: true)), "a DoT from a player in the pen does not tick either");
        }

        // ================= cast refusal =================

        [TestMethod]
        public void Cast_RefusedOnlyWhileRespawning()
        {
            var m = NewMatch();

            Assert.IsFalse(PvpPlayerRules.CastRefusedWhileRespawning(null));
            Assert.IsFalse(PvpPlayerRules.CastRefusedWhileRespawning(Bind(m, 0)));
            Assert.IsTrue(PvpPlayerRules.CastRefusedWhileRespawning(Bind(m, 0, respawning: true)));
        }

        // ================= death destination =================

        [TestMethod]
        public void DeathDestination_PenOnlyWhenTheSameMatchIsStillLive()
        {
            var m = NewMatch();
            var pen = Pen();

            Assert.AreSame(pen, PvpPlayerRules.DeathDestination(pen, m.MatchId, Bind(m, 0, respawning: true)), "Live, same match: the pen");
            Assert.AreSame(pen, PvpPlayerRules.DeathDestination(pen, m.MatchId, Bind(m, 0)), "Live, same match, respawning not yet republished: still the pen");
        }

        [TestMethod]
        public void DeathDestination_NoPenLatched_IsTheExitPath()
        {
            var m = NewMatch();

            Assert.IsNull(PvpPlayerRules.DeathDestination(null, m.MatchId, Bind(m, 0)));
            Assert.IsNull(PvpPlayerRules.DeathDestination(Pen(), Guid.Empty, Bind(m, 0)));
        }

        [TestMethod]
        public void DeathDestination_BindingNotLive_IsTheExitPath()
        {
            var m = NewMatch();

            Assert.IsNull(PvpPlayerRules.DeathDestination(Pen(), m.MatchId, Bind(m, 0, PvpMatchState.Resolving)));
            Assert.IsNull(PvpPlayerRules.DeathDestination(Pen(), m.MatchId, Bind(m, 0, PvpMatchState.Closed)));
            Assert.IsNull(PvpPlayerRules.DeathDestination(Pen(), m.MatchId, null), "exited while dying");
        }

        [TestMethod]
        public void DeathDestination_DifferentMatch_IsTheExitPath()
        {
            var m = NewMatch();

            Assert.IsNull(PvpPlayerRules.DeathDestination(Pen(), m.MatchId, Bind(NewMatch(), 0)));
        }

        // ================= backstop and pen return =================

        [TestMethod]
        public void PenReturnNeeded_UnlessStillLiveInTheSameMatch()
        {
            var m = NewMatch();

            Assert.IsFalse(PvpPlayerRules.PenReturnNeeded(m.MatchId, Bind(m, 0, respawning: true)));
            Assert.IsTrue(PvpPlayerRules.PenReturnNeeded(m.MatchId, null));
            Assert.IsTrue(PvpPlayerRules.PenReturnNeeded(m.MatchId, Bind(m, 0, PvpMatchState.Resolving)));
            Assert.IsTrue(PvpPlayerRules.PenReturnNeeded(m.MatchId, Bind(NewMatch(), 0)));
        }

        [TestMethod]
        public void ShouldReturnFromPen_OnlyWhenSettledInThePenInstance()
        {
            Assert.IsTrue(PvpPlayerRules.ShouldReturnFromPen(Pen(), inDeathProcess: false, Instance));
            Assert.IsFalse(PvpPlayerRules.ShouldReturnFromPen(Pen(), inDeathProcess: true, Instance), "still dying: the death path owns the trip");
            Assert.IsFalse(PvpPlayerRules.ShouldReturnFromPen(Pen(), inDeathProcess: false, Instance + 1), "already elsewhere");
            Assert.IsFalse(PvpPlayerRules.ShouldReturnFromPen(Pen(), inDeathProcess: false, null));
            Assert.IsFalse(PvpPlayerRules.ShouldReturnFromPen(null, inDeathProcess: false, Instance), "no pen: an arena exit");
        }

        [TestMethod]
        public void CanRespawn_RequiresBoundSameMatchInInstanceAliveAndOutOfTheDeathSequence()
        {
            var m = NewMatch();
            var b = Bind(m, 0, respawning: true);

            Assert.IsTrue(PvpPlayerRules.CanRespawn(b, m.MatchId, Instance, Instance, isDead: false, inDeathProcess: false));
            Assert.IsFalse(PvpPlayerRules.CanRespawn(null, m.MatchId, Instance, Instance, false, false));
            Assert.IsFalse(PvpPlayerRules.CanRespawn(b, Guid.NewGuid(), Instance, Instance, false, false));
            Assert.IsFalse(PvpPlayerRules.CanRespawn(b, m.MatchId, Instance + 1, Instance, false, false));
            Assert.IsFalse(PvpPlayerRules.CanRespawn(b, m.MatchId, Instance, Instance, isDead: true, inDeathProcess: false));
            Assert.IsFalse(PvpPlayerRules.CanRespawn(b, m.MatchId, Instance, Instance, isDead: false, inDeathProcess: true));
        }

        // ================= on a seeded Player =================

        private static Player SeededPlayer(PlayerKillerStatus status)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(player, "ephemeralPositions", new Dictionary<PositionType, Position>());
            SetInherited(player, "<Sequences>k__BackingField", new ACE.Server.Network.Sequence.SequenceManager());

            player.PlayerKillerStatus = status;
            player.Location = new Position { LandblockId = new LandblockId(0x00900100), PositionX = 50f, PositionY = 50f, RotationW = 1f };

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        /// <summary>
        /// Reaches Player.CheckPKStatusVsTarget (TryPvpArenaGate) with two PK opponents in a Live match: allowed, and
        /// refused once the target is respawning, so the gate refusal is wired through the real call site.
        /// </summary>
        [TestMethod]
        public void SeededPlayer_Gate_RespawningTarget_Refused()
        {
            var m = NewMatch();
            var a = SeededPlayer(PlayerKillerStatus.PK);
            var b = SeededPlayer(PlayerKillerStatus.PK);

            a.SetPvpBindingForTests(Bind(m, 0));
            b.SetPvpBindingForTests(Bind(m, 1));
            Assert.IsNull(a.CheckPKStatusVsTarget(b, null), "control: Live opponents may fight");

            b.SetPvpBindingForTests(Bind(m, 1, respawning: true));
            var result = a.CheckPKStatusVsTarget(b, null);

            Assert.IsNotNull(result, "a respawning target must be refused");
            Assert.AreEqual(WeenieErrorWithString.YouFailToAffect_NotSamePKType, result[0]);
        }

        [TestMethod]
        public void SeededPlayer_CastPredicate_FollowsTheBinding()
        {
            var m = NewMatch();
            var p = SeededPlayer(PlayerKillerStatus.PKLite);

            Assert.IsFalse(p.PvpCastRefusedWhileRespawning, "unbound");

            p.SetPvpBindingForTests(Bind(m, 0));
            Assert.IsFalse(p.PvpCastRefusedWhileRespawning, "bound, alive");

            p.SetPvpBindingForTests(Bind(m, 0, respawning: true));
            Assert.IsTrue(p.PvpCastRefusedWhileRespawning, "respawning");
            Assert.IsTrue(p.IsPvpRespawning);
        }

        /// <summary>
        /// PINS the risk row "Lifestone protection makes a respawned player unkillable on the hill for a minute": the
        /// respawn's shield clear (RespawnInBattleground -> ClearBattlegroundRespawnShields) removes it.
        /// </summary>
        [TestMethod]
        public void Respawn_ClearsLifestoneProtection()
        {
            var p = SeededPlayer(PlayerKillerStatus.PKLite);
            p.SetLifestoneProtection();

            Assert.IsTrue(p.UnderLifestoneProtection, "fixture: protection set the way the death sequence sets it");

            p.ClearBattlegroundRespawnShields();

            Assert.IsFalse(p.UnderLifestoneProtection);
            Assert.IsNull(p.LifestoneProtectionTimestamp);
        }

        /// <summary>
        /// The registry removal half of EnchantmentManager.Dispel, without the client message and spell hooks a seeded
        /// Player cannot run (no Session, no spell table). Records what it was asked to remove.
        /// </summary>
        private sealed class RegistryOnlyEnchantmentManager : ACE.Server.WorldObjects.Managers.EnchantmentManagerWithCaching
        {
            public readonly List<ACE.Entity.Models.PropertiesEnchantmentRegistry> Dispelled = new();

            public RegistryOnlyEnchantmentManager(WorldObject obj) : base(obj) { }

            public override void Dispel(List<ACE.Entity.Models.PropertiesEnchantmentRegistry> entries)
            {
                foreach (var entry in entries)
                {
                    Dispelled.Add(entry);
                    ACE.Entity.Models.PropertiesEnchantmentRegistryExtensions.TryRemoveEnchantment(WorldObject.Biota.PropertiesEnchantmentRegistry, entry, WorldObject.BiotaDatabaseLock);
                }
            }
        }

        private static ACE.Entity.Models.PropertiesEnchantmentRegistry Enchantment(int spellId, uint caster, bool beneficial) => new()
        {
            SpellId = spellId,
            LayerId = 1,
            CasterObjectId = caster,
            Duration = 60,
            StatModType = beneficial ? EnchantmentTypeFlags.Beneficial : EnchantmentTypeFlags.Undef,
        };

        /// <summary>
        /// The respawn's shield clear removes a harmful enchantment another player put on the respawner, and only that:
        /// the respawner's own harm and another player's beneficial spell stay.
        /// </summary>
        [TestMethod]
        public void Respawn_RemovesHarmFromOtherPlayers()
        {
            var p = SeededPlayer(PlayerKillerStatus.PKLite);
            var manager = new RegistryOnlyEnchantmentManager(p);
            p.EnchantmentManager = manager;

            var foreignHarm = Enchantment(100, 0x50000002, beneficial: false);
            var selfHarm = Enchantment(101, p.Guid.Full, beneficial: false);
            var foreignBuff = Enchantment(102, 0x50000002, beneficial: true);
            p.Biota.PropertiesEnchantmentRegistry = new List<ACE.Entity.Models.PropertiesEnchantmentRegistry> { foreignHarm, selfHarm, foreignBuff };

            p.ClearBattlegroundRespawnShields();

            var left = p.Biota.PropertiesEnchantmentRegistry;
            Assert.IsFalse(left.Any(e => e.SpellId == 100), "the other player's harm is gone");
            Assert.IsTrue(left.Any(e => e.SpellId == 101), "control: own harm stays");
            Assert.IsTrue(left.Any(e => e.SpellId == 102), "control: a beneficial spell stays");
            Assert.AreEqual(1, manager.Dispelled.Count);
        }

        /// <summary>The respawn vital rule: health, stamina and mana each set to their own maximum, nothing else.</summary>
        [TestMethod]
        public void Respawn_RestoresAllThreeVitalsToMax()
        {
            var max = new Dictionary<PropertyAttribute2nd, uint>
            {
                [PropertyAttribute2nd.MaxHealth] = 400,
                [PropertyAttribute2nd.MaxStamina] = 300,
                [PropertyAttribute2nd.MaxMana] = 200,
            };
            var set = new Dictionary<PropertyAttribute2nd, uint>();

            PvpPlayerRules.RestoreRespawnVitals(v => max[v], (v, value) => set[v] = value);

            CollectionAssert.AreEquivalent(max.ToList(), set.ToList());
        }

        /// <summary>
        /// The respawn action itself passes the live max and writes through UpdateVital (a Player cannot be constructed
        /// in this suite, so the call site is pinned by source, the PvpArenaWiringTests pattern).
        /// </summary>
        [TestMethod]
        public void RespawnInBattleground_WiresTheVitalRestoreAndShieldClear()
        {
            var body = Squash(MethodBody("Player_PvpArena.cs", "public void RespawnInBattleground(Guid matchId, Position spawn)"));

            Assert.IsTrue(body.Contains(Squash("PvpPlayerRules.RestoreRespawnVitals(v => Vitals[v].MaxValue, (v, max) => UpdateVital(Vitals[v], max));"), StringComparison.Ordinal), "vital restore wired at max");
            Assert.IsTrue(body.Contains(Squash("ClearBattlegroundRespawnShields();"), StringComparison.Ordinal), "shield clear wired");
        }

        // ================= healing kit =================

        [TestMethod]
        public void HealKitRule_RefusesAnotherPlayerOnlyWhileRespawning()
        {
            var m = NewMatch();

            Assert.IsFalse(PvpPlayerRules.HealKitRefusedWhileRespawning(null, onSelf: false), "unbound");
            Assert.IsFalse(PvpPlayerRules.HealKitRefusedWhileRespawning(Bind(m, 0), onSelf: false), "bound, alive");
            Assert.IsTrue(PvpPlayerRules.HealKitRefusedWhileRespawning(Bind(m, 0, respawning: true), onSelf: false), "respawning, on another player");
            Assert.IsFalse(PvpPlayerRules.HealKitRefusedWhileRespawning(Bind(m, 0, respawning: true), onSelf: true), "respawning, on oneself");
        }

        [TestMethod]
        public void SeededPlayer_HealKitPredicate_TellsSelfFromAnother()
        {
            var m = NewMatch();
            var healer = SeededPlayer(PlayerKillerStatus.PK);
            var mate = SeededPlayer(PlayerKillerStatus.PK);

            healer.SetPvpBindingForTests(Bind(m, 0, respawning: true));

            Assert.IsTrue(healer.PvpHealKitRefusedWhileRespawning(mate), "a respawning healer on a teammate");
            Assert.IsFalse(healer.PvpHealKitRefusedWhileRespawning(healer), "a respawning healer on themself");

            healer.SetPvpBindingForTests(Bind(m, 0));
            Assert.IsFalse(healer.PvpHealKitRefusedWhileRespawning(mate), "control: alive");
        }

        /// <summary>Healer.HandleActionUseOnTarget refuses through the predicate (source pin; a Healer needs a weenie).</summary>
        [TestMethod]
        public void Healer_WiresTheRespawnRefusal()
        {
            var body = Squash(MethodBody("Healer.cs", "public override void HandleActionUseOnTarget(Player healer, WorldObject target)"));

            Assert.IsTrue(body.Contains(Squash("if (healer.PvpHealKitRefusedWhileRespawning(targetPlayer)) { healer.SendUseDoneEvent(WeenieError.YoureTooBusy); return; }"), StringComparison.Ordinal),
                "the kit refusal is wired with the YoureTooBusy use-done");
        }

        /// <summary>The brace-matched body of a member of Source/ACE.Server/WorldObjects/<paramref name="file"/>, comments stripped.</summary>
        private static string MethodBody(string file, string signature)
        {
            string path = null;

            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null && path == null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", file);
                if (System.IO.File.Exists(candidate))
                    path = candidate;
            }

            Assert.IsNotNull(path, $"Could not find {file} by walking up from {AppContext.BaseDirectory}.");

            var source = ACE.Server.Tests.WeaponModSuppressionWiringTests.StripComments(System.IO.File.ReadAllText(path));
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"{file}: {signature} not found");

            var open = source.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"{file}: unbalanced braces after {signature}");
            return null;
        }

        private static string Squash(string text) => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }
}
