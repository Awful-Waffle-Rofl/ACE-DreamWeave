using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The arena 1v1 tunables (Docs/Pvp/DESIGN.md "Tunables"): pvp_arena_dmg_mod_1v1,
    /// pvp_arena_healkit_skill_cap_1v1, pvp_arena_healkit_restoration_cap_1v1. Each discriminates a 1v1 match
    /// from 2v2, FFA and no-match, with the binding faked.
    /// </summary>
    [TestClass]
    public class PvpArenaOneVOneRulesTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private Func<PvpArenaDials> savedDialSource;

        private Func<ACE.Server.Pvp.Battlegrounds.BattlegroundDials> savedBgDialSource;

        [TestInitialize]
        public void Setup()
        {
            savedDialSource = PvpTunables.DialSource;
            savedBgDialSource = ACE.Server.Pvp.Battlegrounds.BattlegroundTunables.DialSource;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpTunables.DialSource = savedDialSource;
            ACE.Server.Pvp.Battlegrounds.BattlegroundTunables.DialSource = savedBgDialSource;
        }

        private static PvpMatch NewMatch(string modeKey)
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), modeKey, teams, Now);
        }

        private static PvpPlayerBinding Binding(PvpMatch match, int teamIndex, PvpMatchState state) =>
            new PvpPlayerBinding(match, teamIndex, state, true, true, true, true, true);

        private static T Seeded<T>() where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            return wo;
        }

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static Player SeededPlayer(PvpPlayerBinding binding = null)
        {
            var p = Seeded<Player>();
            p.SetPvpBindingForTests(binding);
            return p;
        }

        // ================= pvp_arena_dmg_mod_1v1 (AM1) =================

        [TestMethod]
        public void DmgMod_PureFunction_AppliesOnlyWhenInSameLive1v1()
        {
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(false, 2.0, 100.0f), "not in same 1v1: unchanged");
            Assert.AreEqual(200.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(true, 2.0, 100.0f), "in same 1v1: scaled");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(true, double.NaN, 100.0f), "NaN mod: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(true, -1.0, 100.0f), "negative mod: unchanged");
        }

        /// <summary>DISCRIMINATES: a Live 1v1 match reads the mod; 2v2, FFA and no-match never do.</summary>
        [TestMethod]
        public void DmgMod_ChokePointEntry_DiscriminatesMatchMode()
        {
            PvpTunables.DialSource = () => PvpTunables.Defaults with { DmgMod1v1 = 3.0 };

            var oneVOneMatch = NewMatch(ArenaMapCatalog.OneVOneKey);
            var attacker = SeededPlayer(Binding(oneVOneMatch, 0, PvpMatchState.Live));
            var defender = SeededPlayer(Binding(oneVOneMatch, 1, PvpMatchState.Live));

            Assert.AreEqual(300.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(attacker, defender, 100.0f), "same Live 1v1 match: scaled by 3");

            var twoVTwoMatch = NewMatch(ArenaMapCatalog.TwoVTwoKey);
            var attacker2 = SeededPlayer(Binding(twoVTwoMatch, 0, PvpMatchState.Live));
            var defender2 = SeededPlayer(Binding(twoVTwoMatch, 1, PvpMatchState.Live));

            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(attacker2, defender2, 100.0f), "2v2 match: unchanged");

            var noMatchAttacker = SeededPlayer();
            var noMatchDefender = SeededPlayer();

            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(noMatchAttacker, noMatchDefender, 100.0f), "no match: unchanged");

            var creatureTarget = TestCreatures.CreateDefender();

            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(attacker, creatureTarget, 100.0f), "non-player target: unchanged");
        }

        // ================= pvp_arena_dmg_mod_ffa (AM1, FFA) =================

        [TestMethod]
        public void DmgModFfa_PureFunction_AppliesOnlyWhenInSameLiveFfa()
        {
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(false, 2.0, 100.0f), "not in same FFA: unchanged");
            Assert.AreEqual(200.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(true, 2.0, 100.0f), "in same FFA: scaled");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(true, double.NaN, 100.0f), "NaN mod: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(true, -1.0, 100.0f), "negative mod: unchanged");
        }

        /// <summary>DISCRIMINATES: a Live FFA match reads the FFA mod and never the 1v1 one; 1v1, 2v2, no-match and a not-Live FFA never read it.</summary>
        [TestMethod]
        public void DmgModFfa_ChokePointEntry_DiscriminatesMatchMode_AndIsDisjointFrom1v1()
        {
            PvpTunables.DialSource = () => PvpTunables.Defaults with { DmgModFfa = 3.0, DmgMod1v1 = 5.0 };

            var ffa = NewMatch(ArenaMapCatalog.FfaKey);
            var a = SeededPlayer(Binding(ffa, 0, PvpMatchState.Live));
            var d = SeededPlayer(Binding(ffa, 1, PvpMatchState.Live));

            Assert.AreEqual(300.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(a, d, 100.0f), "same Live FFA match: scaled by the FFA mod");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(a, d, 100.0f), "the 1v1 entry stays out of an FFA match");

            var oneVOne = NewMatch(ArenaMapCatalog.OneVOneKey);
            var a1 = SeededPlayer(Binding(oneVOne, 0, PvpMatchState.Live));
            var d1 = SeededPlayer(Binding(oneVOne, 1, PvpMatchState.Live));

            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(a1, d1, 100.0f), "1v1 match: the FFA mod does not apply");
            Assert.AreEqual(500.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(a1, d1, 100.0f), "1v1 match: the 1v1 mod still applies");

            var twoVTwo = NewMatch(ArenaMapCatalog.TwoVTwoKey);
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(SeededPlayer(Binding(twoVTwo, 0, PvpMatchState.Live)), SeededPlayer(Binding(twoVTwo, 1, PvpMatchState.Live)), 100.0f), "2v2: unchanged");

            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(SeededPlayer(Binding(ffa, 0, PvpMatchState.Countdown)), SeededPlayer(Binding(ffa, 1, PvpMatchState.Countdown)), 100.0f), "not Live: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(SeededPlayer(), SeededPlayer(), 100.0f), "no match: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(a, TestCreatures.CreateDefender(), 100.0f), "non-player target: unchanged");
        }

        [TestMethod]
        public void DmgModFfa_DefaultIsTheIdentity()
        {
            PvpTunables.DialSource = () => PvpTunables.Defaults;

            var ffa = NewMatch(ArenaMapCatalog.FfaKey);

            Assert.AreEqual(1.0, PvpTunables.Defaults.DmgModFfa);
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(SeededPlayer(Binding(ffa, 0, PvpMatchState.Live)), SeededPlayer(Binding(ffa, 1, PvpMatchState.Live)), 100.0f));
        }

        // ================= pvp_arena_ffa_ring_dmg (AM1, Tugak ring) =================

        [TestMethod]
        public void FfaRingDmg_PureFunction_AppliesOnlyToARingInSameLiveFfa()
        {
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(false, true, 2.0, 100.0f), "not in same FFA: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(true, false, 2.0, 100.0f), "not a ring (a bolt): unchanged");
            Assert.AreEqual(200.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(true, true, 2.0, 100.0f), "ring in same FFA: scaled");
            Assert.AreEqual(50.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(true, true, 0.5, 100.0f), "ring in same FFA: scaled down");
            Assert.AreEqual(0.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(true, true, 0.0, 100.0f), "0 is a real setting");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(true, true, double.NaN, 100.0f), "NaN: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(true, true, double.PositiveInfinity, 100.0f), "infinite: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(true, true, -1.0, 100.0f), "negative: unchanged");
        }

        /// <summary>
        /// DISCRIMINATES: a ring in a Live FFA match reads the ring mod (school-blind: the entry takes no school, so a Life ring like
        /// Curse of Raven Fury and a war ring both pass); a non-ring in FFA, and a ring in 1v1, 2v2, a battleground, a not-Live FFA, no
        /// match, a self hit or a creature target, never do.
        /// </summary>
        [TestMethod]
        public void FfaRingDmg_ChokePointEntry_ScalesRingsInLiveFfaOnly()
        {
            PvpTunables.DialSource = () => PvpTunables.Defaults with { FfaRingDmg = 0.5, DmgModFfa = 3.0, DmgMod1v1 = 5.0 };

            var ffa = NewMatch(ArenaMapCatalog.FfaKey);
            var a = SeededPlayer(Binding(ffa, 0, PvpMatchState.Live));
            var d = SeededPlayer(Binding(ffa, 1, PvpMatchState.Live));

            Assert.AreEqual(50.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(a, d, true, 100.0f), "ring in a Live FFA match: scaled by the ring mod only");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(a, d, false, 100.0f), "a bolt in a Live FFA match: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(a, a, true, 100.0f), "self hit: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(a, TestCreatures.CreateDefender(), true, 100.0f), "creature target: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(TestCreatures.CreateDefender(), d, true, 100.0f), "creature source: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(SeededPlayer(Binding(ffa, 0, PvpMatchState.Countdown)), SeededPlayer(Binding(ffa, 1, PvpMatchState.Countdown)), true, 100.0f), "not Live: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(SeededPlayer(), SeededPlayer(), true, 100.0f), "no match (open world): unchanged");

            foreach (var key in new[] { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey, ACE.Server.Pvp.Battlegrounds.BattlegroundModes.KothModeKey, ACE.Server.Pvp.Battlegrounds.BattlegroundModes.AttackDefendModeKey })
            {
                var m = NewMatch(key);
                Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(SeededPlayer(Binding(m, 0, PvpMatchState.Live)), SeededPlayer(Binding(m, 1, PvpMatchState.Live)), true, 100.0f), $"{key}: the Tugak ring mod stays out");
            }

            // A separate dial from the FFA damage slider, not exclusive with it: each entry reads only its own setting (FfaRingDmg / DmgModFfa), and a war or void ring in Tugak gets both, multiplied.
            Assert.AreEqual(300.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(a, d, 100.0f), "the FFA slider is a separate dial");
        }

        [TestMethod]
        public void FfaRingDmg_DefaultIsTheIdentity()
        {
            PvpTunables.DialSource = () => PvpTunables.Defaults;

            var ffa = NewMatch(ArenaMapCatalog.FfaKey);

            Assert.AreEqual(1.0, PvpTunables.Defaults.FfaRingDmg);
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyFfaRingDmg(SeededPlayer(Binding(ffa, 0, PvpMatchState.Live)), SeededPlayer(Binding(ffa, 1, PvpMatchState.Live)), true, 100.0f));
        }

        // ================= pvp_bg_dmg_mod (AM1, every battleground mode) =================

        [TestMethod]
        public void DmgModBg_PureFunction_AppliesOnlyWhenInSameLiveBattleground()
        {
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(false, 2.0, 100.0f), "not in the same battleground: unchanged");
            Assert.AreEqual(200.0f, PvpArenaOneVOneRules.ApplyDmgModBg(true, 2.0, 100.0f), "in the same battleground: scaled");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(true, double.NaN, 100.0f), "NaN mod: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(true, double.PositiveInfinity, 100.0f), "infinite mod: unchanged");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(true, -1.0, 100.0f), "negative mod: unchanged");
            Assert.AreEqual(0.0f, PvpArenaOneVOneRules.ApplyDmgModBg(true, 0.0, 100.0f), "0 is a real setting: no damage");
        }

        /// <summary>
        /// DISCRIMINATES: a Live battleground match of EITHER mode (King of the Hill, Attack/Defend) and a future "bg_" mode read the
        /// battleground mod and never the FFA one; every arena mode, a not-Live battleground, no match, a self hit and a non-player
        /// target (a crystal is a Creature) never read it.
        /// </summary>
        [TestMethod]
        public void DmgModBg_ChokePointEntry_CoversEveryBattlegroundMode_AndNothingElse()
        {
            PvpTunables.DialSource = () => PvpTunables.Defaults with { DmgModFfa = 3.0, DmgMod1v1 = 5.0 };
            ACE.Server.Pvp.Battlegrounds.BattlegroundTunables.DialSource = () => ACE.Server.Pvp.Battlegrounds.BattlegroundTunables.Defaults with { DmgMod = 0.5 };

            foreach (var key in new[] { ACE.Server.Pvp.Battlegrounds.BattlegroundModes.KothModeKey, ACE.Server.Pvp.Battlegrounds.BattlegroundModes.AttackDefendModeKey, "bg_future" })
            {
                var bg = NewMatch(key);
                var a = SeededPlayer(Binding(bg, 0, PvpMatchState.Live));
                var d = SeededPlayer(Binding(bg, 1, PvpMatchState.Live));

                Assert.AreEqual(50.0f, PvpArenaOneVOneRules.ApplyDmgModBg(a, d, 100.0f), $"{key}: scaled by the battleground mod");
                Assert.AreEqual(50.0f, PvpArenaOneVOneRules.ApplyDmgModBg(a, SeededPlayer(Binding(bg, 0, PvpMatchState.Live)), 100.0f), $"{key}: a teammate hit scales too (friendly fire is the gate's call)");
                Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModFfa(a, d, 100.0f), $"{key}: the FFA entry stays out");
                Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgMod1v1(a, d, 100.0f), $"{key}: the 1v1 entry stays out");
                Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(SeededPlayer(Binding(bg, 0, PvpMatchState.Countdown)), SeededPlayer(Binding(bg, 1, PvpMatchState.Countdown)), 100.0f), $"{key}: not Live");
                Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(a, a, 100.0f), $"{key}: self");
                Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(a, TestCreatures.CreateDefender(), 100.0f), $"{key}: a creature (a crystal) is never scaled");
            }

            foreach (var key in new[] { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey, ArenaMapCatalog.FfaKey })
            {
                var arena = NewMatch(key);
                Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(SeededPlayer(Binding(arena, 0, PvpMatchState.Live)), SeededPlayer(Binding(arena, 1, PvpMatchState.Live)), 100.0f), $"arena {key}: unchanged");
            }

            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(SeededPlayer(), SeededPlayer(), 100.0f), "no match (open world): unchanged");

            var koth1 = NewMatch(ACE.Server.Pvp.Battlegrounds.BattlegroundModes.KothModeKey);
            var koth2 = NewMatch(ACE.Server.Pvp.Battlegrounds.BattlegroundModes.KothModeKey);
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(SeededPlayer(Binding(koth1, 0, PvpMatchState.Live)), SeededPlayer(Binding(koth2, 1, PvpMatchState.Live)), 100.0f), "two different battleground matches: unchanged");
        }

        [TestMethod]
        public void DmgModBg_DefaultIsTheIdentity()
        {
            ACE.Server.Pvp.Battlegrounds.BattlegroundTunables.DialSource = () => ACE.Server.Pvp.Battlegrounds.BattlegroundTunables.Defaults;

            var bg = NewMatch(ACE.Server.Pvp.Battlegrounds.BattlegroundModes.AttackDefendModeKey);

            Assert.AreEqual(1.0, ACE.Server.Pvp.Battlegrounds.BattlegroundTunables.Defaults.DmgMod);
            Assert.AreEqual(1.0, ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties["pvp_bg_dmg_mod"].Item, "the PropertyManager default matches the dial default");
            Assert.AreEqual(100.0f, PvpArenaOneVOneRules.ApplyDmgModBg(SeededPlayer(Binding(bg, 0, PvpMatchState.Live)), SeededPlayer(Binding(bg, 1, PvpMatchState.Live)), 100.0f));
        }

        // ================= pvp_arena_healkit_skill_cap_1v1 (HK1) =================

        /// <summary>DISCRIMINATES: a target bound to a Live 1v1 match gets its boost capped; 2v2 and no-match do not.</summary>
        [TestMethod]
        public void HealkitSkillCap_DiscriminatesMatchMode()
        {
            PvpTunables.DialSource = () => PvpTunables.Defaults with { HealkitSkillCap1v1 = 50 };

            var oneVOneMatch = NewMatch(ArenaMapCatalog.OneVOneKey);
            var target = SeededPlayer(Binding(oneVOneMatch, 0, PvpMatchState.Live));

            Assert.AreEqual(50, PvpArenaOneVOneRules.ApplyHealkitSkillCap1v1(target, 999), "1v1 match: capped to 50");

            var twoVTwoMatch = NewMatch(ArenaMapCatalog.TwoVTwoKey);
            var target2 = SeededPlayer(Binding(twoVTwoMatch, 0, PvpMatchState.Live));

            Assert.AreEqual(999, PvpArenaOneVOneRules.ApplyHealkitSkillCap1v1(target2, 999), "2v2 match: unchanged");

            var noMatchTarget = SeededPlayer();

            Assert.AreEqual(999, PvpArenaOneVOneRules.ApplyHealkitSkillCap1v1(noMatchTarget, 999), "no match: unchanged");
        }

        [TestMethod]
        public void CapHealkitSkillBonus_IsMin()
        {
            Assert.AreEqual(50, PvpArenaOneVOneRules.CapHealkitSkillBonus(999, 50));
            Assert.AreEqual(10, PvpArenaOneVOneRules.CapHealkitSkillBonus(10, 50), "never widens the boost");
        }

        // ================= pvp_arena_healkit_restoration_cap_1v1 (HK2) =================

        /// <summary>DISCRIMINATES: a target bound to a Live 1v1 match gets its restoration mod capped; 2v2 and no-match do not.</summary>
        [TestMethod]
        public void HealkitRestorationCap_DiscriminatesMatchMode()
        {
            PvpTunables.DialSource = () => PvpTunables.Defaults with { HealkitRestorationCap1v1 = 1.2 };

            var oneVOneMatch = NewMatch(ArenaMapCatalog.OneVOneKey);
            var target = SeededPlayer(Binding(oneVOneMatch, 0, PvpMatchState.Live));

            Assert.AreEqual(1.2, PvpArenaOneVOneRules.ApplyHealkitRestorationCap1v1(target, 5.0), "1v1 match: capped to 1.2");

            var twoVTwoMatch = NewMatch(ArenaMapCatalog.TwoVTwoKey);
            var target2 = SeededPlayer(Binding(twoVTwoMatch, 0, PvpMatchState.Live));

            Assert.AreEqual(5.0, PvpArenaOneVOneRules.ApplyHealkitRestorationCap1v1(target2, 5.0), "2v2 match: unchanged");
        }

        [TestMethod]
        public void CapHealkitRestorationBonus_IsMin()
        {
            Assert.AreEqual(1.2, PvpArenaOneVOneRules.CapHealkitRestorationBonus(5.0, 1.2));
            Assert.AreEqual(0.5, PvpArenaOneVOneRules.CapHealkitRestorationBonus(0.5, 1.2), "never widens the bonus");
        }

        // ================= PvpPlayerRules.InSameLive1v1Match =================

        [TestMethod]
        public void InSameLive1v1Match_RequiresBothLiveAndOneVOne()
        {
            var oneVOne = NewMatch(ArenaMapCatalog.OneVOneKey);
            var a = Binding(oneVOne, 0, PvpMatchState.Live);
            var b = Binding(oneVOne, 1, PvpMatchState.Live);

            Assert.IsTrue(PvpPlayerRules.InSameLive1v1Match(a, b));

            var twoVTwo = NewMatch(ArenaMapCatalog.TwoVTwoKey);
            var c = Binding(twoVTwo, 0, PvpMatchState.Live);
            var d = Binding(twoVTwo, 1, PvpMatchState.Live);

            Assert.IsFalse(PvpPlayerRules.InSameLive1v1Match(c, d), "2v2 is not 1v1");
            Assert.IsFalse(PvpPlayerRules.InSameLive1v1Match(null, b), "null binding");

            var notLive = Binding(oneVOne, 1, PvpMatchState.Staging);
            Assert.IsFalse(PvpPlayerRules.InSameLive1v1Match(a, notLive), "not Live");
        }
    }
}
