using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// A stacking cast-speed buff for void (VoidMagic school) spells. Each void spell you cast adds a
    /// stack; the spell you are casting benefits from the stacks accumulated so far, so the ramp builds
    /// over successive void casts - the first void cast gets no bonus, each subsequent one is faster.
    /// Each stack shortens the cast by 5% / 10% / 15% per rank; stacks cap at <see cref="MaxStacks"/>
    /// and reset if you go class_ability_netherrush_expire_seconds without casting a void spell.
    ///
    /// This is a bespoke integration, not a hook: cast speed is otherwise a fixed constant
    /// (Player_Magic.CastSpeed), so the multiplier is applied in the cast path (DoWindupGestures /
    /// DoCastGesture) via Player.ApplyNetherRushCastSpeed. Stacks are transient runtime state
    /// (Player_ClassAbilityBuffs), never persisted. Carries the IPassiveStatAbility marker (Implemented,
    /// but hooks no combat site) like Battle Hardened.
    /// </summary>
    public class NetherRushAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        /// <summary>
        /// Design cap on accumulated stacks (fixed across ranks; rank scales the per-stack strength).
        /// </summary>
        public const int MaxStacks = 5;

        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.NetherRush,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 1,
            Name = "netherrush",
            DisplayName = "Nether Rush",
            Description = "Casting void spells builds Nether Rush, speeding your void casts by 5% / 10% / 15% per rank " +
                          "for each stack, up to 5 stacks. Stacks reset after 20 seconds without casting a void spell. " +
                          "Higher Arcane Lore increases the per-stack bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },   // Tier-1 GC: rank 1 always 1 point (the class's power splash)
            Implemented = true,
            AffinitySkill = Skill.ArcaneLore,
        };

        /// <summary>
        /// The cast-speed multiplier granted by a given number of active stacks at a given rank
        /// (1.0 = none). Per-stack strength is rank * percentPerRank (5% / 10% / 15%) plus the Arcane
        /// Lore rider fraction, additive on the same per-stack axis (0 reproduces pre-rider behavior
        /// exactly). Stacks are clamped to <see cref="MaxStacks"/> here so callers can't over-apply.
        /// </summary>
        public static float CastSpeedMultiplier(int stacks, int rank, double percentPerRank, double arcaneLoreFraction = 0.0)
        {
            var effective = Math.Clamp(stacks, 0, MaxStacks);
            var perStack = rank * percentPerRank + Math.Max(0.0, arcaneLoreFraction);
            return (float)(1.0 + effective * perStack);
        }

        /// <summary>
        /// Reports the PER-STACK rate (mirrors the perStack term in CastSpeedMultiplier above and
        /// Player.ApplyNetherRushCastSpeed - the two must stay in step), not a total-stacks bonus. No
        /// equipment mod exists for this ability (no EquipmentModId.NetherRush entry), so Gear is 0. The
        /// MaxStacks cap bounds the number of stacks, not the per-stack rate itself, so CapNote is never
        /// set here.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var percentPerRank = PropertyManager.GetDouble("class_ability_netherrush_percent_per_rank").Item;

            var skill = rank * percentPerRank * 100.0;

            var affinity = player.GetClassAbilityScaling(Skill.ArcaneLore,
                PropertyManager.GetDouble("class_ability_netherrush_arcanelore_per_trained").Item,
                PropertyManager.GetDouble("class_ability_netherrush_arcanelore_per_spec").Item) * 0.01 * 100.0;

            var total = skill + affinity;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = total,
                Unit = "%",
                Label = "cast speed",
                Per = "/stack",
                CapNote = null,
            };
        }
    }
}
