using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Result of evaluating whether a class ability token can be used right now, kept as a pure value so the
    /// decision (and its per-branch messaging) is unit-testable without a live Player - the same testability
    /// pattern as BattleHardenedAbility.GetDamageResistMod / ThornsAbility.ComputeReflectDamage.
    /// </summary>
    public enum ClassAbilityTokenOutcome
    {
        Ok,
        NotImplemented,
        AlreadyMaxRank,
        WrongTierOrder,
        InsufficientPoints,
    }

    public readonly struct ClassAbilityTokenEvaluation
    {
        public ClassAbilityTokenOutcome Outcome { get; init; }

        /// <summary>Point cost of the tier being learned - meaningful once the tier ordering is valid.</summary>
        public int Cost { get; init; }

        public bool CanLearn => Outcome == ClassAbilityTokenOutcome.Ok;
    }

    /// <summary>
    /// The catalog of buyable class ability tokens: which class abilities have tokens, the stable wcid assigned to
    /// each (skill, tier), and the Luminance price of each. The catalog is the single source of truth shared by
    /// the /abilities token buy|list command and the generated token weenie content - a self-consistency test
    /// asserts every offering maps to a real registry skill/tier so the two can't silently drift.
    ///
    /// wcids are assigned as WcidBase + skillIndex * WcidsPerAbility + (tier - 1): a fixed block of
    /// WcidsPerAbility (= max supported tiers) is reserved per skill so raising a skill's MaxRank later adds a
    /// token without renumbering any other skill's tokens.
    ///
    /// The token block is 1000500-1000999, reserved for this catalog alone. Do NOT move it without
    /// regenerating the token content: the original base of 1000060 collided head-on with the Unbidden Doors
    /// arc (the catalog would have minted the Driftwarden's Mark for a Multishot III token), and the interim
    /// content block at 1000200 was capped at 24 wcids by the Driftwarden set starting at 1000224 - too small
    /// for 6 wcids/skill, let alone the ~36-skill catalog the class/tier redesign plans
    /// (Docs/ClassAbilities/DESIGN.md §5). ClassAbilityTokenTests.Catalog_MatchesCommittedTokenContent pins the
    /// catalog to the committed Content/ files so constant and content can never drift apart again.
    ///
    /// WcidsPerAbility is 6: skills max at 3 ranks by design (DESIGN.md §5), so half of each skill's slot
    /// range is deliberate spare headroom - number space in the reserved block is free, renumbers are not.
    /// </summary>
    public static class ClassAbilityTokenCatalog
    {
        public const uint WcidBase = 1000500;
        public const int WcidsPerAbility = 6;

        /// <summary>
        /// The catalog outgrew its original 1000500-1000999 block at index 79 (Spellsword, the 8th class).
        /// Index 82 ends at 1000994, and index 83's tier-3 token would mint 1001000 - which is Cedric, the
        /// Archer trainer NPC. Rather than move WcidBase (which the class doc-comment above forbids, because
        /// it renumbers every committed token), indices from <see cref="OverflowStartIndex"/> onward are
        /// minted from a SECOND reserved block starting here. Every token at a lower index keeps the exact
        /// wcid it was committed with.
        ///
        /// 1002000-1002499 is reserved for this in Content/wcid-registry.tsv, opened above the repo-wide
        /// wcid high-water of 1001959 (blood-mage-trainers) with a deliberate gap - 1001960-1001999 stays
        /// free for the ordinary ten-wide unit blocks that allocation pattern hands out.
        /// </summary>
        public const uint WcidOverflowBase = 1002000;

        /// <summary>
        /// The first TokenAbilities index minted from <see cref="WcidOverflowBase"/>. NEVER lower this and
        /// never raise it: it is a pinned boundary, not a capacity figure. Lowering it renumbers committed
        /// tokens below the line; raising it mints a wcid past the end of the primary block.
        /// </summary>
        public const int OverflowStartIndex = 79;

        /// <summary>
        /// The class abilities that have trainer tokens, in a fixed order (index drives wcid assignment - never
        /// reorder; APPEND ONLY, so existing tokens keep their wcids). This is the full 64-skill catalog from
        /// SKILL-TABLES-PREVIEW: the six classes' unique-effect abilities, Training bundles, Rating skills, and
        /// the 18 homed "Enhanced &lt;stat&gt;" skills (the rest of the generated Enhanced family stays unhomed,
        /// AbilityClass.None, and is deliberately not offered). Enhanced entries reference their synthetic
        /// ClassAbilityId via the EnhancedStatAbility.ClassIdFor* helpers. The original 8 stay first to preserve
        /// their committed wcids (1000500-1000544); the rest are grouped by class for readability.
        /// </summary>
        public static readonly IReadOnlyList<ClassAbilityId> TokenAbilities = new[]
        {
            // original 8 (wcid-stable, do not reorder)
            ClassAbilityId.Multishot,
            ClassAbilityId.Thorns,
            ClassAbilityId.Taunt,
            ClassAbilityId.PoisonWeapon,
            ClassAbilityId.SpellAoe,
            ClassAbilityId.BattleHardened,
            ClassAbilityId.Frenzy,
            ClassAbilityId.NetherRush,
            // Archer
            ClassAbilityId.ArcherTraining,
            EnhancedStatAbility.ClassIdForSkill(Skill.MissileWeapons),
            EnhancedStatAbility.ClassIdForAttribute(PropertyAttribute.Coordination),
            ClassAbilityId.Deadeye,
            ClassAbilityId.EagleEye,
            ClassAbilityId.HeavyDraw,
            EnhancedStatAbility.ClassIdForSkill(Skill.MissileDefense),
            ClassAbilityId.CritRating,
            ClassAbilityId.DoubleVolley,
            ClassAbilityId.LongDraw,
            // Rogue
            ClassAbilityId.RogueTraining,
            EnhancedStatAbility.ClassIdForSkill(Skill.FinesseWeapons),
            EnhancedStatAbility.ClassIdForAttribute(PropertyAttribute.Quickness),
            ClassAbilityId.QuestionableTactics,
            ClassAbilityId.Parry,
            ClassAbilityId.Riposte,
            EnhancedStatAbility.ClassIdForSkill(Skill.MeleeDefense),
            ClassAbilityId.CritDamageRating,
            ClassAbilityId.AttackSpeed,
            ClassAbilityId.AcidProc,
            // Vanguard
            ClassAbilityId.VanguardTraining,
            ClassAbilityId.AdvancedWeaponry,
            EnhancedStatAbility.ClassIdForAttribute(PropertyAttribute.Endurance),
            ClassAbilityId.DamageResistRating,
            ClassAbilityId.ShieldBlock,
            ClassAbilityId.ShieldCheck,
            ClassAbilityId.CritResistRating,
            EnhancedStatAbility.ClassIdForVital(PropertyAttribute2nd.MaxHealth),
            // Berserker
            ClassAbilityId.BerserkerTraining,
            EnhancedStatAbility.ClassIdForSkill(Skill.TwoHandedCombat),
            EnhancedStatAbility.ClassIdForAttribute(PropertyAttribute.Strength),
            ClassAbilityId.SavageBlows,
            ClassAbilityId.BloodFury,     // RETIRED 2026-08-17 (Berserker/Rogue balance pass): slot RESERVED
                                          // at index 40 so every later token wcid stays stable; it is
                                          // unregistered, so the offering generators below skip it.
            ClassAbilityId.Executioner,
            EnhancedStatAbility.ClassIdForVital(PropertyAttribute2nd.MaxStamina),
            ClassAbilityId.DamageRating,
            ClassAbilityId.Whirlwind,
            ClassAbilityId.Bloodlust,
            // Archmage
            ClassAbilityId.ArchmageTraining,
            EnhancedStatAbility.ClassIdForSkill(Skill.WarMagic),
            EnhancedStatAbility.ClassIdForAttribute(PropertyAttribute.Focus),
            ClassAbilityId.FlatCastSpeed,
            ClassAbilityId.Overchannel,
            EnhancedStatAbility.ClassIdForVital(PropertyAttribute2nd.MaxMana),
            ClassAbilityId.EchoCast,
            ClassAbilityId.ElementalRend,
            EnhancedStatAbility.ClassIdForSkill(Skill.LifeMagic),
            // VoidSummon
            ClassAbilityId.VoidTraining,
            EnhancedStatAbility.ClassIdForSkill(Skill.VoidMagic),
            EnhancedStatAbility.ClassIdForAttribute(PropertyAttribute.Self),
            ClassAbilityId.StreakToArc,   // RETIRED 2026-07-18 (Arc/Streak ~2.0x = +100%, too strong):
                                          // slot RESERVED so later Void token wcids stay stable; it is
                                          // unregistered, so the offering generators below skip it.
            ClassAbilityId.EmpoweredSummons,
            EnhancedStatAbility.ClassIdForSkill(Skill.Summoning),
            ClassAbilityId.Summon2x,
            ClassAbilityId.VoidDamage,
            ClassAbilityId.Withering,
            // Tier-2 additions 2026-07-30. APPENDED, not filed under their classes above, because the index
            // drives the wcid - inserting them next to their classmates would renumber every token after.
            ClassAbilityId.ManaBarrier,    // Archmage
            ClassAbilityId.NetherBloom,    // VoidSummon
            ClassAbilityId.SoulTether,     // VoidSummon
            // Phase 1 skill redistribution 2026-08-03 - appended so existing token wcids are unchanged.
            EnhancedStatAbility.ClassIdForSkill(Skill.HeavyWeapons),   // Vanguard T1 (replaces Advanced Weaponry's heavy half)
            EnhancedStatAbility.ClassIdForSkill(Skill.MagicDefense),   // Archmage T2

            // Blood Mage (7th class), registered 2026-08-03. APPENDED at the end for the same reason as the
            // Tier-2 additions above - never filed next to a "class block" that doesn't exist yet at this
            // index. Ten of the eleven Blood Mage abilities are listed here; the eleventh, Enhanced Life
            // Magic, already has a slot earlier in this array (EnhancedStatAbility.ClassIdForSkill(Skill.
            // LifeMagic), in the Archmage group above) - it moved CLASS membership to BloodMage via
            // EnhancedStatAbility.Homing, not array position, so its committed wcid (1000824-1000826) is
            // untouched by this append. Filed AFTER the Phase 1 HeavyWeapons/MagicDefense pair above: both
            // additions landed the same day and independently claimed this same slot, colliding on wcids
            // 1000902-1000910; Phase 1 shipped first on master, so Blood Mage yielded and moved here
            // (wcids 1000914-1000970, was 1000902-1000958).
            ClassAbilityId.SanguineReserve,
            ClassAbilityId.Transfusion,
            ClassAbilityId.BloodMageTraining,
            ClassAbilityId.WeakenedBlood,
            ClassAbilityId.Malediction,
            ClassAbilityId.CrimsonHarvest,
            ClassAbilityId.HealBoostRating,
            ClassAbilityId.Exsanguinate,
            ClassAbilityId.BloodPrice,
            ClassAbilityId.SanguineWard,

            // Spellsword (8th class), 2026-08-03. THE FIRST ENTRIES TO CROSS INTO THE OVERFLOW BLOCK -
            // see WcidOverflowBase. Index 79 is where 1000500-1000999 runs out: index 82 ends at 1000994
            // and index 83's tier-3 token would mint 1001000, which is Cedric, the Archer trainer NPC.
            // Ten of the eleven Spellsword abilities are listed here; the eleventh, Enhanced Melee Defense,
            // already has a slot earlier in this array (in the Rogue group), and moved CLASS membership to
            // Spellsword via EnhancedStatAbility.Homing rather than array position, so its committed wcid
            // (1000644-1000646) is untouched by this append.
            ClassAbilityId.Spellblade,
            ClassAbilityId.Resonance,
            ClassAbilityId.SpellswordTraining,
            EnhancedStatAbility.ClassIdForSkill(Skill.LightWeapons),   // Spellsword T1
            ClassAbilityId.Runeblade,
            ClassAbilityId.Sundermark,
            ClassAbilityId.Spellsurge,
            ClassAbilityId.Spellstorm,
            ClassAbilityId.Cascade,
            ClassAbilityId.DispellingEdge,

            // Berserker + Rogue balance pass, 2026-08-17 (Docs/ClassAbilities/BERSERKER-ROGUE-BALANCE-PLAN.md).
            // APPENDED, not filed under their classes above, for the same reason every prior addition was:
            // the index drives the wcid. Break Armor fills Blood Fury's retired slot's REPLACEMENT (not its
            // index - Blood Fury's index 40 stays reserved), Surefooted and Pocket Sand are new Rogue T2/T3
            // abilities. Indices 89/90/91, minted from the overflow block (index >= OverflowStartIndex): wcids
            // 1002060-62, 1002066-68, 1002072-74.
            ClassAbilityId.BreakArmor,
            ClassAbilityId.Surefooted,
            ClassAbilityId.PocketSand,
        };

        public readonly struct Offering
        {
            public ClassAbilityId SkillId { get; init; }
            public int Tier { get; init; }
            public uint Wcid { get; init; }
            public ClassAbilityDefinition Definition { get; init; }
        }

        private static int SkillIndex(ClassAbilityId skill)
        {
            for (var i = 0; i < TokenAbilities.Count; i++)
                if (TokenAbilities[i] == skill)
                    return i;
            return -1;
        }

        /// <summary>
        /// The token wcid for a (skill, tier), from whichever reserved block that skill's catalog index
        /// falls in. Both blocks use the same fixed WcidsPerAbility stride, so the only difference is the
        /// base and the index offset - see <see cref="WcidOverflowBase"/> for why there are two.
        /// </summary>
        public static uint WcidFor(ClassAbilityId skill, int tier)
        {
            var index = SkillIndex(skill);

            if (index < OverflowStartIndex)
                return WcidBase + (uint)(index * WcidsPerAbility) + (uint)(tier - 1);

            return WcidOverflowBase + (uint)((index - OverflowStartIndex) * WcidsPerAbility) + (uint)(tier - 1);
        }

        /// <summary>Every (skill, tier) token offering, in catalog order (skill order, then ascending tier).</summary>
        public static IEnumerable<Offering> AllOfferings()
        {
            foreach (var skillId in TokenAbilities)
            {
                // Retired abilities keep their slot (wcid stability) but are unregistered - skip them.
                if (!ClassAbilityRegistry.Abilities.TryGetValue(skillId, out var def))
                    continue;
                for (var tier = 1; tier <= def.MaxRank; tier++)
                    yield return new Offering { SkillId = skillId, Tier = tier, Wcid = WcidFor(skillId, tier), Definition = def };
            }
        }

        public static bool TryResolve(ClassAbilityId skill, int tier, out Offering offering)
        {
            offering = default;
            if (SkillIndex(skill) < 0)
                return false;

            // Retired (unregistered) abilities hold a reserved slot but resolve to no token.
            if (!ClassAbilityRegistry.Abilities.TryGetValue(skill, out var def))
                return false;
            if (tier < 1 || tier > def.MaxRank)
                return false;

            offering = new Offering { SkillId = skill, Tier = tier, Wcid = WcidFor(skill, tier), Definition = def };
            return true;
        }

        /// <summary>Luminance price of a token = the point cost of its rank * a server-tunable per-point rate.</summary>
        public static long LumCost(ClassAbilityDefinition def, int tier) =>
            def.CostPerRank[tier - 1] * PropertyManager.GetLong("class_ability_token_lum_per_point").Item;

        /// <summary>
        /// Pure decision for whether the given token can be learned now. Does not check class_abilities_enabled
        /// (that gate lives at the call sites) or inventory - only the skill/rank/tier/points rules.
        /// </summary>
        public static ClassAbilityTokenEvaluation Evaluate(ClassAbilityDefinition skill, int currentRank, int tier, int availablePoints)
        {
            if (!skill.Implemented)
                return new ClassAbilityTokenEvaluation { Outcome = ClassAbilityTokenOutcome.NotImplemented };

            if (currentRank >= skill.MaxRank)
                return new ClassAbilityTokenEvaluation { Outcome = ClassAbilityTokenOutcome.AlreadyMaxRank };

            if (tier < 1 || tier > skill.MaxRank || tier != currentRank + 1)
                return new ClassAbilityTokenEvaluation { Outcome = ClassAbilityTokenOutcome.WrongTierOrder };

            var cost = skill.CostPerRank[currentRank];

            if (availablePoints < cost)
                return new ClassAbilityTokenEvaluation { Outcome = ClassAbilityTokenOutcome.InsufficientPoints, Cost = cost };

            return new ClassAbilityTokenEvaluation { Outcome = ClassAbilityTokenOutcome.Ok, Cost = cost };
        }
    }
}
