using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;
using DatSpellBase = ACE.DatLoader.Entity.SpellBase;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Owner ruling 2026-10-15: in Tugak Brawl (FFA) only Curse of Raven Fury does damage, so with pvp_arena_ffa_spell_only on, an attack
    /// that carries no spell (spell == null: melee, missile, cleave, multi-shot, riposte) between two players of the same Live FFA match
    /// is refused by Player.TryPvpArenaGate.
    ///
    /// SEEDING follows PvpArenaSpellRulesIntegrationTests: an uninitialized Player with only the inherited state the arena branch reads,
    /// the binding in through SetPvpBindingForTests, and PropertyManager never touched - the key comes through the
    /// PvpArenaHookSettings.FfaSpellOnlySource seam and is restored in a finally-equivalent TestCleanup.
    ///
    /// DISCRIMINATION: Ffa_NullSpell_KeyOn_IsRefusedSilentlyWithOneThrottledNotice fails if the IsPhysicalRefused call is removed from
    /// TryPvpArenaGate (the swing would come back as an allow, null), and the key-off and non-FFA controls fail if the refusal ignored
    /// the key or the mode.
    /// </summary>
    [TestClass]
    public class PvpFfaSpellOnlyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private Func<bool> savedFriendlyFire;
        private Func<bool> savedSpellOnly;
        private Func<DateTime> savedClock;
        private DateTime clock;

        [TestInitialize]
        public void Setup()
        {
            savedFriendlyFire = PvpArenaHookSettings.FriendlyFireSource;
            savedSpellOnly = PvpArenaHookSettings.FfaSpellOnlySource;
            savedClock = PvpArenaHookSettings.UtcNow;

            clock = Now;
            PvpArenaHookSettings.FriendlyFireSource = () => false;
            PvpArenaHookSettings.FfaSpellOnlySource = () => true;
            PvpArenaHookSettings.UtcNow = () => clock;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpArenaHookSettings.FriendlyFireSource = savedFriendlyFire;
            PvpArenaHookSettings.FfaSpellOnlySource = savedSpellOnly;
            PvpArenaHookSettings.UtcNow = savedClock;
        }

        // ================= the pure rule =================

        [TestMethod]
        public void IsPhysicalRefused_TruthTable()
        {
            Assert.IsTrue(PvpArenaSpellRules.IsPhysicalRefused(isFfa: true, spellOnly: true), "FFA with the key on refuses");
            Assert.IsFalse(PvpArenaSpellRules.IsPhysicalRefused(isFfa: true, spellOnly: false), "FFA with the key off is the old rule");
            Assert.IsFalse(PvpArenaSpellRules.IsPhysicalRefused(isFfa: false, spellOnly: true), "any other mode is untouched");
            Assert.IsFalse(PvpArenaSpellRules.IsPhysicalRefused(isFfa: false, spellOnly: false));
        }

        [TestMethod]
        public void KeyDefault_IsOn()
        {
            Assert.IsTrue(PvpArenaHookSettings.FfaSpellOnlyDefault, "owner default for a new setting is ON");
        }

        // ================= the gate =================

        [TestMethod]
        public void Ffa_NullSpell_KeyOn_IsRefusedSilentlyWithOneThrottledNotice()
        {
            var (attacker, target) = LivePair(ArenaMapCatalog.FfaKey);

            Assert.IsTrue(attacker.TryPvpArenaGate(target, null, out var result), "the arena decided");
            Assert.IsNotNull(result, "a swing or shot with no spell is refused in Tugak Brawl");
            Assert.IsTrue(PvpArenaHookSettings.IsSpawnProtectedRefusal(result), "refused with the silent instance: the call sites send nothing, the throttled line is the only message");
            Assert.AreEqual(Now, NoticeAt(attacker), "the attacker's line was sent");

            clock = Now.AddSeconds(1);
            Assert.IsTrue(attacker.TryPvpArenaGate(target, null, out result));
            Assert.IsNotNull(result, "the second swing is refused too");
            Assert.AreEqual(Now, NoticeAt(attacker), "but inside the throttle window no second line goes out");

            clock = Now.AddSeconds(PvpArenaHookSettings.FfaSpellOnlyNoticeIntervalSeconds + 0.5);
            Assert.IsTrue(attacker.TryPvpArenaGate(target, null, out result));
            Assert.AreEqual(clock, NoticeAt(attacker), "after the window the line may repeat");
        }

        [TestMethod]
        public void Ffa_NullSpell_KeyOff_Lands()
        {
            PvpArenaHookSettings.FfaSpellOnlySource = () => false;
            var (attacker, target) = LivePair(ArenaMapCatalog.FfaKey);

            Assert.IsTrue(attacker.TryPvpArenaGate(target, null, out var result));
            Assert.IsNull(result, "key off restores the old rule: melee and missile land");
            Assert.AreEqual(default(DateTime), NoticeAt(attacker), "and nothing is sent");
        }

        [TestMethod]
        public void OtherModes_NullSpell_KeyOn_StillLands()
        {
            foreach (var mode in new[] { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey })
            {
                var (attacker, target) = LivePair(mode);

                Assert.IsTrue(attacker.TryPvpArenaGate(target, null, out var result));
                Assert.IsNull(result, $"{mode}: melee and missile are untouched outside Tugak Brawl");
            }
        }

        [TestMethod]
        public void Ffa_CurseRavenFury_KeyOn_StillLands_AndOtherHarmfulSpellsStayRefusedByTheSpellRule()
        {
            var (attacker, target) = LivePair(ArenaMapCatalog.FfaKey);

            var raven = FakeSpell(MagicSchool.LifeMagic, SpellCategory.HealthLowering, (uint)SpellId.CurseRavenFury);
            Assert.IsNull(attacker.CheckPKStatusVsTarget(target, raven), "Curse of Raven Fury is the one thing that hurts");

            var other = FakeSpell(MagicSchool.WarMagic, SpellCategory.Undef, 3);
            var refused = attacker.CheckPKStatusVsTarget(target, other);
            Assert.IsNotNull(refused);
            Assert.AreEqual(WeenieErrorWithString.YouFailToAffect_YouCannotAffectAnyone, refused[0]);
            Assert.IsFalse(PvpArenaHookSettings.IsSpawnProtectedRefusal(refused), "the spell rule keeps its own refusal pair");
        }

        [TestMethod]
        public void Ffa_NullSpell_ThroughCheckPKStatusVsTarget_IsRefused()
        {
            var (attacker, target) = LivePair(ArenaMapCatalog.FfaKey);

            Assert.IsNotNull(attacker.CheckPKStatusVsTarget(target, null), "the cleave and multi-shot probes ask this exact question");
        }

        // ================= fixtures =================

        private (Player attacker, Player target) LivePair(string modeKey)
        {
            var match = new PvpMatch(Guid.NewGuid(), modeKey, new List<PvpTeam>(), Now);
            var attacker = SeededPlayer();
            var target = SeededPlayer();

            attacker.SetPvpBindingForTests(Bind(match, 0));
            target.SetPvpBindingForTests(Bind(match, 1));

            return (attacker, target);
        }

        private static PvpPlayerBinding Bind(PvpMatch match, int team) =>
            new PvpPlayerBinding(match, team, PvpMatchState.Live, true, true, true, true, true, true);

        private static DateTime NoticeAt(Player player) => player.PvpFfaSpellOnlyNoticeAtUtc;

        private static Player SeededPlayer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetInherited(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(player, "positionCache", new Dictionary<PositionType, ACE.Entity.Position>());
            SetInherited(player, "ephemeralPositions", new Dictionary<PositionType, ACE.Entity.Position>());

            player.PlayerKillerStatus = PlayerKillerStatus.PK;
            player.Location = new ACE.Entity.Position { LandblockId = new LandblockId(0x00900100), PositionX = 50f, PositionY = 50f, RotationW = 1f };

            return player;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static Spell FakeSpell(MagicSchool school, SpellCategory category, uint metaSpellId)
        {
            var spellBase = (DatSpellBase)RuntimeHelpers.GetUninitializedObject(typeof(DatSpellBase));

            SetBackingField(spellBase, "School", school);
            SetBackingField(spellBase, "Category", category);
            SetBackingField(spellBase, "MetaSpellId", metaSpellId);
            SetBackingField(spellBase, "Bitfield", 0u);

            var spell = (Spell)RuntimeHelpers.GetUninitializedObject(typeof(Spell));
            spell._spellBase = spellBase;

            return spell;
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            var field = target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{target.GetType().Name}.<{propertyName}>k__BackingField was not found by reflection");
            field.SetValue(target, value);
        }
    }
}
