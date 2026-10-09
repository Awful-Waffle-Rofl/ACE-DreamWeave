using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The Doctide-ported arena spell rules (Docs/Pvp/DESIGN.md "Arena spell rules"): which harmful spells
    /// PvpArenaSpellRules.IsRefused refuses inside an arena match, and which arena DoT ticks
    /// PvpArenaSpellRules.ShouldSuppressDotTick deals no damage.
    /// </summary>
    [TestClass]
    public class PvpArenaSpellRulesTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ================= IsRefused: 1v1/2v2 (non-FFA) =================

        [TestMethod]
        public void CreatureEnchantment_AttributeDebuff_Refused_NonFfa()
        {
            Assert.IsTrue(PvpArenaSpellRules.IsRefused(MagicSchool.CreatureEnchantment, SpellCategory.StrengthLowering, spellId: 1, isFfa: false));
        }

        [TestMethod]
        public void CreatureEnchantment_MagicDefenseLowering_Allowed_NonFfa()
        {
            Assert.IsFalse(PvpArenaSpellRules.IsRefused(MagicSchool.CreatureEnchantment, SpellCategory.MagicDefenseLowering, spellId: 1, isFfa: false));
        }

        [TestMethod]
        public void CreatureEnchantment_MeleeDefenseLowering_Allowed_NonFfa()
        {
            Assert.IsFalse(PvpArenaSpellRules.IsRefused(MagicSchool.CreatureEnchantment, SpellCategory.MeleeDefenseLowering, spellId: 1, isFfa: false));
        }

        [TestMethod]
        public void CreatureEnchantment_MissileDefenseLowering_Allowed_NonFfa()
        {
            Assert.IsFalse(PvpArenaSpellRules.IsRefused(MagicSchool.CreatureEnchantment, SpellCategory.MissileDefenseLowering, spellId: 1, isFfa: false));
        }

        [TestMethod]
        public void ItemEnchantment_Harmful_Refused_NonFfa()
        {
            Assert.IsTrue(PvpArenaSpellRules.IsRefused(MagicSchool.ItemEnchantment, SpellCategory.Undef, spellId: 1, isFfa: false));
        }

        [TestMethod]
        public void LifeMagic_Vulnerability_Allowed_InOneVOne()
        {
            Assert.IsFalse(PvpArenaSpellRules.IsRefused(MagicSchool.LifeMagic, SpellCategory.Undef, spellId: 1, isFfa: false));
        }

        [TestMethod]
        public void WarMagic_Allowed_NonFfa()
        {
            Assert.IsFalse(PvpArenaSpellRules.IsRefused(MagicSchool.WarMagic, SpellCategory.Undef, spellId: 1, isFfa: false));
        }

        // ================= IsRefused: FFA =================

        [TestMethod]
        public void Ffa_LifeMagicSpell_Refused()
        {
            Assert.IsTrue(PvpArenaSpellRules.IsRefused(MagicSchool.LifeMagic, SpellCategory.Undef, spellId: (uint)SpellId.VulnerabilityOther1, isFfa: true));
        }

        /// <summary>
        /// CurseRavenFury is verified Life Magic / HealthLowering against the client dat (Docs/Pvp/DESIGN.md
        /// "Arena spell rules"), so this is its real school/category, not an arbitrary pick - and it matters:
        /// IsRefused checks the Creature/Item Enchantment branch BEFORE the FFA exception (Doctide's own
        /// order), so a Creature Enchantment CurseRavenFury would never reach the exception at all. Life Magic
        /// skips that branch, so the exception gets to run.
        /// </summary>
        [TestMethod]
        public void Ffa_CurseRavenFury_Allowed()
        {
            Assert.IsFalse(PvpArenaSpellRules.IsRefused(MagicSchool.LifeMagic, SpellCategory.HealthLowering, spellId: (uint)SpellId.CurseRavenFury, isFfa: true));
        }

        [TestMethod]
        public void Ffa_DefenseLoweringSpell_StillRefused()
        {
            // Unlike 1v1/2v2, FFA refuses every harmful spell but CurseRavenFury - the defense-lowering
            // carve-out does not survive into FFA (it is caught by the FFA branch, since the Creature
            // Enchantment branch ahead of it explicitly exempts the three defense-lowering categories).
            Assert.IsTrue(PvpArenaSpellRules.IsRefused(MagicSchool.CreatureEnchantment, SpellCategory.MagicDefenseLowering, spellId: 1, isFfa: true));
        }

        /// <summary>
        /// The scenario DESIGN.md calls out explicitly: had CurseRavenFury instead been a non-defense-lowering
        /// Creature Enchantment spell, the Creature/Item Enchantment branch would refuse it before the FFA
        /// exception is ever consulted - so FFA would then allow no harmful spells at all. This is Doctide's
        /// real control-flow order, not a hypothetical this codebase invented.
        /// </summary>
        [TestMethod]
        public void Ffa_HadCurseRavenFuryBeenCreatureEnchantment_WouldStillBeRefused()
        {
            Assert.IsTrue(PvpArenaSpellRules.IsRefused(MagicSchool.CreatureEnchantment, SpellCategory.Undef, spellId: (uint)SpellId.CurseRavenFury, isFfa: true));
        }

        // ================= ShouldSuppressDotTick =================

        private static PvpMatch NewMatch(string modeKey = "1v1") => new PvpMatch(Guid.NewGuid(), modeKey, new List<PvpTeam>(), Now);

        private static PvpPlayerBinding Binding(PvpMatch match, int teamIndex, PvpMatchState state) =>
            new PvpPlayerBinding(match, teamIndex, state, true, true, true, true, true);

        [TestMethod]
        public void DotTick_SameMatch_Live_OneVOne_NotSuppressed()
        {
            var match = NewMatch(ArenaMapCatalog.OneVOneKey);
            var target = Binding(match, 0, PvpMatchState.Live);
            var damager = Binding(match, 1, PvpMatchState.Live);

            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(target, damager));
        }

        [TestMethod]
        public void DotTick_SameMatch_NotLive_OneVOne_Suppressed()
        {
            var match = NewMatch(ArenaMapCatalog.OneVOneKey);
            var target = Binding(match, 0, PvpMatchState.Resolving);
            var damager = Binding(match, 1, PvpMatchState.Resolving);

            Assert.IsTrue(PvpArenaSpellRules.ShouldSuppressDotTick(target, damager));
        }

        [TestMethod]
        public void DotTick_SameMatch_Live_Ffa_StillSuppressed()
        {
            var match = NewMatch(ArenaMapCatalog.FfaKey);
            var target = Binding(match, 0, PvpMatchState.Live);
            var damager = Binding(match, 1, PvpMatchState.Live);

            Assert.IsTrue(PvpArenaSpellRules.ShouldSuppressDotTick(target, damager));
        }

        [TestMethod]
        public void DotTick_DifferentMatches_NotSuppressed()
        {
            var matchA = NewMatch(ArenaMapCatalog.OneVOneKey);
            var matchB = NewMatch(ArenaMapCatalog.OneVOneKey);
            var target = Binding(matchA, 0, PvpMatchState.Live);
            var damager = Binding(matchB, 1, PvpMatchState.Live);

            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(target, damager));
        }

        [TestMethod]
        public void DotTick_EitherSideUnbound_NotSuppressed()
        {
            var match = NewMatch(ArenaMapCatalog.OneVOneKey);
            var target = Binding(match, 0, PvpMatchState.Live);

            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(target, null));
            Assert.IsFalse(PvpArenaSpellRules.ShouldSuppressDotTick(null, target));
        }
    }
}
