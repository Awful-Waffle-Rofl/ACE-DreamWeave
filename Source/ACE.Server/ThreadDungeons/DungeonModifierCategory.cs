using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The three buckets every gem modifier falls into for the VAGUE fragment-side feedback (owner ruling,
    /// 2026-09-07). Before a press the player learns the CATEGORY of what a component is aiming at and
    /// nothing more; after the press the finished gem still lists the real modifiers with their real effects.
    /// </summary>
    public enum DungeonModifierCategory
    {
        /// <summary>A knob on the run's ordinary monsters. Also the fallback for anything unrecognised.</summary>
        Monster,

        /// <summary>A knob on the run's boss.</summary>
        Boss,

        /// <summary>A reward rather than a monster knob - experience, luminance, loot, salvage.</summary>
        Bonus,
    }

    /// <summary>
    /// Classifies a <see cref="ModifierDef"/> into a <see cref="DungeonModifierCategory"/>. One place, called
    /// by both vague surfaces - DungeonGemNarrator's chat load line and ThreadDungeonGemHandler's
    /// "Possible effects:" block - for the same reason TryDescribeEffect is one place: two switches on the
    /// same data drift apart silently, and here that drift would show up as the item description and the chat
    /// line disagreeing about what a component does.
    ///
    /// HOW THE THREE RULES WERE DECIDED, "bonus" being the fuzzy one (owner asked for this to be written
    /// down). Everything is derived from the modifier row itself - there is deliberately no hardcoded id list,
    /// so a new modifiers.json row is categorised the moment it is added rather than the next time someone
    /// remembers to update a list here.
    ///
    /// 1. BOSS is <c>Target == "boss"</c>, exactly as the owner specified and as DungeonRewardMath itself
    ///    splits the XP product (DungeonRewardMath.cs:98/102 filter on this same literal). Checked FIRST, so a
    ///    boss-target row is "boss" whatever else it carries.
    ///
    /// 2. BONUS is the row that does nothing to the monsters but does something for the player. Two ways in:
    ///    - the kind is one of the REWARD KINDS - <c>"salvage_affinity"</c> or <c>"loot_quantity"</c>.
    ///      Neither writes a creature knob (see ModifierDef's own remarks): salvage_affinity's magnitude is a
    ///      per-kill chance of an extra salvageable item, and loot_quantity's magnitude IS the multiplier on
    ///      how much a kill drops. Both are rewards, so both are named as one even though the kind is not
    ///      "none". This set is the ONE place that judgement is made; it is deliberately a set rather than a
    ///      chain of equality tests so a third reward kind is one entry, not a second switch.
    ///    - <c>monsterEffectKind == "none"</c> AND at least one reward field differs from its neutral default.
    ///      "None" means the row writes no creature knob; if it also moves no reward, the row does literally
    ///      nothing and there is no honest third thing to call it, so it falls through to "monster" rather
    ///      than being advertised as a bonus the player will not get.
    ///
    ///    WHICH FIELDS COUNT AS "a reward". The owner's wording was "a reward slope/base that differs from the
    ///    neutral default", which names the four XP/luminance fields, and those four are what decide every
    ///    SHIPPED row: radiant (rewardLumBase 0.0, rewardLumSlope 1.0) and enlightened (rewardXpBase 0.0,
    ///    rewardXpSlope 1.0) are the only two "none"-kind rows in Content/dungeons/dynamic/modifiers.json and
    ///    both qualify on those four alone. The two LOOT fields are included as well, and that inclusion
    ///    changes no shipped row - it exists so a future loot-only "none" row is not silently mis-sold to the
    ///    player as a monster modifier, which is the exact kind of lie the vague wording is meant to prevent.
    ///    The neutral values are ModifierDef's own property initialisers, not separately chosen numbers.
    ///
    /// 3. MONSTER is everything else, per the ruling - including a null def. A modifier id the store cannot
    ///    resolve has no data to classify from, and "monster" is the majority case and the least
    ///    over-promising of the three.
    /// </summary>
    public static class DungeonModifierCategories
    {
        /// <summary>ModifierDef.Target for a boss-only modifier. A literal here because it is one everywhere
        /// else in this subsystem too (DungeonRewardMath, DungeonGemRoller, ThreadDungeonStore's lint).</summary>
        private const string BossTarget = "boss";

        /// <summary>ModifierDef.MonsterEffectKind for a row that writes no creature knob.</summary>
        private const string NoMonsterEffect = "none";

        /// <summary>
        /// The monsterEffectKinds that are REWARDS rather than creature knobs, per rule 2 above. Both carry
        /// their meaning in the magnitude itself and neither is written onto a spawned creature.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> RewardKinds =
            new System.Collections.Generic.HashSet<string> { DungeonRewardMath.SalvageAffinity, DungeonRewardMath.LootQuantity };

        // The neutral defaults, mirrored from ModifierDef's property initialisers. A row equal to all of these
        // moves no reward at all.
        private const double NeutralRewardBase = 1.0;
        private const double NeutralRewardSlope = 0.0;
        private const double NeutralLootQuantityMult = 1.0;
        private const double NeutralLootQualityBonus = 0.0;

        public static DungeonModifierCategory Of(ModifierDef def)
        {
            if (def == null)
                return DungeonModifierCategory.Monster;

            if (def.Target == BossTarget)
                return DungeonModifierCategory.Boss;

            var kind = string.IsNullOrEmpty(def.MonsterEffectKind) ? NoMonsterEffect : def.MonsterEffectKind;

            if (RewardKinds.Contains(kind))
                return DungeonModifierCategory.Bonus;

            if (kind == NoMonsterEffect && MovesAReward(def))
                return DungeonModifierCategory.Bonus;

            return DungeonModifierCategory.Monster;
        }

        /// <summary>The word the player sees. Lower case, because every use site is mid-sentence.</summary>
        public static string Word(DungeonModifierCategory category)
        {
            switch (category)
            {
                case DungeonModifierCategory.Boss: return "boss";
                case DungeonModifierCategory.Bonus: return "bonus";
                default: return "monster";
            }
        }

        public static string Word(ModifierDef def) => Word(Of(def));

        /// <summary>
        /// THE ONE EXCEPTION TO THE VAGUE RULING (owner, 2026-09-07), and the only place it is decided. True
        /// for a salvage affinity and for nothing else: both vague surfaces may print such a modifier's
        /// <see cref="ModifierDef.Display"/> outright.
        ///
        /// WHY IT EXISTS, so nobody widens it by analogy or files it as a bug and narrows it. Every other
        /// component type is vague because knowing the exact modifier spoils the dungeon the player is about
        /// to face - that is what the ruling protects. A powder chooses a salvage MATERIAL, and the powder to
        /// material mapping is ARBITRARY by owner ruling: Powdered Azurite grants Tourmaline, Powdered Onyx
        /// grants Obsidian. Nothing in the game teaches that mapping, so a vague powder is not a choice at
        /// all - the player holds twelve identical-looking powders and cannot aim any of them. Naming the
        /// material restores the choice while revealing nothing about the run's difficulty, because a salvage
        /// affinity is a reward knob and touches no creature (see rule 2 above, and ModifierDef's remarks).
        ///
        /// SCOPE, stated so it cannot drift: the affinity's NAME only. No magnitude, no range and no number
        /// is printed for it, exactly as for every other type. Monster, boss and other bonus modifiers keep
        /// naming their category and nothing else.
        ///
        /// Keyed on the effect kind rather than on a list of affinity ids, for the same reason
        /// <see cref="RewardKinds"/> is a set: a new affinity row is nameable the moment it is added.
        /// </summary>
        public static bool IsNameable(ModifierDef def) => IsSalvageAffinity(def);

        /// <summary>
        /// Whether the row is a salvage affinity. The PRIMITIVE the naming exception and the store's own
        /// powder-slot lint rule are both defined in terms of, so the two cannot drift apart: the exception
        /// exists because a powder is the thing that chooses a material, and the lint rule is what makes that
        /// true of the content. RawFragmentRules.BudgetOf tests the same equality for the salvage budget and
        /// is deliberately left where it is - it is the pure core and does not depend on this file.
        /// </summary>
        public static bool IsSalvageAffinity(ModifierDef def)
            => def != null && def.MonsterEffectKind == DungeonRewardMath.SalvageAffinity;

        private static bool MovesAReward(ModifierDef def)
            => def.RewardXpBase != NeutralRewardBase
               || def.RewardXpSlope != NeutralRewardSlope
               || def.RewardLumBase != NeutralRewardBase
               || def.RewardLumSlope != NeutralRewardSlope
               || def.LootQuantityMult != NeutralLootQuantityMult
               || def.LootQualityBonus != NeutralLootQualityBonus;
    }
}
