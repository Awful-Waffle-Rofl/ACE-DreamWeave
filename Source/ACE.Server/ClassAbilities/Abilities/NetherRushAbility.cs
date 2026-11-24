using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// MANA RUSH (displayed name since the 2026-10-24 Archmage pass; it was "Nether Rush" before). A stacking
    /// cast-speed buff for EVERY spell you cast, of any school. Each cast adds a stack; the spell you are
    /// casting benefits from the stacks accumulated so far, so the ramp builds over successive casts - the
    /// first cast gets no bonus, each subsequent one is faster. Each stack shortens the cast by 5% / 10% / 15%
    /// per rank (Loyalty multiplies the per-stack rate); stacks cap at <see cref="MaxStacks"/> and reset if
    /// you go class_ability_netherrush_expire_seconds without casting.
    ///
    /// THE INTERNAL NAME STAYS "netherrush". Ownership persists by <see cref="ClassAbilityDefinition.Name"/>
    /// (the quest-key suffix), the token files and the tunables are keyed by it too, so only the
    /// player-facing text was renamed. Do not "tidy" the Name, the enum member or the tunable keys.
    ///
    /// THE VOID-ONLY SCHOOL GATE CAME OFF on 2026-10-24 (owner ruling), and with it came a CAP: Mana Rush's
    /// own total bonus is clamped at class_ability_netherrush_max_bonus (0.50) times the Loyalty affinity
    /// multiplier, so a fully stacked rank 3 (5 x 15% = 75%) is held at +50% for a player with no Loyalty
    /// bonus. The cap scales with the same multiplier as the per-stack rate, so Loyalty still raises the
    /// ceiling as well as the speed of the ramp. See <see cref="CastSpeedMultiplier"/>.
    ///
    /// PvE ONLY: Player.ApplyNetherRushCastSpeed neither applies nor builds stacks for a harmful spell aimed
    /// at another player (PvpClassifier.IsPvp), and a suppressed player (PK facet, arena mask) reads rank 0.
    ///
    /// This is a bespoke integration, not a hook: cast speed is otherwise a fixed constant
    /// (Player_Magic.CastSpeed), so the multiplier is applied in the cast path (DoWindupGestures /
    /// DoCastGesture) via Player.ApplyClassAbilityCastSpeed. Stacks are transient runtime state
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
            Name = "netherrush",   // persisted quest-key suffix - NEVER rename (displays as "Mana Rush")
            DisplayName = "Mana Rush",
            DescriptionTemplate = "Casting spells from any school builds Mana Rush stacks that speed up your casting, up to 5 stacks " +
                          "and +{v:class_ability_netherrush_max_bonus|pct}% cast speed. Stacks reset after " +
                          "{v:class_ability_netherrush_expire_seconds|num} seconds without a cast.",
            // Live text: per-stack rate = rank * class_ability_netherrush_percent_per_rank (GetReadout below); the cap is
            // class_ability_netherrush_max_bonus (GetReadout below); the reset window is class_ability_netherrush_expire_seconds
            // (Player_ClassAbilityBuffs.cs:440). The "5% / 10% / 15%" ladder is {per:...;join=spaced;unit=%}. "up to 5 stacks" is NetherRushAbility.MaxStacks.
            RankTailTemplate = "+{per:class_ability_netherrush_percent_per_rank|pct}% cast speed per stack",
            TunableSourceFiles = new[] { "WorldObjects/Player_ClassAbilityBuffs.cs" },
            AllowedLiterals = new[]
            {
                ("up to 5 stacks", "the stack count is NetherRushAbility.MaxStacks, a code constant with no tunable"),
            },
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // flat 1/rank (class ability overhaul repricing, 2026-09-12)
            Implemented = true,
            AffinitySkill = Skill.Loyalty,   // moved off Arcane Lore in the 2026-09-12 affinity overhaul
            Ceilings = new[]
            {
                // the cap is class_ability_netherrush_max_bonus times the Loyalty multiplier (MaxBonus), so affinity raises this ceiling
                new ClassAbilityCeiling { Id = "total", Bounds = CeilingBounds.Total, Source = CeilingSource.Tunable, Key = "class_ability_netherrush_max_bonus", Label = "max cast speed bonus", Format = "pct", AffinityScalesMax = true, ZeroIsLimit = true },
                new ClassAbilityCeiling { Id = "stacks", Bounds = CeilingBounds.Stacks, Source = CeilingSource.Constant, ConstantValue = _ => MaxStacks, ConstantRef = "NetherRushAbility.MaxStacks", Label = "max stacks", Format = "int" },
            },
        };

        /// <summary>
        /// The cast-speed multiplier granted by a given number of active stacks at a given rank
        /// (1.0 = none). Per-stack strength is rank * percentPerRank (5% / 10% / 15%) plus the affinity
        /// ADDED amount, additive on the same per-stack axis (0 reproduces pre-affinity behavior
        /// exactly). Stacks are clamped to <see cref="MaxStacks"/> here so callers can't over-apply.
        ///
        /// <paramref name="maxBonus"/> caps the TOTAL bonus (stacks x per-stack), not the per-stack rate.
        /// The caller passes the already-affinity-scaled cap (class_ability_netherrush_max_bonus x the
        /// Loyalty multiplier). The default (no cap) exists so the bare ramp stays testable on its own.
        ///
        /// The affinity skill is LOYALTY as of the 2026-09-12 overhaul. This stays a plain ADDITIVE term on
        /// purpose: the caller does the multiplying and passes the difference, so rank*perRank + added
        /// is identical to rank*perRank*multiplier.
        /// </summary>
        public static float CastSpeedMultiplier(int stacks, int rank, double percentPerRank, double affinityAddedFraction = 0.0, double maxBonus = double.PositiveInfinity)
        {
            var effective = Math.Clamp(stacks, 0, MaxStacks);
            var perStack = rank * percentPerRank + Math.Max(0.0, affinityAddedFraction);
            var bonus = Math.Min(effective * perStack, Math.Max(0.0, maxBonus));
            return (float)(1.0 + bonus);
        }

        /// <summary>
        /// The affinity-scaled cap on Mana Rush's total bonus: class_ability_netherrush_max_bonus times the
        /// Loyalty affinity multiplier (1.0 at no Loyalty bonus). Pure.
        /// </summary>
        public static double MaxBonus(double baseMaxBonus, double affinityMultiplier)
        {
            return Math.Max(0.0, baseMaxBonus) * Math.Max(1.0, affinityMultiplier);
        }

        /// <summary>
        /// Reports the PER-STACK rate (mirrors the perStack term in CastSpeedMultiplier above and
        /// Player.ApplyNetherRushCastSpeed - the two must stay in step), not a total-stacks bonus. No
        /// equipment mod exists for this ability (no EquipmentModId.NetherRush entry), so Gear is 0.
        /// CapNote is set when a full 5-stack ramp would exceed the affinity-scaled maximum bonus.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank) =>
            BuildReadout(rank, player.GetClassAbilityAffinityMultiplier(Skill.Loyalty), player.GetNetherRushStacks());

        /// <summary>The pure core of <see cref="GetReadout"/>, taking the live affinity multiplier so the readings can be pinned with fixed inputs.</summary>
        public ClassAbilityReadout BuildReadout(int rank, double multiplier, int stacksHeld)
        {
            var percentPerRank = PropertyManager.GetDouble("class_ability_netherrush_percent_per_rank").Item;

            var rankBonus = rank * percentPerRank;

            // Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the displayed
            // terms stay in one unit and still sum to Effective.
            var skill = rankBonus * 100.0;

            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

            var total = skill + affinity;

            var cap = MaxBonus(PropertyManager.GetDouble("class_ability_netherrush_max_bonus").Item, multiplier);

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
                CapNote = MaxStacks * total / 100.0 > cap + 0.0000001 ? $"max +{Math.Round(cap * 100.0)}%" : null,
                Ceilings = new[]
                {
                    // Scaled reading (R16): Max = declared cap x max(1, multiplier) and Current = the full ramp's affinity-scaled bonus,
                    // the two values MaxBonus and CastSpeedMultiplier actually compare.
                    ClassAbilityCeilings.ReadScaled(Definition, "total", MaxStacks * total, rank, ClassAbilityText.Live, multiplier),
                    ClassAbilityCeilings.Read(Definition, "stacks", stacksHeld, rank, ClassAbilityText.Live),
                },
            };
        }
    }
}
