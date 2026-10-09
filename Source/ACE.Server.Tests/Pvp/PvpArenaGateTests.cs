using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The pure PvP damage-permission decision (Docs/Pvp/DESIGN.md "Damage gate"): same match and Live
    /// allows; different matches refuse; one side out of a match refuses; teammate harm refused unless
    /// friendly fire is on; neither in a match is NotApplicable.
    /// </summary>
    [TestClass]
    public class PvpArenaGateTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static PvpMatch NewMatch()
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500), new PvpParticipant(2, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), ArenaMapCatalog.TwoVTwoKey, teams, Now);
        }

        private static PvpPlayerBinding Binding(PvpMatch match, int teamIndex, PvpMatchState state) =>
            new PvpPlayerBinding(match, teamIndex, state, true, true, true, true, true);

        [TestMethod]
        public void NeitherInMatch_IsNotApplicable()
        {
            var decision = PvpArenaGate.Evaluate(null, null, friendlyFireEnabled: false);

            Assert.AreEqual(PvpGateDecision.NotApplicable, decision);
        }

        [TestMethod]
        public void SameMatch_Live_OpposingTeams_Allows()
        {
            var match = NewMatch();
            var attacker = Binding(match, 0, PvpMatchState.Live);
            var defender = Binding(match, 1, PvpMatchState.Live);

            var decision = PvpArenaGate.Evaluate(attacker, defender, friendlyFireEnabled: false);

            Assert.AreEqual(PvpGateDecision.Allow, decision);
        }

        [TestMethod]
        public void DifferentMatches_Refuses()
        {
            var matchA = NewMatch();
            var matchB = NewMatch();
            var attacker = Binding(matchA, 0, PvpMatchState.Live);
            var defender = Binding(matchB, 1, PvpMatchState.Live);

            var decision = PvpArenaGate.Evaluate(attacker, defender, friendlyFireEnabled: false);

            Assert.AreEqual(PvpGateDecision.Refuse, decision);
        }

        [TestMethod]
        public void OneSideOutOfAMatch_Refuses()
        {
            var match = NewMatch();
            var attacker = Binding(match, 0, PvpMatchState.Live);

            var decision = PvpArenaGate.Evaluate(attacker, null, friendlyFireEnabled: false);

            Assert.AreEqual(PvpGateDecision.Refuse, decision);
        }

        [TestMethod]
        public void Teammate_Refused_WhenFriendlyFireOff()
        {
            var match = NewMatch();
            var attacker = Binding(match, 0, PvpMatchState.Live);
            var teammate = Binding(match, 0, PvpMatchState.Live);

            var decision = PvpArenaGate.Evaluate(attacker, teammate, friendlyFireEnabled: false);

            Assert.AreEqual(PvpGateDecision.Refuse, decision);
        }

        [TestMethod]
        public void Teammate_Allowed_WhenFriendlyFireOn()
        {
            var match = NewMatch();
            var attacker = Binding(match, 0, PvpMatchState.Live);
            var teammate = Binding(match, 0, PvpMatchState.Live);

            var decision = PvpArenaGate.Evaluate(attacker, teammate, friendlyFireEnabled: true);

            Assert.AreEqual(PvpGateDecision.Allow, decision);
        }

        [TestMethod]
        public void SameMatch_NotYetLive_Refuses()
        {
            var match = NewMatch();
            var attacker = Binding(match, 0, PvpMatchState.Countdown);
            var defender = Binding(match, 1, PvpMatchState.Countdown);

            var decision = PvpArenaGate.Evaluate(attacker, defender, friendlyFireEnabled: false);

            Assert.AreEqual(PvpGateDecision.Refuse, decision);
        }

        // ---------------- battlegrounds never get the Countdown allowance ----------------

        private static PvpMatch NewMatchOfMode(string modeKey)
        {
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(3, 1500) })
            };

            return new PvpMatch(Guid.NewGuid(), modeKey, teams, Now);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("bg")]
        [DataRow("BG_koth")]
        [DataRow("ARENA")]
        [DataRow(ACE.Server.Pvp.Battlegrounds.BattlegroundModes.KothModeKey)]
        [DataRow(ACE.Server.Pvp.Battlegrounds.BattlegroundModes.AttackDefendModeKey)]
        public void BattlegroundCountdown_DefenseLoweringVuln_StaysRefused(string modeKey)
        {
            var match = NewMatchOfMode(modeKey);
            var vuln = ACE.Server.Pvp.Rules.PvpArenaSpellRules.IsCountdownVuln(ACE.Entity.Enum.MagicSchool.CreatureEnchantment, ACE.Entity.Enum.SpellCategory.MeleeDefenseLowering);

            Assert.IsTrue(vuln, "fixture: this is a defense-lowering vuln");
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Binding(match, 0, PvpMatchState.Countdown), Binding(match, 1, PvpMatchState.Countdown), false, vuln));
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Binding(match, 0, PvpMatchState.Countdown), Binding(match, 1, PvpMatchState.Live), false, vuln));
        }

        [DataTestMethod]
        [DataRow(ACE.Server.Pvp.ArenaMapCatalog.OneVOneKey)]
        [DataRow(ACE.Server.Pvp.ArenaMapCatalog.TwoVTwoKey)]
        [DataRow(ACE.Server.Pvp.ArenaMapCatalog.FfaKey)]
        public void ArenaCountdown_DefenseLoweringVuln_IsAllowed_InEveryArenaMode(string modeKey)
        {
            var match = NewMatchOfMode(modeKey);

            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(Binding(match, 0, PvpMatchState.Countdown), Binding(match, 1, PvpMatchState.Countdown), false, true));
        }

        // ---------------- pre-match Countdown: Countdown vulns only (PvpArenaSpellRules.IsCountdownVuln) ----------------

        // Mirrors Player.TryPvpArenaGate's classification: the spell's school/category decides the vuln flag the gate takes.
        private static PvpGateDecision Decide(PvpPlayerBinding attacker, PvpPlayerBinding defender, ACE.Entity.Enum.MagicSchool school, ACE.Entity.Enum.SpellCategory category) =>
            PvpArenaGate.Evaluate(attacker, defender, friendlyFireEnabled: false,
                countdownVuln: ACE.Server.Pvp.Rules.PvpArenaSpellRules.IsCountdownVuln(school, category));

        [DataTestMethod]
        [DataRow(ACE.Entity.Enum.SpellCategory.MeleeDefenseLowering)]
        [DataRow(ACE.Entity.Enum.SpellCategory.MissileDefenseLowering)]
        [DataRow(ACE.Entity.Enum.SpellCategory.MagicDefenseLowering)]
        public void Countdown_BothCountdown_DefenseLoweringVuln_OnEnemy_Allows(ACE.Entity.Enum.SpellCategory category)
        {
            var match = NewMatch();
            var decision = Decide(Binding(match, 0, PvpMatchState.Countdown), Binding(match, 1, PvpMatchState.Countdown), ACE.Entity.Enum.MagicSchool.CreatureEnchantment, category);

            Assert.AreEqual(PvpGateDecision.Allow, decision);
        }

        // Owner ruling 2026-10-08: the seven Life Magic elemental vulnerabilities and Imperil (Armor Lowering) join the Countdown set.
        [DataTestMethod]
        [DataRow(ACE.Entity.Enum.SpellCategory.AcidVulnerability)]
        [DataRow(ACE.Entity.Enum.SpellCategory.BludgeonVulnerability)]
        [DataRow(ACE.Entity.Enum.SpellCategory.ColdVulnerability)]
        [DataRow(ACE.Entity.Enum.SpellCategory.ElectricVulnerability)]
        [DataRow(ACE.Entity.Enum.SpellCategory.FireVulnerability)]
        [DataRow(ACE.Entity.Enum.SpellCategory.PierceVulnerability)]
        [DataRow(ACE.Entity.Enum.SpellCategory.SlashVulnerability)]
        [DataRow(ACE.Entity.Enum.SpellCategory.ArmorLowering)]
        public void Countdown_BothCountdown_LifeVulnOrImperil_OnEnemy_Allows(ACE.Entity.Enum.SpellCategory category)
        {
            var match = NewMatch();

            Assert.AreEqual(PvpGateDecision.Allow, Decide(Binding(match, 0, PvpMatchState.Countdown), Binding(match, 1, PvpMatchState.Countdown), ACE.Entity.Enum.MagicSchool.LifeMagic, category));
            Assert.AreEqual(PvpGateDecision.Refuse, Decide(Binding(match, 0, PvpMatchState.Countdown), Binding(match, 1, PvpMatchState.Countdown), ACE.Entity.Enum.MagicSchool.CreatureEnchantment, category), "school must match: Creature Enchantment with a Life vuln category is refused");
            Assert.AreEqual(PvpGateDecision.Refuse, Decide(Binding(match, 0, PvpMatchState.Countdown), Binding(match, 1, PvpMatchState.Countdown), ACE.Entity.Enum.MagicSchool.WarMagic, category), "school must match: War Magic with a Life vuln category is refused");
        }

        [TestMethod]
        public void IsRefused_CreatureBranch_IsUnchangedByTheLifeWidening()
        {
            foreach (ACE.Entity.Enum.SpellCategory category in Enum.GetValues(typeof(ACE.Entity.Enum.SpellCategory)))
            {
                var defenseLowering = category == ACE.Entity.Enum.SpellCategory.MagicDefenseLowering
                    || category == ACE.Entity.Enum.SpellCategory.MeleeDefenseLowering
                    || category == ACE.Entity.Enum.SpellCategory.MissileDefenseLowering;

                Assert.AreEqual(!defenseLowering, ACE.Server.Pvp.Rules.PvpArenaSpellRules.IsRefused(ACE.Entity.Enum.MagicSchool.CreatureEnchantment, category, 0, isFfa: false), $"Creature Enchantment {category}");
                Assert.IsFalse(ACE.Server.Pvp.Rules.PvpArenaSpellRules.IsRefused(ACE.Entity.Enum.MagicSchool.LifeMagic, category, 0, isFfa: false), $"Life {category} outside FFA");
            }
        }

        [TestMethod]
        public void Countdown_OneCountdownOneLive_DefenseLoweringVuln_Allows_BothDirections()
        {
            var match = NewMatch();
            var cd = Binding(match, 0, PvpMatchState.Countdown);
            var live = Binding(match, 1, PvpMatchState.Live);
            var school = ACE.Entity.Enum.MagicSchool.CreatureEnchantment;
            var cat = ACE.Entity.Enum.SpellCategory.MeleeDefenseLowering;

            Assert.AreEqual(PvpGateDecision.Allow, Decide(cd, live, school, cat));
            Assert.AreEqual(PvpGateDecision.Allow, Decide(live, cd, school, cat));
        }

        [TestMethod]
        public void Countdown_WarBolt_And_OtherHarmfulSpells_And_NullSpell_StayRefused()
        {
            var match = NewMatch();
            var a = Binding(match, 0, PvpMatchState.Countdown);
            var d = Binding(match, 1, PvpMatchState.Countdown);

            Assert.AreEqual(PvpGateDecision.Refuse, Decide(a, d, ACE.Entity.Enum.MagicSchool.WarMagic, ACE.Entity.Enum.SpellCategory.FireStreak), "a war bolt deals damage");
            Assert.AreEqual(PvpGateDecision.Refuse, Decide(a, d, ACE.Entity.Enum.MagicSchool.CreatureEnchantment, ACE.Entity.Enum.SpellCategory.StrengthLowering), "a non-defense debuff stays refused");
            Assert.AreEqual(PvpGateDecision.Refuse, Decide(a, d, ACE.Entity.Enum.MagicSchool.LifeMagic, ACE.Entity.Enum.SpellCategory.MeleeDefenseLowering), "the category alone is not enough, the school must be Creature Enchantment");
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(a, d, friendlyFireEnabled: false), "melee and missile (no spell) stay refused");
        }

        [TestMethod]
        public void Countdown_DefenseLoweringVuln_OnTeammate_StaysRefused_UnlessFriendlyFireIsOn()
        {
            var match = NewMatch();
            var a = Binding(match, 0, PvpMatchState.Countdown);
            var mate = Binding(match, 0, PvpMatchState.Countdown);

            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(a, mate, friendlyFireEnabled: false, countdownVuln: true));
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(a, mate, friendlyFireEnabled: true, countdownVuln: true), "same as Live: friendly fire on allows it");
        }

        [TestMethod]
        public void Vuln_BeforeCountdown_OrAcrossMatches_OrWithOneSideUnbound_StaysRefused()
        {
            var match = NewMatch();
            var other = NewMatch();

            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Binding(match, 0, PvpMatchState.Staging), Binding(match, 1, PvpMatchState.Countdown), false, true));
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Binding(match, 0, PvpMatchState.Resolving), Binding(match, 1, PvpMatchState.Live), false, true));
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Binding(match, 0, PvpMatchState.Countdown), Binding(other, 1, PvpMatchState.Countdown), false, true));
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(Binding(match, 0, PvpMatchState.Countdown), null, false, true));
        }

        [TestMethod]
        public void BothLive_Behaviour_IsUnchangedByTheVulnFlag()
        {
            var match = NewMatch();
            var a = Binding(match, 0, PvpMatchState.Live);
            var d = Binding(match, 1, PvpMatchState.Live);

            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(a, d, false, false));
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(a, d, false, true));
            Assert.AreEqual(PvpGateDecision.Refuse, PvpArenaGate.Evaluate(a, Binding(match, 0, PvpMatchState.Live), false, true), "teammate still refused when Live");
        }
    }
}
